using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShiftControl.App.Services;
using ShiftControl.Hardware;
using ShiftControl.Maintenance;
using ShiftControl.Storage;

namespace ShiftControl.App.ViewModels;

/// <summary>Driver Center: inventário, placa de vídeo, Windows Update e backup/restauração.</summary>
public sealed partial class DriversViewModel : ToolViewModel
{
    private readonly HttpClient _http;
    private readonly ChangeJournal _journal;
    private readonly ILogger<DriversViewModel> _log;
    private IReadOnlyList<DriverInfo> _all = [];

    public DriversViewModel(HttpClient http, ChangeJournal journal, ILogger<DriversViewModel> log)
    {
        _http = http;
        _journal = journal;
        _log = log;
    }

    public ObservableCollection<RowVm> Gpus { get; } = [];
    public ObservableCollection<RowVm> Problems { get; } = [];
    public ObservableCollection<RowVm> Devices { get; } = [];
    public ObservableCollection<RowVm> Updates { get; } = [];

    [ObservableProperty] public partial bool Loaded { get; set; }
    [ObservableProperty] public partial bool ShowAll { get; set; }
    [ObservableProperty] public partial string InventorySummary { get; set; } = "";
    [ObservableProperty] public partial bool HasProblems { get; set; }
    [ObservableProperty] public partial bool Searching { get; set; }
    [ObservableProperty] public partial bool Searched { get; set; }
    [ObservableProperty] public partial bool Installing { get; set; }
    [ObservableProperty] public partial string UpdatesSummary { get; set; } = "";
    [ObservableProperty] public partial bool HasUpdates { get; set; }
    [ObservableProperty] public partial int SelectedUpdates { get; set; }
    [ObservableProperty] public partial string OperationLog { get; set; } = "";

    partial void OnShowAllChanged(bool value) => FillDevices();

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            _all = await Task.Run(Drivers.Inventory);
            Problems.Clear();
            foreach (var d in _all.Where(d => d.HasProblem).OrderBy(d => d.DeviceName))
                Problems.Add(new RowVm(d.DeviceName, L.Get(d.MissingDriver ? "drivers.problem.missing" : "drivers.problem.code") + $" ({d.ErrorCode})",
                    ClassName(d.DeviceClass), Tone.Critical, L.Get("drivers.problemTag")));
            HasProblems = Problems.Count > 0;
            FillDevices();
            InventorySummary = L.F("drivers.summary", _all.Count(d => d.Version is not null), Problems.Count);
            Loaded = true;
            await CheckGpuAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha no inventário de drivers.");
            Error(L.Get("drivers.inventoryError"));
        }
        finally
        {
            Busy = false;
        }
    }

    private void FillDevices()
    {
        Devices.Clear();
        var list = _all.Where(d => !d.HasProblem && d.Version is not null && (ShowAll || d.Important))
            .OrderBy(d => d.Important ? 0 : 1).ThenBy(d => ClassName(d.DeviceClass)).ThenBy(d => d.DeviceName);
        foreach (var d in list)
        {
            string meta = string.Join(" · ", new[] { ClassName(d.DeviceClass), d.Provider, d.Date?.ToString("d", L.Culture) }.Where(s => !string.IsNullOrEmpty(s)));
            Devices.Add(new RowVm(d.DeviceName, "", meta, d.Signed ? Tone.Muted : Tone.Attention, d.Signed ? "" : L.Get("drivers.unsigned")) { Value = d.Version ?? "" });
        }
    }

    private static string ClassName(string cls)
    {
        string key = "drivers.class." + cls.ToUpperInvariant();
        string name = L.Get(key);
        return name == key ? cls : name;
    }

    private async Task CheckGpuAsync()
    {
        Gpus.Clear();
        IReadOnlyList<GpuDriverStatus> gpus;
        try { gpus = await Drivers.CheckGpusAsync(_http); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha ao verificar o driver da placa de vídeo.");
            return;
        }
        foreach (var g in gpus)
        {
            string installed = g.InstalledVersion ?? "—";
            (Tone tone, string status, string detail) = (g.UpdateAvailable, g.LookupError) switch
            {
                (true, _) => (Tone.Attention, L.Get("drivers.gpu.update"), L.F("drivers.gpu.newer", g.LatestVersion, g.LatestDate?.ToString("d", L.Culture))),
                (false, _) => (Tone.Good, L.Get("drivers.gpu.current"), L.F("drivers.gpu.latestIs", g.LatestVersion)),
                (_, "noApi") => (Tone.Neutral, "", L.Get("drivers.gpu.noApi")),
                (_, "modelNotFound") => (Tone.Neutral, "", L.Get("drivers.gpu.modelNotFound")),
                _ => (Tone.Neutral, "", L.Get("drivers.gpu.network")),
            };
            string url = g.UpdateAvailable == true && g.DownloadUrl is not null ? g.DownloadUrl : g.OfficialPage;
            Gpus.Add(new RowVm(g.GpuName, detail, L.F("drivers.gpu.installed", installed, g.InstalledDate?.ToString("d", L.Culture) ?? "—"), tone, status)
            {
                ActionLabel = L.Get(g.UpdateAvailable == true ? "drivers.gpu.download" : "drivers.gpu.page"),
                Action = new RelayCommand(() => SystemIntegration.OpenUrl(url)),
            });
        }
    }

    [RelayCommand]
    private async Task SearchUpdatesAsync()
    {
        if (Searching) return;
        Searching = true;
        Updates.Clear();
        try
        {
            var found = await Task.Run(Drivers.SearchWindowsUpdate);
            foreach (var u in found)
            {
                string meta = string.Join(" · ", new[] { u.Manufacturer, u.DriverDate?.ToString("d", L.Culture), u.SizeBytes > 0 ? Shell.Size(u.SizeBytes) : null }.Where(s => !string.IsNullOrEmpty(s)));
                Updates.Add(new RowVm(u.Title, "", meta, Tone.Accent) { Selectable = true, IsChecked = true, Tag = u, Changed = _ => CountSelected() });
            }
            HasUpdates = Updates.Count > 0;
            UpdatesSummary = Updates.Count == 0 ? L.Get("drivers.wu.none") : L.F("drivers.wu.found", Updates.Count);
            CountSelected();
            Searched = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha na busca de drivers no Windows Update.");
            Error(L.Get("drivers.wu.error"));
        }
        finally
        {
            Searching = false;
        }
    }

    private void CountSelected() => SelectedUpdates = Updates.Count(u => u.IsChecked);

    /// <summary>Instala os drivers marcados, com ponto de restauração antes (exige administrador).</summary>
    public async Task InstallSelectedAsync()
    {
        var chosen = Updates.Where(u => u.IsChecked).Select(u => (DriverUpdate)u.Tag!).ToList();
        if (chosen.Count == 0 || Installing) return;
        Installing = true;
        try
        {
            var point = await Task.Run(() => RestorePoints.Create("Shift Control: antes de instalar drivers"));
            if (point is not (RestorePointResult.Created or RestorePointResult.TooFrequent))
            {
                Error(L.Get("drivers.restoreBlocked") + " " + L.Get("drivers.restorePoint." + point));
                return;
            }
            foreach (var row in Updates.Where(u => u.IsChecked)) row.Busy = true;
            var r = await Task.Run(() => Drivers.InstallFromWindowsUpdate(chosen.Select(c => c.UpdateId).ToList()));
            foreach (var row in Updates) row.Busy = false;
            string pointText = L.Get("drivers.restorePoint." + point);
            _journal.Record("drivers", L.F("drivers.journal.wu", chosen.Count), string.Join("; ", chosen.Select(c => c.Title)) + " · " + pointText, null, null);
            if (r.Ok && r.Summary != "partial")
            {
                Info(L.Get("drivers.wu.result." + r.Summary) + " " + pointText);
                foreach (var row in Updates.Where(u => u.IsChecked).ToList()) Updates.Remove(row);
                HasUpdates = Updates.Count > 0;
                CountSelected();
            }
            else Error(L.Get("drivers.wu.result." + r.Summary));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao instalar drivers do Windows Update.");
            Error(L.Get("drivers.wu.error"));
        }
        finally
        {
            foreach (var row in Updates) row.Busy = false;
            Installing = false;
        }
    }

    public async Task BackupAsync(string folder)
    {
        if (Busy || Installing) return;
        Busy = true;
        OperationLog = "";
        try
        {
            string target = Path.Combine(folder, "Drivers " + DateTime.Now.ToString("yyyy-MM-dd HHmm"));
            var r = await Drivers.BackupAsync(target, AppendLog);
            if (r.Ok) Info(L.F("drivers.backup.ok", r.Output.Split('\n')[0], target));
            else Error(L.Get("drivers.op." + r.Summary));
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task RestoreAsync(string folder)
    {
        if (Busy || Installing) return;
        Busy = true;
        OperationLog = "";
        try
        {
            if (!Shell.IsElevated)
            {
                Error(L.Get("common.requiresAdmin"));
                return;
            }
            var point = await Task.Run(() => RestorePoints.Create("Shift Control: antes de restaurar drivers"));
            if (point is not (RestorePointResult.Created or RestorePointResult.TooFrequent))
            {
                Error(L.Get("drivers.restoreBlocked") + " " + L.Get("drivers.restorePoint." + point));
                return;
            }
            var r = await Drivers.RestoreAsync(folder, AppendLog);
            _journal.Record("drivers", L.Get("drivers.journal.restore"), folder + " · " + L.Get("drivers.restorePoint." + point), null, null);
            if (r.Ok) Info(L.Get("drivers.restore." + r.Summary));
            else Error(L.Get("drivers.op." + r.Summary));
        }
        finally
        {
            Busy = false;
        }
    }

    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    private void AppendLog(string line) => _ui?.TryEnqueue(() =>
    {
        var text = OperationLog + line + "\n";
        OperationLog = text.Length > 20000 ? text[^20000..] : text;
    });
}
