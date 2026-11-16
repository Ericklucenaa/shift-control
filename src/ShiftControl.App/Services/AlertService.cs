using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ShiftControl.Core;
using ShiftControl.Diagnostics;
using ShiftControl.Maintenance;
using ShiftControl.Monitoring;
using ShiftControl.Storage;

namespace ShiftControl.App.Services;

/// <summary>
/// Avalia as regras de alerta a cada 5 s com a última leitura dos sensores; a saúde dos discos é lida a cada 30 min.
/// Cada alerta é gravado no histórico e mostrado como notificação do ícone da bandeja.
/// </summary>
public sealed class AlertService : IDisposable
{
    public const string SettingsKey = "alerts";

    private readonly MonitoringEngine _engine;
    private readonly ISettingsStore _store;
    private readonly AlertStore _alerts;
    private readonly TrayService _tray;
    private readonly ILogger<AlertService> _log;
    private readonly AlertEvaluator _evaluator = new();
    private DispatcherQueueTimer? _timer;
    private IReadOnlyList<(string Disk, string Health)> _diskHealth = [];
    private DateTime _diskHealthAt;
    private bool _readingDisks;

    public AlertService(MonitoringEngine engine, ISettingsStore store, AlertStore alerts, TrayService tray, ILogger<AlertService> log)
    {
        _engine = engine;
        _store = store;
        _alerts = alerts;
        _tray = tray;
        _log = log;
        Settings = (store.LoadSection<AlertSettings>(SettingsKey) ?? new AlertSettings()).Normalize();
    }

    public AlertSettings Settings { get; private set; }

    public void Initialize(DispatcherQueue ui)
    {
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Update(Action<AlertSettings> change)
    {
        var next = System.Text.Json.JsonSerializer.Deserialize<AlertSettings>(System.Text.Json.JsonSerializer.Serialize(Settings))!;
        change(next);
        Settings = next.Normalize();
        try { _store.SaveSection(SettingsKey, Settings); }
        catch (Exception ex) { _log.LogError(ex, "Não foi possível gravar as regras de alerta."); }
    }

    private void Tick()
    {
        if (!Settings.Enabled) return;
        RefreshDiskHealth();
        var s = _engine.Latest;
        if (s is null) return;
        var drives = new List<(string, double)>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady && d.TotalSize > 0)
                    drives.Add((d.Name.TrimEnd('\\'), d.AvailableFreeSpace * 100.0 / d.TotalSize));
            }
            catch (IOException) { }
        }
        var input = new AlertInput(s.Cpu.Temperature.Value, s.PrimaryGpu?.Temperature.Value,
            s.Memory.TotalBytes > 0 ? s.Memory.LoadPercent : null, drives, _diskHealth);
        foreach (var a in _evaluator.Evaluate(input, Settings, DateTimeOffset.Now))
        {
            try
            {
                _alerts.Add(a.Rule, a.Level.ToString(), a.Value, a.Subject);
                var (title, text) = Describe(a.Rule, a.Value, a.Subject);
                _tray.Notify(title, text, a.Level == AlertLevel.Critical);
                _log.LogWarning("Alerta {Rule} ({Level}): {Value} {Subject}", a.Rule, a.Level, a.Value, a.Subject);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha ao registrar o alerta {Rule}.", a.Rule);
            }
        }
    }

    public static (string Title, string Text) Describe(string rule, double? value, string? subject) =>
        (L.Get("alert.rule." + rule), L.F("alert.text." + rule, value?.ToString("0", L.Culture) ?? "", subject ?? ""));

    private void RefreshDiskHealth()
    {
        if (!Settings.DiskHealth || _readingDisks || DateTime.UtcNow - _diskHealthAt < TimeSpan.FromMinutes(30)) return;
        _readingDisks = true;
        _ = Task.Run(() =>
        {
            try { _diskHealth = DiskHealthReader.Read().Select(d => (d.Model, d.HealthStatus)).ToList(); }
            catch (Exception ex) { _log.LogWarning(ex, "Falha ao ler a saúde dos discos."); }
            finally
            {
                _diskHealthAt = DateTime.UtcNow;
                _readingDisks = false;
            }
        });
    }

    public void Dispose() => _timer?.Stop();
}
