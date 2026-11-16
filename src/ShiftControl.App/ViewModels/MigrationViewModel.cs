using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ShiftControl.App.Overlay;
using ShiftControl.App.Services;
using ShiftControl.Core;
using ShiftControl.Maintenance;
using ShiftControl.Overlay;
using ShiftControl.Setup;

namespace ShiftControl.App.ViewModels;

/// <summary>"Antes de formatar" (cria o pacote) e "Depois de formatar" (restaura a partir dele).</summary>
public sealed partial class MigrationViewModel : ToolViewModel
{
    private readonly WinGetClient _winget;
    private readonly SettingsService _settings;
    private readonly OverlayService _overlay;
    private readonly GameBoostService _boost;
    private readonly AlertService _alerts;
    private readonly FormatKitViewModel _formatKit;
    private readonly ILogger<MigrationViewModel> _log;

    public MigrationViewModel(WinGetClient winget, SettingsService settings, OverlayService overlay, GameBoostService boost, AlertService alerts,
        FormatKitViewModel formatKit, ILogger<MigrationViewModel> log)
    {
        _winget = winget;
        _settings = settings;
        _overlay = overlay;
        _boost = boost;
        _alerts = alerts;
        _formatKit = formatKit;
        _log = log;
    }

    [ObservableProperty] public partial bool IncludePrograms { get; set; } = true;
    [ObservableProperty] public partial bool IncludeDrivers { get; set; } = true;
    [ObservableProperty] public partial bool IncludeWifi { get; set; } = true;
    [ObservableProperty] public partial bool IncludeSettings { get; set; } = true;
    [ObservableProperty] public partial string CreateStep { get; set; } = "";
    public ObservableCollection<RowVm> CreateResults { get; } = [];
    [ObservableProperty] public partial bool HasCreateResults { get; set; }

    [ObservableProperty] public partial string? RestoreFolder { get; set; }
    [ObservableProperty] public partial bool HasPackage { get; set; }
    [ObservableProperty] public partial string PackageSummary { get; set; } = "";
    public ObservableCollection<RowVm> RestoreSteps { get; } = [];

    public async Task RestoreAllAsync()
    {
        if (Busy || !HasPackage || RestoreFolder is null) return;
        Busy = true;
        try
        {
            foreach (var row in RestoreSteps)
            {
                row.Busy = true;
                try
                {
                    switch ((MigrationStep)row.Tag!)
                    {
                        case MigrationStep.Programs:
                            var json = Migration.ProgramsKit(RestoreFolder) ?? throw new InvalidDataException("Lista de programas ausente.");
                            bool ok = await _formatKit.InstallKitAsync(json);
                            row.Status = L.Get(ok ? "migration.done" : "migration.failed");
                            row.Tone = ok ? Tone.Good : Tone.Critical;
                            break;
                        case MigrationStep.Wifi: await RestoreWifiAsync(row); break;
                        case MigrationStep.Drivers: await RestoreDriversAsync(row); break;
                        case MigrationStep.AppSettings: await RestoreSettings(row); break;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Falha na restauração de {Step}.", row.Tag);
                    row.Status = L.Get("migration.failed");
                    row.Tone = Tone.Critical;
                    row.Meta = ex.Message;
                }
                finally { row.Busy = false; }
            }
        }
        finally { Busy = false; }
    }

    public async Task CreateAsync(string parent)
    {
        if (Busy) return;
        Busy = true;
        CreateResults.Clear();
        HasCreateResults = false;
        string folder = Path.Combine(parent, "Shift Control - migração " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + " " + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var options = new MigrationOptions(IncludePrograms, IncludeDrivers && Shell.IsElevated, IncludeWifi, IncludeSettings);
            var results = await Migration.CreateAsync(folder, options, _winget, IncludeSettings ? ExportSettings() : null,
                step => CreateStep = L.Get("migration.step." + step));
            foreach (var r in results)
            {
                string detail = r.Ok ? L.F("migration.count." + r.Step, r.Count) : r.Detail == "noProfiles" ? L.Get("migration.noWifi") : r.Detail ?? "";
                CreateResults.Add(new RowVm(L.Get("migration.step." + r.Step), detail, "", r.Ok ? Tone.Good : Tone.Attention, L.Get(r.Ok ? "migration.done" : "migration.failed")));
            }
            if (IncludeDrivers && !Shell.IsElevated)
                CreateResults.Add(new RowVm(L.Get("migration.step.Drivers"), L.Get("migration.driversNeedAdmin"), "", Tone.Attention, L.Get("migration.skipped")));
            HasCreateResults = true;
            Info(L.F("migration.created", folder));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao criar o pacote de migração.");
            Error(L.F("migration.createError", ex.Message));
        }
        finally
        {
            CreateStep = "";
            Busy = false;
        }
    }

    /// <summary>Preferências do app, do overlay, do Game Boost e dos alertas num único JSON.</summary>
    private string ExportSettings()
    {
        var root = new JsonObject
        {
            ["app"] = JsonSerializer.SerializeToNode(_settings.Current),
            ["overlay"] = JsonSerializer.SerializeToNode(_overlay.Settings),
            ["gameBoost"] = JsonSerializer.SerializeToNode(_boost.Settings),
            ["alerts"] = JsonSerializer.SerializeToNode(_alerts.Settings),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public void OpenPackage(string folder)
    {
        if (Busy) return;
        RestoreSteps.Clear();
        var manifest = Migration.Read(folder);
        if (manifest is null)
        {
            HasPackage = false;
            Error(L.Get("migration.notPackage"));
            return;
        }
        RestoreFolder = folder;
        HasPackage = true;
        Message = null;
        PackageSummary = L.F("migration.packageFrom", manifest.CreatedAt.LocalDateTime.ToString("g", L.Culture), manifest.WindowsVersion);

        if (manifest.Programs > 0)
            RestoreSteps.Add(Step(MigrationStep.Programs, L.F("migration.count.Programs", manifest.Programs), RestorePrograms));
        if (manifest.WifiProfiles > 0)
            RestoreSteps.Add(Step(MigrationStep.Wifi, L.F("migration.count.Wifi", manifest.WifiProfiles), RestoreWifiAsync));
        if (manifest.Drivers > 0)
            RestoreSteps.Add(Step(MigrationStep.Drivers, L.F("migration.count.Drivers", manifest.Drivers) + (Shell.IsElevated ? "" : " · " + L.Get("common.requiresAdmin")), RestoreDriversAsync));
        if (manifest.AppSettings)
            RestoreSteps.Add(Step(MigrationStep.AppSettings, L.Get("migration.count.AppSettings"), RestoreSettings));
    }

    private RowVm Step(MigrationStep step, string detail, Func<RowVm, Task> run)
    {
        RowVm? row = null;
        row = new RowVm(L.Get("migration.restore." + step), detail, "", Tone.Neutral)
        {
            Tag = step,
            ActionLabel = L.Get("migration.restoreAction." + step),
            Action = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () =>
            {
                if (Busy) return;
                Busy = true;
                row!.Busy = true;
                try { await run(row); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Falha ao restaurar {Step}.", step);
                    row.Tone = Tone.Critical;
                    row.Status = L.Get("migration.failed");
                    row.Meta = ex.Message;
                }
                finally { row.Busy = false; Busy = false; }
            }),
        };
        return row;
    }

    private Task RestorePrograms(RowVm row)
    {
        var json = Migration.ProgramsKit(RestoreFolder!);
        if (json is null) return Task.CompletedTask;
        var (matched, unknown) = _formatKit.ImportKit(json);
        row.Tone = Tone.Good;
        row.Status = L.Get("migration.done");
        row.Meta = L.F("migration.programsImported", matched) + (unknown.Count > 0 ? " " + L.F("migration.programsUnknown", unknown.Count, string.Join(", ", unknown.Take(8))) : "");
        return Task.CompletedTask;
    }

    private async Task RestoreWifiAsync(RowVm row)
    {
        var r = await Migration.RestoreWifiAsync(RestoreFolder!);
        row.Tone = r.Ok ? Tone.Good : Tone.Attention;
        row.Status = L.Get(r.Ok ? "migration.done" : "migration.failed");
        row.Meta = L.F("migration.wifiRestored", r.Count);
    }

    private async Task RestoreDriversAsync(RowVm row)
    {
        if (!Shell.IsElevated)
        {
            row.Tone = Tone.Attention;
            row.Meta = L.Get("common.requiresAdmin");
            return;
        }
        var point = await Task.Run(() => Hardware.RestorePoints.Create("Shift Control: antes de restaurar drivers"));
        if (point is not (Hardware.RestorePointResult.Created or Hardware.RestorePointResult.TooFrequent))
            throw new InvalidOperationException(L.Get("drivers.restoreBlocked") + " " + L.Get("drivers.restorePoint." + point));
        var r = await Migration.RestoreDriversAsync(RestoreFolder!);
        row.Tone = r.Ok ? Tone.Good : Tone.Critical;
        row.Status = L.Get(r.Ok ? "migration.done" : "migration.failed");
        row.Meta = L.Get("drivers.restore." + (r.Ok ? r.Summary : "failed"));
    }

    private Task RestoreSettings(RowVm row)
    {
        var json = Migration.AppSettings(RestoreFolder!);
        var root = json is null ? null : JsonNode.Parse(json);
        if (root?["app"]?.Deserialize<AppSettings>() is { } app) _settings.Replace(app);
        if (root?["overlay"]?.Deserialize<OverlaySettings>() is { } overlay) _overlay.ReplaceSettings(overlay);
        if (root?["gameBoost"]?.Deserialize<GameBoostSettings>() is { } boost)
            _boost.Update(s =>
            {
                s.AutoApply = boost.AutoApply;
                s.DefaultProfile = boost.DefaultProfile;
                s.PerGame = boost.PerGame;
                s.PauseApps = boost.PauseApps;
            });
        if (root?["alerts"]?.Deserialize<Diagnostics.AlertSettings>() is { } alerts)
            _alerts.Update(s =>
            {
                foreach (var p in typeof(Diagnostics.AlertSettings).GetProperties()) p.SetValue(s, p.GetValue(alerts));
            });
        row.Tone = Tone.Good;
        row.Status = L.Get("migration.done");
        row.Meta = L.Get("migration.settingsRestored");
        return Task.CompletedTask;
    }
}
