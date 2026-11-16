namespace ShiftControl.Core;

public enum MetricUnit { Percent, Celsius, MHz, Watts, BytesPerSecond }

public sealed record MetricDefinition(string Key, MetricUnit Unit, double? FixedMax);

/// <summary>Séries gravadas no histórico e exibidas nos gráficos.</summary>
public static class MetricKeys
{
    public const string CpuLoad = "cpu.load";
    public const string CpuTemp = "cpu.temp";
    public const string CpuClock = "cpu.clock";
    public const string CpuPower = "cpu.power";
    public const string GpuLoad = "gpu.load";
    public const string GpuTemp = "gpu.temp";
    public const string GpuHotspot = "gpu.hotspot";
    public const string GpuVram = "gpu.vram";
    public const string GpuClock = "gpu.clock";
    public const string GpuPower = "gpu.power";
    public const string RamLoad = "ram.load";
    public const string DiskRead = "disk.read";
    public const string DiskWrite = "disk.write";
    public const string NetDown = "net.down";
    public const string NetUp = "net.up";

    public static IReadOnlyList<MetricDefinition> All { get; } =
    [
        new(CpuLoad, MetricUnit.Percent, 100),
        new(CpuTemp, MetricUnit.Celsius, null),
        new(CpuClock, MetricUnit.MHz, null),
        new(CpuPower, MetricUnit.Watts, null),
        new(GpuLoad, MetricUnit.Percent, 100),
        new(GpuTemp, MetricUnit.Celsius, null),
        new(GpuHotspot, MetricUnit.Celsius, null),
        new(GpuVram, MetricUnit.Percent, 100),
        new(GpuClock, MetricUnit.MHz, null),
        new(GpuPower, MetricUnit.Watts, null),
        new(RamLoad, MetricUnit.Percent, 100),
        new(DiskRead, MetricUnit.BytesPerSecond, null),
        new(DiskWrite, MetricUnit.BytesPerSecond, null),
        new(NetDown, MetricUnit.BytesPerSecond, null),
        new(NetUp, MetricUnit.BytesPerSecond, null),
    ];

    public static MetricDefinition? Find(string key) => All.FirstOrDefault(m => m.Key == key);
}

public static class MetricExtractor
{
    /// <summary>Extrai as séries conhecidas de um snapshot. Métricas indisponíveis ficam de fora (nunca viram zero).</summary>
    public static IEnumerable<KeyValuePair<string, double>> Extract(SystemSnapshot s)
    {
        foreach (var (key, reading) in Readings(s))
            if (reading.Value is { } v)
                yield return new(key, v);
    }

    public static IEnumerable<(string Key, Reading Value)> Readings(SystemSnapshot s)
    {
        yield return (MetricKeys.CpuLoad, s.Cpu.Load);
        yield return (MetricKeys.CpuTemp, s.Cpu.Temperature);
        yield return (MetricKeys.CpuClock, s.Cpu.Clock);
        yield return (MetricKeys.CpuPower, s.Cpu.Power);

        var gpu = s.PrimaryGpu;
        var none = Reading.Missing(Unavailable.NotSupported);
        yield return (MetricKeys.GpuLoad, gpu?.Load ?? none);
        yield return (MetricKeys.GpuTemp, gpu?.Temperature ?? none);
        yield return (MetricKeys.GpuHotspot, gpu?.Hotspot ?? none);
        yield return (MetricKeys.GpuVram, gpu?.VramPercent ?? none);
        yield return (MetricKeys.GpuClock, gpu?.CoreClock ?? none);
        yield return (MetricKeys.GpuPower, gpu?.Power ?? none);

        yield return (MetricKeys.RamLoad, s.Memory.TotalBytes > 0 ? Reading.Of(s.Memory.LoadPercent) : Reading.Missing(Unavailable.Pending));

        bool disksReady = s.Drives.Any(d => d.ReadBps.HasValue);
        yield return (MetricKeys.DiskRead, disksReady ? Reading.Of(s.DiskReadBps) : Reading.Missing(Unavailable.Pending));
        yield return (MetricKeys.DiskWrite, disksReady ? Reading.Of(s.DiskWriteBps) : Reading.Missing(Unavailable.Pending));
        yield return (MetricKeys.NetDown, s.Network.Ready ? Reading.Of(s.Network.DownBps) : Reading.Missing(Unavailable.Pending));
        yield return (MetricKeys.NetUp, s.Network.Ready ? Reading.Of(s.Network.UpBps) : Reading.Missing(Unavailable.Pending));
    }
}
