using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ShiftControl.App.Services;
using ShiftControl.Maintenance;
using ShiftControl.Storage;

namespace ShiftControl.App.ViewModels;

/// <summary>Saúde dos discos (SMART pela API de Armazenamento do Windows) e espaço livre por unidade.</summary>
public sealed partial class DisksViewModel : ToolViewModel
{
    private readonly ILogger<DisksViewModel> _log;

    public DisksViewModel(ILogger<DisksViewModel> log) => _log = log;

    public ObservableCollection<RowVm> Disks { get; } = [];
    public ObservableCollection<RowVm> Volumes { get; } = [];
    [ObservableProperty] public partial bool Loaded { get; set; }
    [ObservableProperty] public partial bool CountersMissing { get; set; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var disks = await Task.Run(DiskHealthReader.Read);
            Disks.Clear();
            foreach (var d in disks)
            {
                var tone = d.HealthStatus switch { "Healthy" => Tone.Good, "Warning" => Tone.Attention, "Unhealthy" => Tone.Critical, _ => Tone.Muted };
                if (d.OperationalStatus == "Predictive failure") tone = Tone.Critical;
                var facts = new List<string>();
                if (d.TemperatureC is { } t) facts.Add(L.F("disks.temp", Fmt.Temperature(t)));
                if (d.WearPercent is { } w) facts.Add(L.F("disks.wear", w.ToString("0", L.Culture)));
                if (d.PowerOnHours is { } h) facts.Add(L.F("disks.hours", h.ToString("N0", L.Culture)));
                if (d.ReadErrors is { } re && d.WriteErrors is { } we) facts.Add(L.F("disks.errors", re + we));
                string detail = L.Get("disks.health.detail." + d.HealthStatus) + (d.OperationalStatus == "Predictive failure" ? " " + L.Get("disks.predictive") : "");
                Disks.Add(new RowVm(d.Model, detail, $"{d.MediaType} · {d.Bus}" + (facts.Count > 0 ? " · " + string.Join(" · ", facts) : ""), tone,
                    L.Get("disks.health." + d.HealthStatus)));
            }
            CountersMissing = disks.Count > 0 && disks.All(d => !d.CountersAvailable);

            Volumes.Clear();
            foreach (var v in DriveInfo.GetDrives().Where(v => v.DriveType == DriveType.Fixed))
            {
                try
                {
                    if (!v.IsReady || v.TotalSize == 0) continue;
                    double free = v.AvailableFreeSpace * 100.0 / v.TotalSize;
                    var tone = free <= 5 ? Tone.Critical : free <= 10 ? Tone.Attention : Tone.Good;
                    string label = string.IsNullOrWhiteSpace(v.VolumeLabel) ? L.Get("disks.localDisk") : v.VolumeLabel;
                    Volumes.Add(new RowVm($"{label} ({v.Name.TrimEnd('\\')})", L.F("disks.free", Shell.Size(v.AvailableFreeSpace), Shell.Size(v.TotalSize)),
                        v.DriveFormat, tone, free.ToString("0", L.Culture) + "% " + L.Get("disks.freeShort")) { Fraction = 1 - free / 100 });
                }
                catch (IOException) { }
            }
            Loaded = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao ler a saúde dos discos.");
            Error(L.Get("disks.error"));
        }
        finally
        {
            Busy = false;
        }
    }
}

/// <summary>Limpeza segura (temporários e caches) e analisador de espaço.</summary>
public sealed partial class CleanupViewModel : ToolViewModel
{
    private readonly ChangeJournal _journal;
    private readonly ILogger<CleanupViewModel> _log;
    private CancellationTokenSource? _scanCts;

    public CleanupViewModel(ChangeJournal journal, ILogger<CleanupViewModel> log)
    {
        _journal = journal;
        _log = log;
        ScanPath = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    }

    public ObservableCollection<RowVm> Categories { get; } = [];
    [ObservableProperty] public partial bool Analyzed { get; set; }
    [ObservableProperty] public partial string SelectedSize { get; set; } = "";
    [ObservableProperty] public partial bool CanClean { get; set; }

    public ObservableCollection<RowVm> Children { get; } = [];
    public ObservableCollection<RowVm> LargestFiles { get; } = [];
    [ObservableProperty] public partial string ScanPath { get; set; }
    [ObservableProperty] public partial bool Scanning { get; set; }
    [ObservableProperty] public partial string ScanProgress { get; set; } = "";
    [ObservableProperty] public partial string ScanSummary { get; set; } = "";
    [ObservableProperty] public partial bool Scanned { get; set; }
    [ObservableProperty] public partial bool CanGoUp { get; set; }

    [RelayCommand]
    public async Task AnalyzeAsync()
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var targets = await Task.Run(() => Cleaner.Analyze());
            Categories.Clear();
            foreach (var t in targets)
            {
                bool usable = t.Available && t.Bytes > 0;
                string meta = !t.Available ? L.Get("common.requiresAdmin") : t.Files > 0 ? L.F("cleanup.files", t.Files.ToString("N0", L.Culture)) : L.Get("cleanup.nothing");
                Categories.Add(new RowVm(L.Get("cleanup.cat." + t.Category), L.Get("cleanup.catHint." + t.Category), meta, usable ? Tone.Accent : Tone.Muted)
                {
                    Selectable = true,
                    IsChecked = usable && t.Category is not CleanCategory.RecycleBin,
                    CanCheck = usable,
                    Value = Shell.Size(t.Bytes),
                    Tag = t,
                    Changed = _ => UpdateSelection(),
                });
            }
            UpdateSelection();
            Analyzed = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha na análise de limpeza.");
            Error(L.Get("cleanup.error"));
        }
        finally
        {
            Busy = false;
        }
    }

    private void UpdateSelection()
    {
        long bytes = Categories.Where(c => c.IsChecked).Sum(c => ((CleanTarget)c.Tag!).Bytes);
        SelectedSize = Shell.Size(bytes);
        CanClean = bytes > 0;
    }

    public bool RecycleBinSelected => Categories.Any(c => c.IsChecked && ((CleanTarget)c.Tag!).Category == CleanCategory.RecycleBin);

    public async Task CleanAsync()
    {
        var chosen = Categories.Where(c => c.IsChecked).Select(c => ((CleanTarget)c.Tag!).Category).ToList();
        if (chosen.Count == 0 || Busy) return;
        Busy = true;
        try
        {
            var results = await Task.Run(() => Cleaner.Clean(chosen));
            long freed = results.Sum(r => r.FreedBytes);
            int skipped = results.Sum(r => r.Skipped);
            _journal.Record("cleanup", L.F("cleanup.journal", Shell.Size(freed)),
                string.Join("; ", results.Select(r => $"{L.Get("cleanup.cat." + r.Category)}: {Shell.Size(r.FreedBytes)}")), null, null);
            Info(L.F("cleanup.done", Shell.Size(freed)) + (skipped > 0 ? " " + L.F("cleanup.skipped", skipped) : ""));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha na limpeza.");
            Error(L.Get("cleanup.error"));
        }
        finally
        {
            Busy = false;
        }
        await AnalyzeAsync();
    }

    [RelayCommand]
    public async Task ScanAsync(string? path = null)
    {
        if (path is not null) ScanPath = path;
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        Scanning = true;
        ScanProgress = "";
        try
        {
            var progress = new Progress<string>(name => ScanProgress = name);
            string root = ScanPath;
            var scan = await Task.Run(() => StorageAnalyzer.Scan(root, progress, 25, cts.Token), cts.Token);
            if (_scanCts != cts) return;
            Children.Clear();
            long max = scan.Children.FirstOrDefault()?.Bytes ?? 0;
            foreach (var c in scan.Children.Take(200))
            {
                Children.Add(new RowVm(c.Name, "", c.IsFile ? L.Get("storage.file") : L.F("storage.items", c.Files.ToString("N0", L.Culture)) + (c.Partial ? " · " + L.Get("storage.partial") : ""),
                    c.IsFile ? Tone.Muted : Tone.Accent)
                {
                    Value = Shell.Size(c.Bytes),
                    Fraction = scan.TotalBytes > 0 ? (double)c.Bytes / scan.TotalBytes : 0,
                    ActionLabel = c.IsFile ? null : L.Get("storage.open"),
                    Action = c.IsFile ? null : new AsyncRelayCommand(() => ScanAsync(c.Path)),
                    SecondaryLabel = L.Get("storage.reveal"),
                    SecondaryAction = new RelayCommand(() => Reveal(c.Path)),
                });
            }
            LargestFiles.Clear();
            foreach (var f in scan.LargestFiles)
                LargestFiles.Add(new RowVm(f.Name, Path.GetDirectoryName(f.Path) ?? "", "", Tone.Muted)
                { Value = Shell.Size(f.Bytes), SecondaryLabel = L.Get("storage.reveal"), SecondaryAction = new RelayCommand(() => Reveal(f.Path)) });
            ScanSummary = L.F("storage.summary", Shell.Size(scan.TotalBytes), scan.Children.Count) + (scan.Inaccessible > 0 ? " · " + L.F("storage.inaccessible", scan.Inaccessible) : "");
            CanGoUp = Directory.GetParent(ScanPath.TrimEnd('\\') + "\\")?.Parent is not null || Path.GetPathRoot(ScanPath) != ScanPath;
            Scanned = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao analisar {Path}.", ScanPath);
            Error(L.F("storage.error", ScanPath));
        }
        finally
        {
            if (_scanCts == cts) Scanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private async Task UpAsync()
    {
        var parent = Directory.GetParent(ScanPath.TrimEnd('\\'));
        if (parent is not null) await ScanAsync(parent.FullName);
    }

    private static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>Programas que iniciam com o Windows (ligar/desligar como o Gerenciador de Tarefas, com desfazer).</summary>
public sealed partial class StartupViewModel : ToolViewModel
{
    private readonly ChangeJournal _journal;
    private readonly ILogger<StartupViewModel> _log;

    public StartupViewModel(ChangeJournal journal, ILogger<StartupViewModel> log)
    {
        _journal = journal;
        _log = log;
    }

    public ObservableCollection<RowVm> Items { get; } = [];
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial bool HasMachineItems { get; set; }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var items = await Task.Run(Startup.List);
        Items.Clear();
        foreach (var i in items)
        {
            string meta = string.Join(" · ", new[] { i.Publisher, L.Get("startup.source." + i.Source) }.Where(s => !string.IsNullOrEmpty(s)));
            var row = new RowVm(i.Name, i.Command, meta, i.Enabled ? Tone.Accent : Tone.Muted)
            {
                Toggleable = true,
                IsOn = i.Enabled,
                CanToggle = !i.RequiresAdmin || IsElevated,
                Tag = i,
                Changed = OnToggled,
            };
            Items.Add(row);
        }
        HasMachineItems = items.Any(i => i.RequiresAdmin) && !IsElevated;
        Summary = L.F("startup.summary", items.Count(i => i.Enabled), items.Count);
    }

    private void OnToggled(RowVm row)
    {
        var item = (StartupItem)row.Tag!;
        try
        {
            if (!Startup.SetEnabled(item, row.IsOn))
            {
                row.SetQuietly(isOn: !row.IsOn);
                Error(L.Get("startup.needsAdmin"));
                return;
            }
            row.Tone = row.IsOn ? Tone.Accent : Tone.Muted;
            _journal.Record("startup", L.F(row.IsOn ? "startup.journalOn" : "startup.journalOff", item.Name), item.Command,
                UndoService.StartupToggle, item.Key + "|" + (row.IsOn ? "0" : "1"));
            Summary = L.F("startup.summary", Items.Count(i => i.IsOn), Items.Count);
            Info(L.F(row.IsOn ? "startup.enabled" : "startup.disabled", item.Name));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _log.LogWarning(ex, "Falha ao alterar a inicialização de {Name}.", item.Name);
            row.SetQuietly(isOn: !row.IsOn);
            Error(L.Get("startup.needsAdmin"));
        }
    }
}

/// <summary>Reparo do Windows (SFC, DISM, verificação de disco) e histórico de erros do sistema.</summary>
public sealed partial class RepairViewModel : ToolViewModel
{
    private readonly ChangeJournal _journal;
    private readonly ILogger<RepairViewModel> _log;
    private readonly DispatcherQueue? _ui = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _cts;

    public RepairViewModel(ChangeJournal journal, ILogger<RepairViewModel> log)
    {
        _journal = journal;
        _log = log;
        foreach (var tool in Enum.GetValues<RepairTool>())
        {
            var t = tool;
            Tools.Add(new RowVm(L.Get("repair.tool." + t), L.Get("repair.toolHint." + t), L.Get("repair.toolTime." + t), Tone.Neutral)
            {
                ActionLabel = L.Get("repair.run"),
                Action = new AsyncRelayCommand(() => RunAsync(t)),
                Tag = t,
            });
        }
    }

    public ObservableCollection<RowVm> Tools { get; } = [];
    public ObservableCollection<RowVm> Errors { get; } = [];
    public ObservableCollection<StatVm> ErrorCounts { get; } = [];
    [ObservableProperty] public partial string Output { get; set; } = "";
    [ObservableProperty] public partial bool Running { get; set; }
    [ObservableProperty] public partial bool HasOutput { get; set; }
    [ObservableProperty] public partial int PeriodIndex { get; set; } = 1;
    [ObservableProperty] public partial bool LoadingErrors { get; set; }
    [ObservableProperty] public partial bool ErrorsLoaded { get; set; }
    [ObservableProperty] public partial bool NoErrors { get; set; }

    partial void OnPeriodIndexChanged(int value) => _ = LoadErrorsAsync();

    public async Task RunAsync(RepairTool tool)
    {
        if (Running) return;
        var row = Tools.First(t => (RepairTool)t.Tag! == tool);
        Running = true;
        row.Busy = true;
        row.Status = L.Get("repair.running");
        row.Tone = Tone.Accent;
        Output = "";
        HasOutput = true;
        _cts = new CancellationTokenSource();
        try
        {
            var r = await Repair.RunAsync(tool, line => _ui?.TryEnqueue(() =>
            {
                var text = Output + line + "\n";
                Output = text.Length > 30000 ? text[^30000..] : text;
            }), _cts.Token);
            row.Status = L.Get("repair.outcome." + r.Outcome);
            row.Tone = r.Outcome switch { RepairOutcome.Healthy => Tone.Good, RepairOutcome.Repaired or RepairOutcome.RebootRequired => Tone.Attention, _ => Tone.Critical };
            if (r.Outcome != RepairOutcome.RequiresAdmin)
                _journal.Record("repair", L.F("repair.journal", L.Get("repair.tool." + tool)), L.Get("repair.outcome." + r.Outcome) + $" (código {r.ExitCode})", null, null);
            if (r.Outcome == RepairOutcome.RequiresAdmin) Error(L.Get("common.requiresAdmin"));
            else Info(L.Get("repair.outcomeDetail." + r.Outcome));
        }
        catch (OperationCanceledException)
        {
            row.Status = L.Get("repair.cancelled");
            row.Tone = Tone.Muted;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao executar {Tool}.", tool);
            row.Status = L.Get("repair.outcome.Failed");
            row.Tone = Tone.Critical;
        }
        finally
        {
            row.Busy = false;
            Running = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    public async Task LoadErrorsAsync()
    {
        if (LoadingErrors) return;
        LoadingErrors = true;
        try
        {
            int days = PeriodIndex switch { 0 => 7, 2 => 90, _ => 30 };
            var events = await Task.Run(() => ErrorHistory.Read(TimeSpan.FromDays(days)));
            ErrorCounts.Clear();
            foreach (var kind in new[] { ErrorKind.Bsod, ErrorKind.UnexpectedShutdown, ErrorKind.AppCrash, ErrorKind.DriverTimeout, ErrorKind.DiskError, ErrorKind.HardwareError })
            {
                int n = events.Count(e => e.Kind == kind);
                ErrorCounts.Add(new StatVm(L.Get("errors.kindShort." + kind)) { Value = n.ToString(L.Culture), Tone = n == 0 ? Tone.Muted : kind is ErrorKind.AppCrash ? Tone.Attention : Tone.Critical });
            }
            Errors.Clear();
            foreach (var e in events.Take(150))
            {
                var tone = e.Kind is ErrorKind.Bsod or ErrorKind.HardwareError or ErrorKind.DiskError ? Tone.Critical : e.Kind is ErrorKind.AppHang ? Tone.Muted : Tone.Attention;
                string title = L.Get("errors.kind." + e.Kind) + (e.Subject.Length > 0 ? ": " + e.Subject : "");
                Errors.Add(new RowVm(title, L.Get("errors.explain." + e.Kind), e.At.ToString("g", L.Culture), tone));
            }
            NoErrors = events.Count == 0;
            ErrorsLoaded = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao ler o histórico de erros.");
            Error(L.Get("errors.error"));
        }
        finally
        {
            LoadingErrors = false;
        }
    }
}

/// <summary>Teste de conexão (ping, jitter, perda) com histórico.</summary>
public sealed partial class NetworkViewModel : ToolViewModel
{
    private readonly PingTestStore _store;
    private readonly ILogger<NetworkViewModel> _log;
    private CancellationTokenSource? _cts;

    public NetworkViewModel(PingTestStore store, ILogger<NetworkViewModel> log)
    {
        _store = store;
        _log = log;
        Stats = [Ping, Jitter, Loss, Quality];
    }

    public IReadOnlyList<string> Hosts => NetworkTest.SuggestedHosts;
    public StatVm Ping { get; } = new(L.Get("network.ping"), "ms");
    public StatVm Jitter { get; } = new(L.Get("network.jitter"), "ms");
    public StatVm Loss { get; } = new(L.Get("network.loss"), "%");
    public StatVm Quality { get; } = new(L.Get("network.quality"));
    public IReadOnlyList<StatVm> Stats { get; }
    public ObservableCollection<RowVm> History { get; } = [];

    [ObservableProperty] public partial string Host { get; set; } = NetworkTest.SuggestedHosts[0];
    [ObservableProperty] public partial bool Running { get; set; }
    [ObservableProperty] public partial double ProgressValue { get; set; }
    [ObservableProperty] public partial double[]? Samples { get; set; }
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial string Verdict { get; set; } = "";
    [ObservableProperty] public partial bool HasHistory { get; set; }

    public const int Count = 40;

    public void LoadHistory()
    {
        History.Clear();
        foreach (var t in _store.List(50))
        {
            var q = NetworkTest.Rate(new PingSummary(t.Host, t.Sent, t.Received, t.AvgMs, t.MinMs, t.MaxMs, t.JitterMs, []));
            History.Add(new RowVm(t.Host, L.F("network.historyDetail", Ms(t.AvgMs), Ms(t.JitterMs), t.LossPercent.ToString("0.#", L.Culture)),
                t.At.LocalDateTime.ToString("g", L.Culture), ToneOf(q), L.Get("network.q." + q)));
        }
        HasHistory = History.Count > 0;
    }

    private static string Ms(double? v) => v is { } x ? x.ToString("0", L.Culture) : "—";

    private static Tone ToneOf(NetworkTest.Quality q) => q switch
    {
        NetworkTest.Quality.Excellent or NetworkTest.Quality.Good => Tone.Good,
        NetworkTest.Quality.Fair => Tone.Attention,
        _ => Tone.Critical,
    };

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunAsync()
    {
        if (Running)
        {
            _cts?.Cancel();
            return;
        }
        string host = (Host ?? "").Trim();
        if (host.Length == 0 || host.Any(char.IsWhiteSpace))
        {
            Error(L.Get("network.invalidHost"));
            return;
        }
        Running = true;
        Message = null;
        ProgressValue = 0;
        var live = new List<double>();
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<(int Done, double? Ms)>(p =>
            {
                ProgressValue = p.Done * 100.0 / Count;
                live.Add(p.Ms ?? double.NaN);
                Samples = [.. live];
                if (p.Ms is { } ms) Ping.Value = ms.ToString("0", L.Culture);
            });
            var r = await NetworkTest.RunAsync(host, Count, 250, progress, _cts.Token);
            var q = NetworkTest.Rate(r);
            Ping.Value = Ms(r.AvgMs);
            Jitter.Value = Ms(r.JitterMs);
            Loss.Value = r.LossPercent.ToString("0.#", L.Culture);
            Loss.Tone = r.LossPercent >= 1 ? Tone.Critical : Tone.Good;
            Quality.Value = L.Get("network.q." + q);
            Quality.Tone = ToneOf(q);
            Verdict = L.Get("network.verdict." + q) + (r.MinMs is { } mn && r.MaxMs is { } mx ? " " + L.F("network.range", mn.ToString("0", L.Culture), mx.ToString("0", L.Culture)) : "");
            HasResult = true;
            _store.Save(new PingTestRecord(0, DateTimeOffset.Now, r.Host, r.Sent, r.Received, r.AvgMs, r.MinMs, r.MaxMs, r.JitterMs));
            LoadHistory();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha no teste de conexão.");
            Error(L.Get("network.error"));
        }
        finally
        {
            Running = false;
        }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _store.Clear();
        LoadHistory();
    }
}

/// <summary>Regras de alerta e histórico.</summary>
public sealed partial class AlertsViewModel : ToolViewModel
{
    private readonly AlertService _service;
    private readonly AlertStore _store;
    private bool _loading;

    public AlertsViewModel(AlertService service, AlertStore store)
    {
        _service = service;
        _store = store;
        _store.Added += (_, _) => _ui?.TryEnqueue(LoadHistory);
    }

    private readonly DispatcherQueue? _ui = DispatcherQueue.GetForCurrentThread();

    public ObservableCollection<RowVm> History { get; } = [];
    [ObservableProperty] public partial bool HasHistory { get; set; }

    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial bool CpuTemp { get; set; }
    [ObservableProperty] public partial double CpuTempLimit { get; set; }
    [ObservableProperty] public partial bool GpuTemp { get; set; }
    [ObservableProperty] public partial double GpuTempLimit { get; set; }
    [ObservableProperty] public partial bool DiskFree { get; set; }
    [ObservableProperty] public partial double DiskFreePercent { get; set; }
    [ObservableProperty] public partial bool Memory { get; set; }
    [ObservableProperty] public partial double MemoryPercent { get; set; }
    [ObservableProperty] public partial bool DiskHealth { get; set; }
    [ObservableProperty] public partial double SustainSeconds { get; set; }
    [ObservableProperty] public partial double CooldownMinutes { get; set; }

    public void Load()
    {
        _loading = true;
        var s = _service.Settings;
        Enabled = s.Enabled;
        CpuTemp = s.CpuTemp;
        CpuTempLimit = s.CpuTempLimit;
        GpuTemp = s.GpuTemp;
        GpuTempLimit = s.GpuTempLimit;
        DiskFree = s.DiskFree;
        DiskFreePercent = s.DiskFreePercent;
        Memory = s.Memory;
        MemoryPercent = s.MemoryPercent;
        DiskHealth = s.DiskHealth;
        SustainSeconds = s.SustainSeconds;
        CooldownMinutes = s.CooldownMinutes;
        _loading = false;
        LoadHistory();
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(History) or nameof(HasHistory) or nameof(Message) or nameof(MessageIsError) or nameof(Busy)) return;
        _service.Update(s =>
        {
            s.Enabled = Enabled;
            s.CpuTemp = CpuTemp;
            s.CpuTempLimit = Valid(CpuTempLimit, s.CpuTempLimit);
            s.GpuTemp = GpuTemp;
            s.GpuTempLimit = Valid(GpuTempLimit, s.GpuTempLimit);
            s.DiskFree = DiskFree;
            s.DiskFreePercent = Valid(DiskFreePercent, s.DiskFreePercent);
            s.Memory = Memory;
            s.MemoryPercent = Valid(MemoryPercent, s.MemoryPercent);
            s.DiskHealth = DiskHealth;
            s.SustainSeconds = (int)Valid(SustainSeconds, s.SustainSeconds);
            s.CooldownMinutes = (int)Valid(CooldownMinutes, s.CooldownMinutes);
        });
    }

    private static double Valid(double v, double fallback) => double.IsNaN(v) ? fallback : v;

    public void LoadHistory()
    {
        History.Clear();
        foreach (var a in _store.List(100))
        {
            var (title, text) = AlertService.Describe(a.Rule, a.Value, a.Subject);
            History.Add(new RowVm(title, text, a.At.LocalDateTime.ToString("g", L.Culture), a.Level == "Critical" ? Tone.Critical : Tone.Attention,
                L.Get("alert.level." + a.Level)));
        }
        HasHistory = History.Count > 0;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _store.Clear();
        LoadHistory();
    }
}
