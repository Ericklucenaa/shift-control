using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShiftControl.App.Services;
using ShiftControl.Core;
using ShiftControl.Diagnostics;
using ShiftControl.Monitoring;
using ShiftControl.Storage;

namespace ShiftControl.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    public const int LiveSlots = 60;
    public ObservableCollection<RowVm> RecentAlerts { get; } = [];
    [ObservableProperty] public partial bool HasAlerts { get; set; }

    private readonly MonitoringEngine _engine;
    private readonly SettingsService _settings;

    public DashboardViewModel(MonitoringEngine engine, SettingsService settings, AlertStore alerts)
    {
        _engine = engine;
        _settings = settings;
        void RefreshAlerts()
        {
            RecentAlerts.Clear();
            foreach (var a in alerts.List(5))
            {
                var (title, text) = AlertService.Describe(a.Rule, a.Value, a.Subject);
                RecentAlerts.Add(new RowVm(title, text, a.At.LocalDateTime.ToString("g", L.Culture), a.Level == "Critical" ? Tone.Critical : Tone.Attention));
            }
            HasAlerts = RecentAlerts.Count > 0;
        }
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        alerts.Added += (_, _) => ui?.TryEnqueue(RefreshAlerts);
        RefreshAlerts();
        LastAnalysisLevel = settings.Current.LastAnalysisLevel;
        LastAnalysisAt = settings.Current.LastAnalysisAt;
        Cpu = new CpuCard();
        Ram = new RamCard();
        Charts =
        [
            new ChartCard("chart.cpu", MetricKeys.CpuLoad, null, MetricUnit.Percent, 100),
            new ChartCard("chart.gpu", MetricKeys.GpuLoad, null, MetricUnit.Percent, 100),
            new ChartCard("chart.memory", MetricKeys.RamLoad, null, MetricUnit.Percent, 100),
            new ChartCard("chart.disk", MetricKeys.DiskRead, MetricKeys.DiskWrite, MetricUnit.BytesPerSecond, double.NaN, "chart.read", "chart.write"),
            new ChartCard("chart.network", MetricKeys.NetDown, MetricKeys.NetUp, MetricUnit.BytesPerSecond, double.NaN, "chart.download", "chart.upload"),
        ];
    }

    // Cabeçalho
    [ObservableProperty] public partial string ComputerName { get; set; } = Environment.MachineName;
    [ObservableProperty] public partial string SystemLine { get; set; } = "";
    [ObservableProperty] public partial string UptimeText { get; set; } = "";
    [ObservableProperty] public partial bool IsElevated { get; set; } = true;
    [ObservableProperty] public partial bool DriverMissing { get; set; }

    // Diagnóstico
    [ObservableProperty] public partial HealthLevel LastAnalysisLevel { get; set; }
    [ObservableProperty] public partial DateTimeOffset? LastAnalysisAt { get; set; }
    [ObservableProperty] public partial bool HasReport { get; set; }
    [ObservableProperty] public partial string ReportSummary { get; set; } = "";
    public ObservableCollection<FindingItem> Findings { get; } = [];

    public string HealthText => L.Get($"health.{LastAnalysisLevel}");
    public string LastAnalysisText => LastAnalysisAt is { } at
        ? L.F("dashboard.lastAnalysis", Fmt.When(at, DateTimeOffset.Now))
        : L.Get("dashboard.neverAnalyzed");

    partial void OnLastAnalysisLevelChanged(HealthLevel value) => OnPropertyChanged(nameof(HealthText));
    partial void OnLastAnalysisAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(LastAnalysisText));

    // Cartões
    public CpuCard Cpu { get; }
    public RamCard Ram { get; }
    public ObservableCollection<GpuCard> Gpus { get; } = [];
    [ObservableProperty] public partial GpuCard? SelectedGpu { get; set; }

    partial void OnSelectedGpuChanged(GpuCard? oldValue, GpuCard? newValue)
    {
        if (newValue is null && oldValue is not null && Gpus.Contains(oldValue)) SelectedGpu = oldValue;
    }
    [ObservableProperty] public partial bool HasMultipleGpus { get; set; }
    [ObservableProperty] public partial bool HasGpu { get; set; } = true;
    public ObservableCollection<DriveItem> Drives { get; } = [];
    public IReadOnlyList<ChartCard> Charts { get; }
    [ObservableProperty] public partial string ChartsCaption { get; set; } = "";

    [ObservableProperty] public partial bool HasData { get; set; }

    public void Update(SystemSnapshot s)
    {
        HasData = true;
        var info = _engine.Info;
        if (info is not null)
        {
            ComputerName = info.ComputerName;
            SystemLine = string.Join("  ·  ", new[] { info.OsName, info.OsDisplayVersion, L.F("dashboard.build", info.OsBuild) }
                .Where(p => !string.IsNullOrWhiteSpace(p)));
            IsElevated = info.IsElevated;
            DriverMissing = info.IsElevated && !info.SensorDriverInstalled;
        }
        UptimeText = L.F("dashboard.uptime", Fmt.Duration(TimeSpan.FromMilliseconds(Environment.TickCount64)));

        Cpu.Update(s.Cpu);
        Ram.Update(s.Memory);
        UpdateGpus(s.Gpus);
        UpdateDrives(s.Drives);

        double seconds = _engine.EffectiveInterval.TotalSeconds;
        ChartsCaption = L.F("dashboard.chartsCaption", Fmt.Number(LiveSlots * seconds, 0), Fmt.Number(seconds, seconds < 1 ? 1 : 0));
        var buffer = new double[LiveSlots];
        foreach (var chart in Charts) chart.Update(_engine.Live, buffer, seconds);
    }

    private void UpdateGpus(IReadOnlyList<GpuState> gpus)
    {
        // Mantém a mesma instância por GPU (seleção e bindings sobrevivem às atualizações).
        var ordered = gpus.OrderByDescending(g => g.IsDiscrete).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i >= Gpus.Count) Gpus.Add(new GpuCard());
            Gpus[i].Update(ordered[i]);
        }
        while (Gpus.Count > ordered.Count) Gpus.RemoveAt(Gpus.Count - 1);
        HasGpu = Gpus.Count > 0;
        HasMultipleGpus = Gpus.Count > 1;
        if (SelectedGpu is null || !Gpus.Contains(SelectedGpu)) SelectedGpu = Gpus.FirstOrDefault();
    }

    private void UpdateDrives(IReadOnlyList<DriveState> drives)
    {
        for (int i = 0; i < drives.Count; i++)
        {
            if (i >= Drives.Count) Drives.Add(new DriveItem());
            Drives[i].Update(drives[i]);
        }
        while (Drives.Count > drives.Count) Drives.RemoveAt(Drives.Count - 1);
    }

    [RelayCommand]
    private void RunDiagnostics()
    {
        var snapshot = _engine.Latest;
        var info = _engine.Info;
        if (snapshot is null || info is null)
        {
            ReportSummary = L.Get("diagnostics.notReady");
            Findings.Clear();
            HasReport = true;
            return;
        }

        int samples = _engine.Live.Count;
        var recent = new RecentAverages(
            _engine.Live.RecentAverage(MetricKeys.CpuLoad, 60),
            _engine.Live.RecentAverage(MetricKeys.RamLoad, 60),
            Math.Min(samples, 60));
        var report = HealthAnalyzer.Analyze(snapshot, info, recent, DateTimeOffset.Now);

        Findings.Clear();
        foreach (var f in report.Findings) Findings.Add(FindingItem.From(f));
        ReportSummary = report.Findings.Count(f => f.Severity != FindingSeverity.Info) is var problems && problems == 0
            ? L.F("diagnostics.summaryClean", report.ChecksRun)
            : L.F("diagnostics.summaryProblems", report.ChecksRun, problems);
        HasReport = true;

        LastAnalysisLevel = report.Level;
        LastAnalysisAt = report.At;
        _settings.Update(s =>
        {
            s.LastAnalysisAt = report.At;
            s.LastAnalysisLevel = report.Level;
        });
    }

    [RelayCommand]
    private void CloseReport() => HasReport = false;

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (SystemIntegration.RestartElevated()) Microsoft.UI.Xaml.Application.Current.Exit();
    }

    [RelayCommand]
    private void OpenDriverSite() => SystemIntegration.OpenUrl(SystemIntegration.PawnIoUrl);
}

public sealed partial class CpuCard : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial double LoadPercent { get; set; } = double.NaN;
    [ObservableProperty] public partial string LoadText { get; set; } = "—";
    [ObservableProperty] public partial IReadOnlyList<double> ThreadLoads { get; set; } = [];
    [ObservableProperty] public partial string ThreadCaption { get; set; } = "";
    public MetricText Temperature { get; } = new();
    public MetricText Clock { get; } = new();
    public MetricText Power { get; } = new();
    public MetricText Topology { get; } = new();

    public void Update(CpuState cpu)
    {
        Name = cpu.Name;
        LoadPercent = cpu.Load.Value ?? double.NaN;
        LoadText = cpu.Load.Value is { } v ? Fmt.Number(v, 0) : "—";
        ThreadLoads = cpu.ThreadLoads;
        ThreadCaption = L.F("cpu.threadCaption", cpu.ThreadLoads.Count);
        Temperature.Set(cpu.Temperature, Fmt.Temperature);
        Clock.Set(cpu.Clock, mhz => cpu.ClockSource == ClockSource.WindowsEstimate
            ? L.F("cpu.clockEstimated", Fmt.Clock(mhz))
            : Fmt.Clock(mhz));
        if (cpu.ClockSource == ClockSource.WindowsEstimate) Clock.Hint = L.Get("cpu.clockEstimatedHint");
        Power.Set(cpu.Power, Fmt.Watts);
        if (cpu.Cores > 0) Topology.SetText(L.F("cpu.topology", cpu.Cores, cpu.Threads));
        else Topology.SetMissing(Unavailable.NotSupported);
    }
}

public sealed partial class GpuCard : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial double LoadPercent { get; set; } = double.NaN;
    [ObservableProperty] public partial string LoadText { get; set; } = "—";
    [ObservableProperty] public partial double VramPercent { get; set; } = double.NaN;
    public MetricText Temperature { get; } = new();
    public MetricText Hotspot { get; } = new();
    public MetricText MemoryJunction { get; } = new();
    public MetricText Vram { get; } = new();
    public MetricText Clock { get; } = new();
    public MetricText Power { get; } = new();
    public MetricText Fan { get; } = new();

    public void Update(GpuState g)
    {
        Name = g.Name;
        LoadPercent = g.Load.Value ?? double.NaN;
        LoadText = g.Load.Value is { } v ? Fmt.Number(v, 0) : "—";
        Temperature.Set(g.Temperature, Fmt.Temperature);
        Hotspot.Set(g.Hotspot, Fmt.Temperature);
        MemoryJunction.Set(g.MemoryJunction, Fmt.Temperature);
        VramPercent = g.VramPercent.Value ?? double.NaN;
        if (g.VramUsedMb.Value is { } used && g.VramTotalMb.Value is { } total)
            Vram.SetText(L.F("common.ofTotal", Fmt.Megabytes(used), Fmt.Megabytes(total)));
        else
            Vram.Set(g.VramUsedMb, Fmt.Megabytes);
        Clock.Set(g.CoreClock, Fmt.Mhz);
        Power.Set(g.Power, Fmt.Watts);
        if (g.FanRpm.Value is { } rpm)
            Fan.SetText(g.FanPercent.Value is { } pct ? $"{Fmt.Rpm(rpm)} · {Fmt.Percent(pct)}" : Fmt.Rpm(rpm));
        else
            Fan.Set(g.FanPercent, Fmt.Percent);
        // Ventoinhas paradas em repouso (0 RPM) são normais em muitas placas: deixar isso claro.
        if (g.FanRpm.Value == 0) Fan.Hint = L.Get("gpu.fanStopHint");
    }

    public override string ToString() => Name;
}

public sealed partial class RamCard : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string UsedText { get; set; } = "—";
    [ObservableProperty] public partial string UsedUnit { get; set; } = "";
    [ObservableProperty] public partial string Caption { get; set; } = "";
    [ObservableProperty] public partial double LoadPercent { get; set; } = double.NaN;
    public MetricText Available { get; } = new();
    public MetricText Speed { get; } = new();
    public MetricText Modules { get; } = new();
    public MetricText Committed { get; } = new();

    public void Update(MemoryState m)
    {
        if (m.TotalBytes == 0) return;
        var (used, unit) = Units.Bytes(m.UsedBytes);
        UsedText = Fmt.Number(used, 1);
        UsedUnit = L.F("ram.inUse", unit);
        Caption = L.F("ram.caption", Fmt.Bytes(m.TotalBytes), Fmt.Percent(m.LoadPercent));
        LoadPercent = m.LoadPercent;
        Title = m.Type is null ? Fmt.Bytes(m.TotalBytes, 0) : $"{Fmt.Bytes(m.TotalBytes, 0)} {m.Type}";
        Available.SetText(Fmt.Bytes(m.AvailableBytes));
        Speed.Set(m.SpeedMts, v => Fmt.Number(v, 0) + " MT/s");
        Modules.SetText(m.ModuleSizes.Count == 0 ? "—"
            : m.ModuleSizes.Distinct().Count() == 1
                ? $"{m.ModuleSizes.Count} × {Fmt.Bytes(m.ModuleSizes[0], 0)}"
                : string.Join(" + ", m.ModuleSizes.Select(s => Fmt.Bytes(s, 0))));
        if (m.ModuleSizes.Count == 0) Modules.SetMissing(Unavailable.NotSupported);
        Committed.SetText(L.F("common.ofTotal", Fmt.Bytes(m.CommittedBytes), Fmt.Bytes(m.CommitLimitBytes)));
    }
}

public sealed partial class DriveItem : ObservableObject
{
    [ObservableProperty] public partial string Model { get; set; } = "";
    [ObservableProperty] public partial string KindText { get; set; } = "";
    [ObservableProperty] public partial string ActivityText { get; set; } = "";
    [ObservableProperty] public partial HealthLevel Health { get; set; }
    [ObservableProperty] public partial string HealthText { get; set; } = "";
    public MetricText Temperature { get; } = new();
    public ObservableCollection<VolumeItem> Volumes { get; } = [];

    public void Update(DriveState d)
    {
        Model = d.Model;
        KindText = d.Kind switch
        {
            DriveKind.NvmeSsd => "NVMe",
            DriveKind.Ssd => "SSD",
            DriveKind.Hdd => "HDD",
            DriveKind.Removable => "USB",
            _ => d.Bus,
        };
        Temperature.Set(d.Temperature, Fmt.Temperature);
        ActivityText = d.ReadBps.Value is { } r && d.WriteBps.Value is { } w
            ? L.F("drive.activity", Fmt.Rate(r), Fmt.Rate(w))
            : L.Get("drive.activityPending");
        Health = d.Health switch
        {
            DriveHealth.Healthy => HealthLevel.Good,
            DriveHealth.Warning => HealthLevel.Attention,
            DriveHealth.Unhealthy => HealthLevel.Critical,
            _ => HealthLevel.Unknown,
        };
        HealthText = L.Get($"drive.health.{d.Health}")
                     + (d.LifeRemaining.Value is { } life ? " · " + L.F("drive.life", Fmt.Percent(life)) : "");

        for (int i = 0; i < d.Volumes.Count; i++)
        {
            if (i >= Volumes.Count) Volumes.Add(new VolumeItem());
            Volumes[i].Update(d.Volumes[i]);
        }
        while (Volumes.Count > d.Volumes.Count) Volumes.RemoveAt(Volumes.Count - 1);
    }
}

public sealed partial class VolumeItem : ObservableObject
{
    [ObservableProperty] public partial string Root { get; set; } = "";
    [ObservableProperty] public partial string FreeText { get; set; } = "";
    [ObservableProperty] public partial double UsedPercent { get; set; }
    [ObservableProperty] public partial bool IsLow { get; set; }

    public void Update(VolumeState v)
    {
        Root = v.Root.TrimEnd('\\');
        FreeText = L.F("volume.free", Fmt.Bytes(v.FreeBytes, 0), Fmt.Bytes(v.TotalBytes, 0));
        UsedPercent = v.UsedPercent;
        IsLow = v.FreePercent < HealthAnalyzer.VolumeAttentionFreePercent;
    }
}

public sealed partial class ChartCard : ObservableObject
{
    private readonly MetricUnit _unit;

    public ChartCard(string titleKey, string primaryKey, string? secondaryKey, MetricUnit unit, double maximum,
        string? primaryLabelKey = null, string? secondaryLabelKey = null)
    {
        Title = L.Get(titleKey);
        PrimaryKey = primaryKey;
        SecondaryKey = secondaryKey;
        _unit = unit;
        Maximum = maximum;
        PrimaryLabel = primaryLabelKey is null ? null : L.Get(primaryLabelKey);
        SecondaryLabel = secondaryLabelKey is null ? null : L.Get(secondaryLabelKey);
        Formatter = v => Fmt.Metric(_unit, v);
    }

    public string Title { get; }
    public string PrimaryKey { get; }
    public string? SecondaryKey { get; }
    public double Maximum { get; }
    public string? PrimaryLabel { get; }
    public string? SecondaryLabel { get; }
    public bool HasSecondary => SecondaryKey is not null;
    public bool BinaryScale => _unit == MetricUnit.BytesPerSecond;
    public Func<double, string> Formatter { get; }

    [ObservableProperty] public partial double[]? Values { get; set; }
    [ObservableProperty] public partial double[]? SecondaryValues { get; set; }
    [ObservableProperty] public partial string CurrentText { get; set; } = "—";
    [ObservableProperty] public partial string SecondaryText { get; set; } = "";
    [ObservableProperty] public partial double SecondsPerSlot { get; set; } = 1;

    public void Update(LiveHistory live, double[] buffer, double secondsPerSlot)
    {
        SecondsPerSlot = secondsPerSlot;
        Values = Read(live, PrimaryKey, buffer);
        CurrentText = Last(Values) is { } v ? (PrimaryLabel is null ? "" : PrimaryLabel + " ") + Formatter(v) : Fmt.Unavailable;
        if (SecondaryKey is not null)
        {
            SecondaryValues = Read(live, SecondaryKey, buffer);
            SecondaryText = Last(SecondaryValues) is { } s ? SecondaryLabel + " " + Formatter(s) : "";
        }
    }

    private static double[] Read(LiveHistory live, string key, double[] buffer)
    {
        int n = live.Copy(key, buffer);
        return buffer.AsSpan(0, n).ToArray();
    }

    private static double? Last(double[]? values) =>
        values is { Length: > 0 } && double.IsFinite(values[^1]) ? values[^1] : null;
}

public sealed record FindingItem(FindingSeverity Severity, string Title, string Detail)
{
    public static FindingItem From(Finding f)
    {
        double E(string k) => f.Evidence.TryGetValue(k, out var v) ? v : double.NaN;
        string title = L.F($"finding.{f.Code}.title", f.Subject);
        string detail = f.Code switch
        {
            "volume.low" => L.F("finding.volume.low.detail", Fmt.Bytes(E("freeBytes")), Fmt.Percent(E("freePercent")), Fmt.Bytes(E("totalBytes"))),
            "drive.health" => L.F("finding.drive.health.detail", L.Get($"drive.health.{(DriveHealth)(int)E("status")}")),
            "drive.temp" => L.F("finding.drive.temp.detail", Fmt.Temperature(E("temperature")), Fmt.Temperature(E("threshold"))),
            "drive.life" => L.F("finding.drive.life.detail", Fmt.Percent(E("lifeRemaining"))),
            "memory.high" or "cpu.high" => L.F($"finding.{f.Code}.detail", Fmt.Percent(E("average")), (int)E("samples")),
            "cpu.sensors" => Fmt.Hint((Unavailable)(int)E("reason")),
            _ => "",
        };
        return new FindingItem(f.Severity, title, detail);
    }
}
