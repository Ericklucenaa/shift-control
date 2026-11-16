using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShiftControl.App.Overlay;
using ShiftControl.App.Services;
using ShiftControl.Core;
using ShiftControl.Gaming;
using ShiftControl.Overlay;

namespace ShiftControl.App.ViewModels;

public sealed record ColorChoice(string Hex, string Label, Windows.UI.Color Color);

public sealed partial class OverlayItemVm : ObservableObject
{
    private readonly Action _changed;

    public OverlayItemVm(OverlayItem item, Action changed)
    {
        _changed = changed;
        Metric = item.Metric;
        DefaultName = OverlayComposer.DefaultLabel(item.Metric);
        Visible = item.Visible;
        Label = item.Label ?? "";
        SourceText = L.Get($"overlay.source.{OverlayCatalog.SourceOf(item.Metric)}");
    }

    public OverlayMetric Metric { get; }
    public string DefaultName { get; }
    public string SourceText { get; }

    [ObservableProperty] public partial bool Visible { get; set; }
    [ObservableProperty] public partial string Label { get; set; }

    partial void OnVisibleChanged(bool value) => _changed();

    public void CommitLabel() => _changed();

    public OverlayItem ToItem() => new() { Metric = Metric, Visible = Visible, Label = string.IsNullOrWhiteSpace(Label) ? null : Label };
}

public sealed record StyleVm(OverlayStyle Style, string Title, string Description, OverlayPreset Preview);

public sealed record GamePresetVm(string Executable, string Preset);

public sealed record PresentingVm(int Pid, string Executable, string FpsText, string Runtime, bool Excluded);

public sealed partial class OverlayPageViewModel : ObservableObject
{
    private readonly OverlayService _service;
    private bool _loading;

    public OverlayPageViewModel(OverlayService service)
    {
        _service = service;
        Orientations = [new(OverlayOrientation.Vertical, L.Get("overlay.orientation.vertical")), new(OverlayOrientation.Horizontal, L.Get("overlay.orientation.horizontal"))];
        Alignments = [new(OverlayAlignment.Left, L.Get("overlay.alignment.left")), new(OverlayAlignment.Right, L.Get("overlay.alignment.right"))];
        Anchors = Enum.GetValues<OverlayAnchor>().Select(a => new Choice<OverlayAnchor>(a, L.Get($"overlay.anchor.{a}"))).ToList();
        Intervals = OverlayPreset.AllowedIntervalsMs.Select(ms => new Choice<int>(ms, L.F("overlay.interval", ms))).ToList();
        TextColors = [Color("#ECEDEE", "overlay.color.white"), Color("#FFFFFF", "overlay.color.pure"), Color("#F2E9D8", "overlay.color.warm")];
        Accents = AccentPalette.Options.Select(o => Color(o.Hex, $"accent.{o.Key}")).ToList();
        Backgrounds = [Color("#0E0F10", "overlay.color.graphite"), Color("#000000", "overlay.color.black"), Color("#1C1E21", "overlay.color.gray")];
        service.Changed += (_, _) => RefreshStatus();
        Load();
        RefreshStatus();
    }

    private static ColorChoice Color(string hex, string key) => new(hex, L.Get(key), ThemeService.Parse(hex));

    public IReadOnlyList<Choice<OverlayOrientation>> Orientations { get; }
    public IReadOnlyList<Choice<OverlayAlignment>> Alignments { get; }
    public IReadOnlyList<Choice<OverlayAnchor>> Anchors { get; }
    public IReadOnlyList<Choice<int>> Intervals { get; }
    public IReadOnlyList<ColorChoice> TextColors { get; }
    public IReadOnlyList<ColorChoice> Accents { get; }
    public IReadOnlyList<ColorChoice> Backgrounds { get; }
    public ObservableCollection<Choice<int>> Monitors { get; } = [];
    public ObservableCollection<string> PresetNames { get; } = [];
    public ObservableCollection<OverlayItemVm> Items { get; } = [];
    public ObservableCollection<PresentingVm> Presenting { get; } = [];
    public ObservableCollection<GamePresetVm> GamePresets { get; } = [];
    public ObservableCollection<StyleVm> Styles { get; } = [];

    /// <summary>O preset em uso, para a miniatura da tela (posição e visual finais).</summary>
    [ObservableProperty] public partial OverlayPreset? CurrentPreset { get; set; }
    [ObservableProperty] public partial double CornerRadius { get; set; }
    [ObservableProperty] public partial string PositionText { get; set; } = "";

    // Geral
    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial bool OnlyInGame { get; set; }
    [ObservableProperty] public partial bool RecordSessions { get; set; }
    [ObservableProperty] public partial string Hotkey { get; set; } = "";
    [ObservableProperty] public partial string? SelectedPreset { get; set; }

    // Aparência do preset ativo
    [ObservableProperty] public partial Choice<OverlayOrientation>? Orientation { get; set; }
    [ObservableProperty] public partial Choice<OverlayAlignment>? Alignment { get; set; }
    [ObservableProperty] public partial Choice<OverlayAnchor>? Anchor { get; set; }
    [ObservableProperty] public partial Choice<int>? Interval { get; set; }
    [ObservableProperty] public partial Choice<int>? Monitor { get; set; }
    [ObservableProperty] public partial ColorChoice? TextColor { get; set; }
    [ObservableProperty] public partial ColorChoice? AccentColor { get; set; }
    [ObservableProperty] public partial ColorChoice? BackgroundColor { get; set; }
    [ObservableProperty] public partial double FontSize { get; set; }
    [ObservableProperty] public partial double Scale { get; set; }
    [ObservableProperty] public partial double Opacity { get; set; }
    [ObservableProperty] public partial double OffsetX { get; set; }
    [ObservableProperty] public partial double OffsetY { get; set; }
    [ObservableProperty] public partial bool ShowGraphs { get; set; }
    [ObservableProperty] public partial bool ShowBars { get; set; }

    // Jogos e rede
    [ObservableProperty] public partial bool AutoDetect { get; set; }
    [ObservableProperty] public partial string ManualExecutable { get; set; } = "";
    [ObservableProperty] public partial string ExclusionsText { get; set; } = "";
    [ObservableProperty] public partial string PingHost { get; set; } = "";

    // Estado
    [ObservableProperty] public partial string CaptureTitle { get; set; } = "";
    [ObservableProperty] public partial string CaptureMessage { get; set; } = "";
    [ObservableProperty] public partial bool CaptureOk { get; set; }
    [ObservableProperty] public partial bool ShowGrant { get; set; }
    [ObservableProperty] public partial bool ShowRetry { get; set; }
    [ObservableProperty] public partial bool EditMode { get; set; }
    [ObservableProperty] public partial bool HasTarget { get; set; }
    [ObservableProperty] public partial string TargetTitle { get; set; } = "";
    [ObservableProperty] public partial string TargetDetail { get; set; } = "";
    [ObservableProperty] public partial bool ExclusiveFullscreen { get; set; }
    [ObservableProperty] public partial string HotkeyStatus { get; set; } = "";
    [ObservableProperty] public partial bool HotkeyOk { get; set; }
    [ObservableProperty] public partial string VisibilityText { get; set; } = "";

    public string? TargetExecutable => _service.Target?.Executable;

    private void Load()
    {
        _loading = true;
        var s = _service.Settings;
        var p = s.Presets.FirstOrDefault(x => x.Name == s.ActivePreset) ?? s.Active;

        Enabled = s.Enabled;
        OnlyInGame = s.OnlyInGame;
        RecordSessions = s.RecordSessions;
        Hotkey = s.Hotkey;
        AutoDetect = s.AutoDetect;
        ManualExecutable = s.ManualExecutable ?? "";
        ExclusionsText = string.Join(Environment.NewLine, s.Exclusions);
        PingHost = s.PingHost;

        PresetNames.Clear();
        foreach (var name in s.Presets.Select(x => x.Name)) PresetNames.Add(name);
        SelectedPreset = p.Name;

        Orientation = Orientations.First(o => o.Value == p.Orientation);
        Alignment = Alignments.First(a => a.Value == p.Alignment);
        Anchor = Anchors.First(a => a.Value == p.Anchor);
        Interval = Intervals.FirstOrDefault(i => i.Value == p.UpdateIntervalMs) ?? Intervals[1];
        TextColor = TextColors.FirstOrDefault(c => Same(c.Hex, p.TextColor)) ?? TextColors[0];
        AccentColor = Accents.FirstOrDefault(c => Same(c.Hex, p.AccentColor)) ?? Accents[0];
        BackgroundColor = Backgrounds.FirstOrDefault(c => Same(c.Hex, p.BackgroundColor)) ?? Backgrounds[0];
        FontSize = p.FontSize;
        Scale = p.Scale;
        Opacity = p.BackgroundOpacity * 100;
        OffsetX = p.OffsetX;
        OffsetY = p.OffsetY;
        ShowGraphs = p.ShowGraphs;
        ShowBars = p.ShowBars;
        CornerRadius = p.CornerRadius;
        CurrentPreset = _service.ActivePreset.Clone();
        PositionText = p.Anchor == OverlayAnchor.Custom
            ? L.F("overlay.position.custom", Fmt.Number(p.OffsetX), Fmt.Number(p.OffsetY))
            : L.Get($"overlay.anchor.{p.Anchor}");

        // Exemplos renderizados com as métricas que o usuário escolheu.
        Styles.Clear();
        foreach (var style in OverlayStyles.All)
        {
            var preview = p.Clone();
            style.Apply(preview);
            Styles.Add(new StyleVm(style, L.Get($"overlay.style.{style.Key}"), L.Get($"overlay.style.{style.Key}.desc"), preview));
        }

        Monitors.Clear();
        Monitors.Add(new Choice<int>(-1, L.Get("overlay.monitor.primary")));
        var displays = OverlayService.Displays();
        for (int i = 0; i < displays.Count; i++)
            Monitors.Add(new Choice<int>(i, L.F("overlay.monitor.item", i + 1, displays[i].OuterBounds.Width, displays[i].OuterBounds.Height)));
        Monitor = Monitors.FirstOrDefault(m => m.Value == p.MonitorIndex) ?? Monitors[0];

        Items.Clear();
        foreach (var item in p.Items) Items.Add(new OverlayItemVm(item, CommitItems));

        GamePresets.Clear();
        foreach (var (exe, preset) in s.GamePresets) GamePresets.Add(new GamePresetVm(exe, preset));
        _loading = false;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Reload() => Load();

    public void RefreshStatus()
    {
        var state = _service.CaptureState;
        var access = _service.Access;
        CaptureOk = state == FrameCaptureState.Running;
        ShowGrant = state == FrameCaptureState.PermissionDenied && access is CaptureAccess.None;
        ShowRetry = state is FrameCaptureState.Faulted || (state == FrameCaptureState.PermissionDenied && access is CaptureAccess.Administrator or CaptureAccess.PerformanceLogUsers);
        (CaptureTitle, CaptureMessage) = state switch
        {
            FrameCaptureState.Running => (L.Get("overlay.capture.running"), L.Get("overlay.capture.runningDetail")),
            FrameCaptureState.Starting => (L.Get("overlay.capture.starting"), ""),
            FrameCaptureState.PermissionDenied when access == CaptureAccess.PendingSignOut =>
                (L.Get("overlay.capture.pendingTitle"), L.Get("overlay.capture.pending")),
            FrameCaptureState.PermissionDenied => (L.Get("overlay.capture.deniedTitle"), L.Get("overlay.capture.denied")),
            FrameCaptureState.Missing => (L.Get("overlay.capture.missingTitle"), L.Get("overlay.capture.missing")),
            FrameCaptureState.Faulted => (L.Get("overlay.capture.faultedTitle"), _service.CaptureDetail ?? ""),
            _ => (L.Get("overlay.capture.stoppedTitle"), L.Get("overlay.capture.stopped")),
        };

        var t = _service.Target;
        HasTarget = t is not null;
        TargetTitle = t is null ? L.Get("overlay.game.none") : _service.TargetName ?? t.Executable;
        TargetDetail = t is null
            ? (_service.Settings.Enabled ? L.Get("overlay.game.noneDetail") : L.Get("overlay.game.disabled"))
            : L.F("overlay.game.detail", t.Executable, t.ProcessId, Api(t.Runtime), t.PresentMode, Fmt.Number(t.Fps));
        ExclusiveFullscreen = t?.IsExclusiveFullscreen == true;
        EditMode = _service.EditMode;
        HotkeyOk = _service.HotkeyRegistered;
        HotkeyStatus = !_service.Settings.Enabled ? L.Get("overlay.hotkey.inactive")
            : _service.HotkeyRegistered ? L.Get("overlay.hotkey.ok") : L.Get("overlay.hotkey.conflict");
        VisibilityText = !_service.Settings.Enabled ? "" : _service.IsOverlayShown ? L.Get("overlay.visible.shown")
            : !_service.UserVisible ? L.F("overlay.visible.hiddenByHotkey", _service.Settings.Hotkey) : L.Get("overlay.visible.waiting");

        // Processos renderizando agora: transparência sobre o que a detecção está vendo.
        var list = _service.Router.Presenting().OrderByDescending(p => p.Fps).Take(6)
            .Select(p => new PresentingVm(p.ProcessId, p.Executable, Fmt.Number(p.Fps) + " FPS", Api(p.Runtime),
                GameTargetPolicy.IsExcluded(p.Executable, _service.Settings.Exclusions)))
            .ToList();
        if (!list.SequenceEqual(Presenting))
        {
            Presenting.Clear();
            foreach (var p in list) Presenting.Add(p);
        }
    }

    private static string Api(string runtime) => string.IsNullOrWhiteSpace(runtime) || runtime == "Other" ? L.Get("overlay.api.unknown") : runtime;

    #region Gravação das alterações

    private void Settings(Action<OverlaySettings> change)
    {
        if (_loading) return;
        _service.UpdateSettings(change);
    }

    private void Preset(Action<OverlayPreset> change)
    {
        if (_loading) return;
        _service.UpdatePreset(change);
        RefreshPreview();
    }

    /// <summary>Atualiza a miniatura e os exemplos sem recarregar os controles (não interrompe um slider em uso).</summary>
    private void RefreshPreview()
    {
        var p = _service.Settings.Active;
        CurrentPreset = _service.ActivePreset.Clone();
        PositionText = p.Anchor == OverlayAnchor.Custom
            ? L.F("overlay.position.custom", Fmt.Number(p.OffsetX), Fmt.Number(p.OffsetY))
            : L.Get($"overlay.anchor.{p.Anchor}");
        for (int i = 0; i < Styles.Count; i++)
        {
            var preview = p.Clone();
            Styles[i].Style.Apply(preview);
            Styles[i] = Styles[i] with { Preview = preview };
        }
    }

    partial void OnEnabledChanged(bool value) => Settings(s => s.Enabled = value);
    partial void OnOnlyInGameChanged(bool value) => Settings(s => s.OnlyInGame = value);
    partial void OnRecordSessionsChanged(bool value) => Settings(s => s.RecordSessions = value);
    partial void OnAutoDetectChanged(bool value) => Settings(s => s.AutoDetect = value);

    partial void OnSelectedPresetChanged(string? value)
    {
        if (value is null || _loading) return;
        Settings(s => s.ActivePreset = value);
        Load();
    }

    partial void OnOrientationChanged(Choice<OverlayOrientation>? value) { if (value is not null) Preset(p => p.Orientation = value.Value); }
    partial void OnAlignmentChanged(Choice<OverlayAlignment>? value) { if (value is not null) Preset(p => p.Alignment = value.Value); }
    partial void OnAnchorChanged(Choice<OverlayAnchor>? value) { if (value is not null) Preset(p => p.Anchor = value.Value); }
    partial void OnIntervalChanged(Choice<int>? value) { if (value is not null) Preset(p => p.UpdateIntervalMs = value.Value); }
    partial void OnMonitorChanged(Choice<int>? value) { if (value is not null) Preset(p => p.MonitorIndex = value.Value); }
    partial void OnTextColorChanged(ColorChoice? value) { if (value is not null) Preset(p => p.TextColor = value.Hex); }
    partial void OnAccentColorChanged(ColorChoice? value) { if (value is not null) Preset(p => p.AccentColor = value.Hex); }
    partial void OnBackgroundColorChanged(ColorChoice? value) { if (value is not null) Preset(p => p.BackgroundColor = value.Hex); }
    partial void OnFontSizeChanged(double value) => Preset(p => p.FontSize = value);
    partial void OnScaleChanged(double value) => Preset(p => p.Scale = value);
    partial void OnOpacityChanged(double value) => Preset(p => p.BackgroundOpacity = value / 100);
    partial void OnOffsetXChanged(double value) { if (double.IsFinite(value)) Preset(p => p.OffsetX = value); }
    partial void OnOffsetYChanged(double value) { if (double.IsFinite(value)) Preset(p => p.OffsetY = value); }
    partial void OnShowGraphsChanged(bool value) => Preset(p => p.ShowGraphs = value);
    partial void OnShowBarsChanged(bool value) => Preset(p => p.ShowBars = value);
    partial void OnCornerRadiusChanged(double value) => Preset(p => p.CornerRadius = value);

    /// <summary>Posição escolhida na miniatura (ponto clicado ou arraste).</summary>
    public void SetPosition(OverlayAnchor anchor, double? offsetX, double? offsetY)
    {
        _service.SetPosition(anchor, offsetX, offsetY);
        Load();
    }

    [RelayCommand]
    private void ApplyStyle(StyleVm? style)
    {
        if (style is null) return;
        _service.ApplyStyle(style.Style);
        Load();
    }

    /// <summary>Visibilidade, rótulo e ordem das métricas (chamado também após arrastar e soltar).</summary>
    public void CommitItems() => Preset(p => p.Items = Items.Select(i => i.ToItem()).ToList());

    public void CommitPingHost() => Settings(s => s.PingHost = PingHost);

    public void CommitExclusions() => Settings(s => s.Exclusions = ExclusionsText
        .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());

    public void CommitManualExecutable() => Settings(s => s.ManualExecutable = string.IsNullOrWhiteSpace(ManualExecutable) ? null : ManualExecutable);

    /// <summary>Grava um novo atalho. Retorna falso se o texto não for um atalho válido.</summary>
    public bool SetHotkey(string text)
    {
        if (!ShiftControl.Overlay.Hotkeys.TryParse(text, out var hk)) return false;
        Hotkey = hk.ToString();
        Settings(s => s.Hotkey = Hotkey);
        RefreshStatus();
        return true;
    }

    #endregion

    #region Comandos

    [RelayCommand]
    private void ToggleEditMode() => _service.SetEditMode(!_service.EditMode);

    [RelayCommand]
    private void ToggleVisible() => _service.ToggleVisible();

    [RelayCommand]
    private void RetryCapture() => _service.RetryCapture();

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (SystemIntegration.RestartElevated()) Microsoft.UI.Xaml.Application.Current.Exit();
    }

    [RelayCommand]
    private void UseProcess(PresentingVm? p)
    {
        if (p is null) return;
        ManualExecutable = p.Executable;
        CommitManualExecutable();
    }

    [RelayCommand]
    private void ExcludeProcess(PresentingVm? p)
    {
        if (p is null) return;
        ExclusionsText = string.Join(Environment.NewLine, ExclusionsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Append(p.Executable));
        CommitExclusions();
    }

    [RelayCommand]
    private void ClearManual()
    {
        ManualExecutable = "";
        CommitManualExecutable();
    }

    [RelayCommand]
    private void AssignPresetToGame()
    {
        if (_service.Target is not { } t || SelectedPreset is not { } preset) return;
        Settings(s => s.GamePresets[GameTargetPolicy.Normalize(t.Executable)] = preset);
        Load();
    }

    [RelayCommand]
    private void RemoveGamePreset(GamePresetVm? entry)
    {
        if (entry is null) return;
        Settings(s => s.GamePresets.Remove(entry.Executable));
        Load();
    }

    [RelayCommand]
    private void ResetBuiltInPresets()
    {
        Settings(s =>
        {
            foreach (var builtIn in OverlayPreset.BuiltIn())
            {
                int i = s.Presets.FindIndex(p => p.Name == builtIn.Name);
                if (i >= 0) s.Presets[i] = builtIn;
                else s.Presets.Add(builtIn);
            }
        });
        Load();
    }

    /// <summary>Cria um preset novo a partir do atual (ex.: "Minimalista (2)").</summary>
    public void DuplicatePreset(string? requestedName)
    {
        var current = _service.Settings.Active;
        string baseName = string.IsNullOrWhiteSpace(requestedName) ? current.Name : requestedName.Trim();
        string name = baseName;
        for (int i = 2; _service.Settings.Presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{baseName} ({i})";
        var copy = current.Clone();
        copy.Name = name;
        Settings(s =>
        {
            s.Presets.Add(copy);
            s.ActivePreset = name;
        });
        Load();
    }

    public bool CanDeleteActivePreset => _service.Settings.Presets.Count > 1;

    [RelayCommand]
    private void DeleteActivePreset()
    {
        if (!CanDeleteActivePreset) return;
        string name = _service.Settings.ActivePreset;
        Settings(s =>
        {
            s.Presets.RemoveAll(p => p.Name == name);
            foreach (var key in s.GamePresets.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList()) s.GamePresets.Remove(key);
            s.ActivePreset = s.Presets[0].Name;
        });
        Load();
    }

    public async Task<bool> GrantPermissionAsync()
    {
        bool ok = await _service.GrantPermissionAsync();
        RefreshStatus();
        return ok;
    }

    #endregion
}
