using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ShiftControl.App.Services;
using ShiftControl.Diagnostics;
using ShiftControl.Setup;
using ShiftControl.Storage;

namespace ShiftControl.App.ViewModels;

public sealed partial class PackageVm : ObservableObject
{
    private readonly Action _changed;

    public PackageVm(CatalogPackage package, bool hardwareMissing, Action changed)
    {
        Package = package;
        HardwareMissing = hardwareMissing;
        _changed = changed;
    }

    public CatalogPackage Package { get; }
    public string Name => Package.Name;
    public string Id => Package.Id;
    public string? Description => Package.Description;
    public string? Note => Package.Note;
    public bool HasNote => !string.IsNullOrEmpty(Package.Note);
    public bool HardwareMissing { get; }
    public string SourceText => Package.Source == "msstore" ? "Microsoft Store" : "WinGet";

    [ObservableProperty] public partial bool Selected { get; set; }
    [ObservableProperty] public partial bool? Installed { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool HasUpdate { get; set; }

    partial void OnSelectedChanged(bool value) => _changed();

    public void SetInstalled(InstalledEntry? entry, bool checkedList)
    {
        if (!checkedList) { Installed = null; StatusText = ""; HasUpdate = false; return; }
        if (Package.NoDetect) { Installed = null; StatusText = L.Get("formatKit.status.noDetect"); HasUpdate = false; return; }
        Installed = entry is not null;
        HasUpdate = entry?.Available is not null;
        StatusText = entry is null ? (HardwareMissing ? L.Get("formatKit.status.hardwareMissing") : "")
            : HasUpdate ? L.F("formatKit.status.update", entry.Version, entry.Available)
            : L.F("formatKit.status.installed", entry.Version == "Unknown" ? "" : entry.Version).TrimEnd(' ', '·');
    }
}

public sealed record PackageGroup(string Category, IReadOnlyList<PackageVm> Items);

public sealed partial class JobVm : ObservableObject
{
    public JobVm(InstallJob job) => Job = job;

    public InstallJob Job { get; }
    public string Name => Job.Package.Name;
    public string KindText => L.Get(Job.Kind == JobKind.Install ? "formatKit.kind.install" : "formatKit.kind.upgrade");

    [ObservableProperty] public partial string StateText { get; set; } = "";
    [ObservableProperty] public partial string Detail { get; set; } = "";
    [ObservableProperty] public partial JobState State { get; set; }
    [ObservableProperty] public partial bool Running { get; set; }

    public void Refresh()
    {
        State = Job.State;
        Running = Job.State == JobState.Running;
        StateText = Job.State switch
        {
            JobState.Queued => L.Get("formatKit.state.queued"),
            JobState.Running => L.Get(Job.Kind == JobKind.Install ? "formatKit.state.installing" : "formatKit.state.upgrading"),
            JobState.Cancelled => L.Get("formatKit.state.cancelled"),
            _ => L.Get("formatKit.outcome." + (Job.Outcome ?? WinGetOutcome.Failed)),
        };
        Detail = Job.State == JobState.Running ? Job.LastLine ?? ""
            : Job.State == JobState.Failed ? $"{WinGetCodes.Hex(Job.ExitCode ?? 0)} · {LastMeaningful(Job.OutputTail)}"
            : "";
    }

    private static string LastMeaningful(string? tail) =>
        tail?.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
}

public sealed partial class FormatKitViewModel : ObservableObject
{
    private readonly WinGetClient _winget;
    private readonly ChangeJournal _journal;
    private readonly ILogger<FormatKitViewModel> _log;
    private readonly Catalog _catalog = Catalog.Load();
    private readonly List<PackageVm> _all = [];
    private DispatcherQueue? _ui;
    private CancellationTokenSource? _cts;
    private bool _applyingProfile;
    private bool _loaded;

    public FormatKitViewModel(WinGetClient winget, ChangeJournal journal, ILogger<FormatKitViewModel> log)
    {
        _winget = winget;
        _journal = journal;
        _log = log;
        bool hasNvidia = SystemFactsSafe().Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        foreach (var p in _catalog.Packages)
            _all.Add(new PackageVm(p, p.Requires == "nvidia" && !hasNvidia, OnSelectionChanged));
        Groups = _catalog.Categories
            .Select(c => new PackageGroup(c, _all.Where(p => p.Package.Category == c).ToList()))
            .Where(g => g.Items.Count > 0).ToList();
        Profiles = [.. _catalog.Profiles.Select(p => new Choice<string>(p.Key, p.Name)), new Choice<string>("custom", L.Get("formatKit.profile.custom"))];
        ApplySearch();
    }

    private static IReadOnlyList<string> SystemFactsSafe()
    {
        try { return Hardware.SystemFactsProbe.ReadFast().GpuNames; }
        catch (Exception) { return []; }
    }

    [ObservableProperty] public partial IReadOnlyList<PackageGroup> Groups { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";

    partial void OnSearchChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        string query = Search.Trim();
        Groups = _catalog.Categories.Select(c => new PackageGroup(c, _all.Where(p =>
            p.Package.Category == c && (query.Length == 0 ||
            p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            p.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            c.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList()))
            .Where(g => g.Items.Count > 0).ToList();
        NoResults = Groups.Count == 0;
        ResultsText = L.F("formatKit.results", Groups.Sum(g => g.Items.Count), _all.Count);
        UpdateHiddenSelection();
    }
    public IReadOnlyList<Choice<string>> Profiles { get; }
    public ObservableCollection<JobVm> Jobs { get; } = [];

    [ObservableProperty] public partial Choice<string>? Profile { get; set; }
    [ObservableProperty] public partial bool Loading { get; set; }
    [ObservableProperty] public partial bool WinGetMissing { get; set; }
    [ObservableProperty] public partial string WinGetVersion { get; set; } = "";
    [ObservableProperty] public partial bool Running { get; set; }
    [ObservableProperty] public partial bool HasJobs { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial bool NoResults { get; set; }
    [ObservableProperty] public partial string ResultsText { get; set; } = "";
    [ObservableProperty] public partial string HiddenSelectionText { get; set; } = "";
    [ObservableProperty] public partial string ReportText { get; set; } = "";
    [ObservableProperty] public partial bool HasFailures { get; set; }
    [ObservableProperty] public partial bool RebootRequired { get; set; }
    [ObservableProperty] public partial int ToInstall { get; set; }
    [ObservableProperty] public partial int ToUpgrade { get; set; }
    [ObservableProperty] public partial string InstallButtonText { get; set; } = "";
    [ObservableProperty] public partial string UpgradeButtonText { get; set; } = "";
    [ObservableProperty] public partial bool IsElevated { get; set; } = Hardware.Elevation.IsElevated;
    [ObservableProperty] public partial string? Message { get; set; }

    public void Attach(DispatcherQueue ui) => _ui = ui;

    public async Task LoadAsync(bool force = false)
    {
        if (_loaded && !force) return;
        Loading = true;
        Message = null;
        try
        {
            var version = await Task.Run(() => _winget.VersionAsync());
            WinGetMissing = version is null;
            WinGetVersion = version ?? "";
            if (WinGetMissing) return;
            var installed = await Task.Run(() => _winget.ListInstalledAsync());
            foreach (var p in _all)
                p.SetInstalled(WinGetListParser.Match(p.Package, installed) ?? RegistryDetection.Find(p.Package), checkedList: true);
            _loaded = true;
            if (Profile is null) Profile = Profiles[0];
            else OnSelectionChanged();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao consultar o WinGet.");
            Message = L.Get("formatKit.listError");
        }
        finally
        {
            Loading = false;
        }
    }

    partial void OnProfileChanged(Choice<string>? value)
    {
        if (value is null || value.Value == "custom") return;
        _applyingProfile = true;
        foreach (var p in _all)
            p.Selected = p.Package.Profiles.Contains(value.Value) && !p.HardwareMissing;
        _applyingProfile = false;
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        if (!_applyingProfile && Profile?.Value != "custom" && _loaded)
        {
            // Mexer manualmente na lista transforma o perfil em "Personalizado".
            var expected = _all.Where(p => Profile is not null && p.Package.Profiles.Contains(Profile.Value) && !p.HardwareMissing).ToHashSet();
            if (!_all.Where(p => p.Selected).ToHashSet().SetEquals(expected)) Profile = Profiles[^1];
        }
        ToInstall = _all.Count(p => p.Selected);
        ToUpgrade = SelectedUpgrades().Count;
        InstallButtonText = L.F("formatKit.installSelected", ToInstall);
        UpgradeButtonText = L.F("formatKit.upgradeSelected", ToUpgrade);
        Summary = L.F("formatKit.summary", _all.Count(p => p.Selected), _all.Count(p => p.Selected && p.Installed == true));
        UpdateHiddenSelection();
    }

    private void UpdateHiddenSelection()
    {
        var visible = Groups.SelectMany(g => g.Items).ToHashSet();
        int hidden = _all.Count(p => p.Selected && !visible.Contains(p));
        HiddenSelectionText = hidden > 0 ? L.F("formatKit.hiddenSelected", hidden) : "";
    }

    [RelayCommand]
    private void ClearSearch() => Search = "";

    [RelayCommand]
    private void SelectAll() => SetSelection(true);

    [RelayCommand]
    private void ClearSelection() => SetSelection(false);

    private void SetSelection(bool selected)
    {
        if (Running || Loading) return;
        _applyingProfile = true;
        foreach (var p in _all) p.Selected = selected && !p.HardwareMissing;
        _applyingProfile = false;
        Profile = Profiles[^1];
        OnSelectionChanged();
    }

    [RelayCommand]
    private Task RefreshAsync() => Running || Loading ? Task.CompletedTask : LoadAsync(force: true);

    [RelayCommand]
    private Task InstallSelectedAsync() =>
        RunAsync(_all.Where(p => p.Selected)
            .Select(p => new InstallJob { Package = p.Package, Kind = JobKind.Install, RecordInstallation = p.Installed != true }).ToList());

    [RelayCommand]
    private Task UpgradeSelectedAsync() => RunAsync(SelectedUpgrades());

    private List<InstallJob> SelectedUpgrades() => PackageJobPlanner.Upgrades(
        _all.Select(p => new PackageSelection(p.Package, p.Selected, p.Installed == true, p.HasUpdate)));

    [RelayCommand]
    private Task RetryFailedAsync() =>
        RunAsync(Jobs.Where(j => j.Job.State is JobState.Failed or JobState.Cancelled)
            .Select(j => new InstallJob { Package = j.Job.Package, Kind = j.Job.Kind, RecordInstallation = j.Job.RecordInstallation }).ToList());

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task RunAsync(List<InstallJob> jobs)
    {
        if (jobs.Count == 0 || Running) return;
        Running = true;
        HasJobs = true;
        ReportText = "";
        HasFailures = false;
        RebootRequired = false;
        Jobs.Clear();
        var vms = jobs.Select(j => new JobVm(j)).ToList();
        foreach (var vm in vms)
        {
            vm.Refresh();
            Jobs.Add(vm);
        }

        var queue = new InstallQueue(_winget);
        queue.JobChanged += job => _ui?.TryEnqueue(() => vms.FirstOrDefault(v => v.Job == job)?.Refresh());
        queue.Installed += job =>
        {
            try
            {
                _journal.Record("formatKit", L.F("formatKit.journal.installed", job.Package.Name), job.Package.Id,
                    UndoService.WinGetUninstall, job.Package.Id);
            }
            catch (Exception ex) { _log.LogError(ex, "Falha ao registrar a instalação na Central de desfazer."); }
        };

        _cts = new CancellationTokenSource();
        try
        {
            var report = await Task.Run(() => queue.RunAsync(jobs, _cts.Token));
            foreach (var vm in vms) vm.Refresh();
            HasFailures = report.Failed > 0 || jobs.Any(j => j.State == JobState.Cancelled);
            RebootRequired = report.RebootRequired;
            ReportText = L.F("formatKit.report", report.Succeeded, report.Failed, Fmt.Duration(report.Finished - report.Started));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha na fila do Format Kit.");
            Message = L.Get("formatKit.queueError");
            HasFailures = true;
        }
        finally
        {
            Running = false;
            _cts.Dispose();
            _cts = null;
        }
        await LoadAsync(force: true); // atualiza o estado de "instalado"
    }

    #region Kit

    /// <summary>Restaura todos os IDs do pacote, inclusive os que não pertencem ao catálogo sugerido.</summary>
    public async Task<bool> InstallKitAsync(string json)
    {
        if (Running) throw new InvalidOperationException("Já existe uma instalação em andamento.");
        var entries = KitFile.Read(json);
        var installed = await _winget.ListInstalledAsync();
        var catalog = Catalog.Load();
        var jobs = entries.Where(e => !installed.Any(i => string.Equals(i.Id, e.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(e => new InstallJob
            {
                Package = catalog.Find(e.Id) ?? new CatalogPackage { Id = e.Id, Name = e.Id, Source = e.Source, Category = "migration" },
                Kind = JobKind.Install,
            }).ToList();
        await RunAsync(jobs);
        return jobs.All(j => j.State == JobState.Done);
    }

    public string ExportKit() => KitFile.Write(
        _all.Where(p => p.Selected).Select(p => new KitFile.KitEntry(p.Package.Id, p.Package.Source)), DateTimeOffset.Now);

    /// <summary>Marca os pacotes do kit. Retorna quantos foram reconhecidos e os IDs fora do catálogo.</summary>
    public (int Matched, IReadOnlyList<string> Unknown) ImportKit(string json)
    {
        var entries = KitFile.Read(json);
        _applyingProfile = true;
        foreach (var p in _all) p.Selected = false;
        var unknown = new List<string>();
        foreach (var e in entries)
        {
            var vm = _all.FirstOrDefault(p => string.Equals(p.Package.Id, e.Id, StringComparison.OrdinalIgnoreCase));
            if (vm is null) unknown.Add(e.Id);
            else vm.Selected = true;
        }
        _applyingProfile = false;
        Profile = Profiles[^1];
        OnSelectionChanged();
        return (entries.Count - unknown.Count, unknown);
    }

    #endregion
}
