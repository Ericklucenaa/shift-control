using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using ShiftControl.Core;
using ShiftControl.Gaming;
using ShiftControl.Monitoring;
using ShiftControl.Overlay;
using ShiftControl.Storage;
using Windows.Graphics;

namespace ShiftControl.App.Overlay;

/// <summary>
/// Orquestra o overlay de jogos: captura de frames (só enquanto o overlay está ativo), escolha do jogo,
/// ping (só se uma métrica de rede estiver visível), janela, atalho global, posicionamento e gravação de sessões.
/// Roda na thread da interface; a captura e o ping têm threads próprias.
/// </summary>
public sealed class OverlayService : IDisposable
{
    private const string SettingsKey = "overlay";

    private readonly MonitoringEngine _engine;
    private readonly ISettingsStore _store;
    private readonly GameSessionStore _sessions;
    private readonly ILogger<OverlayService> _log;
    private readonly PresentMonProvider _provider;
    private readonly LatencyProbe _latency;
    private readonly OverlayFrame _frame = new();
    private DispatcherQueue? _ui;
    private DispatcherQueueTimer? _renderTimer;
    private DispatcherQueueTimer? _detectTimer;
    private OverlayHost? _host;
    private OverlayPanel? _panel;
    private SessionAggregator? _session;
    private int _previewSubscribers;
    private string? _appliedPresetName;
    private bool _disposed;

    public OverlayService(MonitoringEngine engine, ISettingsStore store, GameSessionStore sessions,
        ILogger<OverlayService> log, ILogger<PresentMonProvider> captureLog, ILogger<LatencyProbe> latencyLog)
    {
        _engine = engine;
        _store = store;
        _sessions = sessions;
        _log = log;
        _provider = new PresentMonProvider(Path.Combine(AppContext.BaseDirectory, "Tools", "PresentMon.exe"), captureLog);
        _latency = new LatencyProbe(latencyLog);
        Settings = (store.LoadSection<OverlaySettings>(SettingsKey) ?? new OverlaySettings()).Normalize();
        Access = CapturePermission.Current();
    }

    public OverlaySettings Settings { get; private set; }
    public FrameRouter Router { get; } = new();
    public FrameCaptureState CaptureState => _provider.State;
    public string? CaptureDetail => _provider.Detail;
    public CaptureAccess Access { get; private set; }
    public PresentingProcess? Target { get; private set; }
    public string? TargetName { get; private set; }
    public DateTimeOffset? SessionStart { get; private set; }
    public bool UserVisible { get; private set; } = true;
    public bool EditMode { get; private set; }
    public bool IsOverlayShown => _host?.IsVisible == true;
    public bool HotkeyRegistered { get; private set; }
    public long SessionsSaved { get; private set; }

    /// <summary>Perfil do Game Boost ativo (definido pelo GameBoostService), gravado na sessão para comparar antes/depois.</summary>
    public string? ActiveBoost { get; set; }

    /// <summary>Preset em uso agora (o do jogo detectado, se houver um associado).</summary>
    public OverlayPreset ActivePreset => Settings.ForGame(Target?.Executable);

    /// <summary>Estado mudou (captura, jogo, visibilidade). Sempre na thread da interface.</summary>
    public event EventHandler? Changed;

    /// <summary>Novo quadro de valores, para a prévia na tela de configuração.</summary>
    public event EventHandler<OverlayFrame>? Rendered;

    /// <summary>Uma sessão foi gravada.</summary>
    public event EventHandler<GameSessionRecord>? SessionSaved;

    public void Initialize(DispatcherQueue ui)
    {
        _ui = ui;
        _provider.Present += (in PresentRecord r) => Router.OnPresent(in r);
        _provider.StateChanged += (_, state) => _ui.TryEnqueue(() =>
        {
            if (state == FrameCaptureState.PermissionDenied) Access = CapturePermission.Current();
            RaiseChanged();
        });

        _renderTimer = ui.CreateTimer();
        _renderTimer.Tick += (_, _) => Render();
        _detectTimer = ui.CreateTimer();
        _detectTimer.Interval = TimeSpan.FromSeconds(1);
        _detectTimer.Tick += (_, _) => Detect();
        Apply();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    #region Configuração

    public void UpdateSettings(Action<OverlaySettings> change)
    {
        var next = Settings.Clone();
        change(next);
        next.Normalize();
        Settings = next;
        try { _store.SaveSection(SettingsKey, next); }
        catch (Exception ex) { _log.LogError(ex, "Não foi possível gravar as configurações do overlay."); }
        _appliedPresetName = null; // força reaplicar o visual
        Apply();
    }

    /// <summary>Substitui todas as configurações (restauração do pacote de migração).</summary>
    public void ReplaceSettings(OverlaySettings settings)
    {
        Settings = settings.Normalize();
        try { _store.SaveSection(SettingsKey, Settings); }
        catch (Exception ex) { _log.LogError(ex, "Não foi possível gravar as configurações do overlay."); }
        _appliedPresetName = null;
        Apply();
    }

    /// <summary>Altera um preset pelo nome (o ativo, por padrão).</summary>
    public void UpdatePreset(Action<OverlayPreset> change, string? name = null)
    {
        UpdateSettings(s =>
        {
            var preset = s.Presets.FirstOrDefault(p => p.Name == (name ?? s.ActivePreset));
            if (preset is not null) change(preset);
        });
    }

    #endregion

    #region Ciclo de vida

    private void Apply()
    {
        if (_ui is null || _disposed) return;
        bool enabled = Settings.Enabled;

        if (enabled)
        {
            EnsureHost();
            RegisterHotkey();
            if (_provider.State is FrameCaptureState.Stopped or FrameCaptureState.Faulted)
                _provider.Start();
            _detectTimer!.Start();
        }
        else
        {
            EndSession();
            _provider.Stop();
            _detectTimer?.Stop();
            Router.Clear();
            Target = null;
            TargetName = null;
            EditMode = false;
            _host?.UnregisterHotkey();
            HotkeyRegistered = false;
        }

        var preset = ActivePreset;
        if (_appliedPresetName != preset.Name || _panel is not null && !ReferenceEquals(_panel.Preset, preset))
        {
            _panel?.Apply(preset);
            _appliedPresetName = preset.Name;
        }

        // Ping só quando há métrica de rede visível no overlay ativo.
        bool needLatency = enabled && preset.VisibleItems.Any(i => OverlayCatalog.SourceOf(i.Metric) == OverlaySource.Network);
        if (needLatency) _latency.Start(Settings.PingHost);
        else _latency.Stop();

        _renderTimer!.Interval = TimeSpan.FromMilliseconds(preset.UpdateIntervalMs);
        if (enabled || _previewSubscribers > 0) _renderTimer.Start();
        else _renderTimer.Stop();

        UpdateVisibility();
        RaiseChanged();
    }

    private void EnsureHost()
    {
        if (_host is not null && !_host.IsClosed) return;
        _panel = new OverlayPanel();
        _panel.Apply(ActivePreset);
        _appliedPresetName = ActivePreset.Name;
        _host = new OverlayHost(_panel);
        _host.HotkeyPressed += (_, _) => ToggleVisible();
        _host.Moved += (_, pos) => OnDragged(pos);
        _panel.SizeChanged += (_, _) => PositionHost();
    }

    private void RegisterHotkey()
    {
        if (_host is null) return;
        HotkeyRegistered = Hotkeys.TryParse(Settings.Hotkey, out var hk) && _host.RegisterHotkey(hk);
        if (!HotkeyRegistered) _log.LogWarning("Não foi possível registrar o atalho {Hotkey} (em uso por outro programa?).", Settings.Hotkey);
    }

    /// <summary>Atalho Ctrl+Shift+O (padrão): mostra/oculta sem desligar a captura.</summary>
    public void ToggleVisible()
    {
        UserVisible = !UserVisible;
        UpdateVisibility();
        RaiseChanged();
    }

    public void SetEditMode(bool edit)
    {
        if (!Settings.Enabled) return;
        EditMode = edit;
        if (_host is not null) _host.ClickThrough = !edit;
        if (_panel is not null) _panel.EditMode = edit;
        if (edit) UserVisible = true;
        UpdateVisibility();
        RaiseChanged();
    }

    private void UpdateVisibility()
    {
        bool show = Settings.Enabled && UserVisible && (EditMode || !Settings.OnlyInGame || Target is not null);
        _engine.FullRateRequested = show;
        if (_host is null) return;
        if (show)
        {
            PositionHost();
            if (!_host.IsVisible) _host.Show();
        }
        else if (_host.IsVisible) _host.Hide();
    }

    #endregion

    #region Detecção do jogo e sessão

    private void Detect()
    {
        var presenting = Router.Presenting();
        int? currentPid = Target?.ProcessId;
        var decision = GameTargetPolicy.Decide(new TargetContext(
            GameProcesses.ForegroundProcessId(),
            presenting,
            Settings.ManualExecutable,
            Settings.AutoDetect,
            currentPid,
            currentPid is { } pid && GameProcesses.IsAlive(pid),
            Settings.Exclusions));

        if (decision.Target?.ProcessId != currentPid) SwitchTarget(decision.Target);
        else Target = decision.Target; // atualiza FPS/modo de apresentação

        if (_session is not null && Target is not null)
        {
            var s = _engine.Latest;
            var gpu = s?.PrimaryGpu;
            _session.ObserveBoost(ActiveBoost);
            _session.AddSecond(DateTimeOffset.Now, Router.Statistics.Snapshot().InstantFps,
                s?.Cpu.Load.Value, gpu?.Load.Value, s?.Cpu.Temperature.Value, gpu?.Temperature.Value, gpu?.VramUsedMb.Value,
                s?.Cpu.Clock.Value, gpu?.CoreClock.Value, gpu?.Power.Value);
        }
        RaiseChanged();
    }

    private void SwitchTarget(PresentingProcess? next)
    {
        EndSession();
        Router.SetTarget(next?.ProcessId);
        Target = next;
        if (next is null)
        {
            TargetName = null;
            SessionStart = null;
            _log.LogInformation("Nenhum jogo em execução detectado.");
        }
        else
        {
            TargetName = GameProcesses.DisplayName(next.ProcessId, next.Executable);
            SessionStart = DateTimeOffset.Now;
            if (Settings.RecordSessions)
                _session = new SessionAggregator(GameTargetPolicy.Normalize(next.Executable), TargetName, SessionStart.Value);
            _log.LogInformation("Jogo detectado: {Exe} (PID {Pid}, {Runtime}, {Mode}).", next.Executable, next.ProcessId, next.Runtime, next.PresentMode);
        }
        Apply(); // preset por jogo, ping, visibilidade
    }

    /// <summary>Fecha a sessão em andamento e grava se tiver duração e frames suficientes.</summary>
    private void EndSession()
    {
        var session = _session;
        _session = null;
        if (session is null) return;
        var end = DateTimeOffset.Now;
        var frames = Router.Statistics.Snapshot();
        if (!session.IsWorthSaving(end, frames))
        {
            _log.LogInformation("Sessão de {Exe} curta demais para gravar ({Seconds:F0} s, {Frames} frames).",
                session.Executable, (end - session.Start).TotalSeconds, frames.Frames);
            return;
        }
        var record = session.Complete(end, frames, Router.TargetRuntime);
        try
        {
            long id = _sessions.Save(record, session.Points);
            SessionsSaved++;
            _log.LogInformation("Sessão gravada: {Exe}, {Minutes:F1} min, FPS médio {Fps:F1}.", record.Executable, record.Duration.TotalMinutes, record.AverageFps);
            SessionSaved?.Invoke(this, record with { Id = id });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao gravar a sessão de {Exe}.", record.Executable);
        }
    }

    #endregion

    #region Desenho e posição

    public void AttachPreview()
    {
        _previewSubscribers++;
        Apply();
    }

    public void DetachPreview()
    {
        _previewSubscribers = Math.Max(0, _previewSubscribers - 1);
        Apply();
    }

    private void Render()
    {
        bool hasTarget = Target is not null;
        var frames = hasTarget ? Router.Statistics.Snapshot() : FrameStatsSnapshot.Empty;
        bool capturing = _provider.State == FrameCaptureState.Running && hasTarget;
        OverlayComposer.Compose(_frame, _engine.Latest, frames, capturing ? Router.Statistics : null, capturing,
            _latency.IsRunning ? _latency.Stats.Snapshot(_latency.Host) : null,
            TargetName, SessionStart is { } start ? DateTimeOffset.Now - start : null);

        if (_host?.IsVisible == true) _panel?.Update(_frame);
        Rendered?.Invoke(this, _frame);
    }

    public static IReadOnlyList<DisplayArea> Displays()
    {
        try { return DisplayArea.FindAll().ToList(); }
        catch (Exception) { return [DisplayArea.Primary]; }
    }

    private static DisplayArea DisplayFor(int index)
    {
        var displays = Displays();
        return index >= 0 && index < displays.Count ? displays[index] : DisplayArea.Primary;
    }

    /// <summary>
    /// Tela de um monitor em pixels independentes de DPI (o que o usuário vê como "1920×1080" a 150% vira 1280×720),
    /// com a escala de DPI daquele monitor. Usado pela janela real e pela miniatura da prévia.
    /// </summary>
    public static (ScreenRect Dip, double Scale, int PixelWidth, int PixelHeight) ScreenOf(int monitorIndex)
    {
        var b = DisplayFor(monitorIndex).OuterBounds;
        double scale = Win32.ScaleForPoint(b.X + b.Width / 2, b.Y + b.Height / 2);
        return (new ScreenRect(0, 0, b.Width / scale, b.Height / scale), scale, b.Width, b.Height);
    }

    private void PositionHost()
    {
        if (_host is null || _panel is null) return;
        var preset = ActivePreset;
        var bounds = DisplayFor(preset.MonitorIndex).OuterBounds; // tela inteira: jogos sem bordas cobrem a barra de tarefas
        double scale = Win32.ScaleForPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

        _panel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = _panel.DesiredSize.Width * scale + 2;
        double h = _panel.DesiredSize.Height * scale + 2;
        var (x, y) = OverlayPlacement.Compute(new ScreenRect(bounds.X, bounds.Y, bounds.Width, bounds.Height), w, h,
            preset.Anchor, preset.OffsetX * scale, preset.OffsetY * scale);
        _host.MoveAndResize(new RectInt32((int)Math.Round(x), (int)Math.Round(y), Math.Max((int)Math.Ceiling(w), 40), Math.Max((int)Math.Ceiling(h), 20)));
    }

    /// <summary>Posição escolhida na prévia (âncora clicada ou arraste livre), em DIPs do monitor do preset.</summary>
    public void SetPosition(OverlayAnchor anchor, double? offsetX = null, double? offsetY = null)
    {
        UpdatePreset(p =>
        {
            p.Anchor = anchor;
            if (offsetX is { } ox) p.OffsetX = ox;
            if (offsetY is { } oy) p.OffsetY = oy;
        }, ActivePreset.Name);
    }

    /// <summary>Aplica um estilo pronto ao preset em uso (métricas escolhidas são mantidas).</summary>
    public void ApplyStyle(OverlayStyle style) => UpdatePreset(style.Apply, ActivePreset.Name);

    /// <summary>Arrastado no modo de edição: grava a posição como deslocamento "personalizado" no monitor em que caiu.</summary>
    private void OnDragged(PointInt32 pos)
    {
        var displays = Displays();
        int index = displays.ToList().FindIndex(d =>
            pos.X >= d.OuterBounds.X && pos.X < d.OuterBounds.X + d.OuterBounds.Width &&
            pos.Y >= d.OuterBounds.Y && pos.Y < d.OuterBounds.Y + d.OuterBounds.Height);
        var display = index >= 0 ? displays[index] : DisplayArea.Primary;
        double scale = Win32.ScaleForPoint(pos.X, pos.Y);
        string presetName = ActivePreset.Name;
        UpdatePreset(p =>
        {
            p.Anchor = OverlayAnchor.Custom;
            p.MonitorIndex = index;
            p.OffsetX = Math.Max(0, (pos.X - display.OuterBounds.X) / scale);
            p.OffsetY = Math.Max(0, (pos.Y - display.OuterBounds.Y) / scale);
        }, presetName);
    }

    #endregion

    #region Permissão

    public void RefreshAccess()
    {
        Access = CapturePermission.Current();
        RaiseChanged();
    }

    /// <summary>Tenta iniciar a captura de novo (ex.: depois de conceder a permissão).</summary>
    public void RetryCapture()
    {
        if (!Settings.Enabled) return;
        _provider.Stop();
        _provider.Start();
        RaiseChanged();
    }

    public async Task<bool> GrantPermissionAsync()
    {
        bool ok = await Task.Run(CapturePermission.GrantToCurrentUser);
        RefreshAccess();
        _log.LogInformation("Inclusão no grupo Performance Log Users: {Result}.", ok ? "concluída" : "não realizada");
        return ok;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderTimer?.Stop();
        _detectTimer?.Stop();
        EndSession();
        _latency.Dispose();
        _provider.Dispose();
        _host?.Close();
        _engine.FullRateRequested = false;
    }
}
