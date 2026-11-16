namespace ShiftControl.Core;

public enum GpuVendor { Other, Nvidia, Amd, Intel }

public enum DriveKind { Unknown, Hdd, Ssd, NvmeSsd, Removable }

public enum DriveHealth { Unknown, Healthy, Warning, Unhealthy }

/// <summary>De onde veio a frequência da CPU exibida.</summary>
public enum ClockSource { None, Sensor, WindowsEstimate }

public enum HardwareCategory { Cpu, Gpu, Memory, Storage, Network, Motherboard, Controller, Battery, Psu, Other }

public enum SensorKind
{
    Other, Temperature, Load, Clock, Power, Voltage, Current, Fan, Control, Data, SmallData,
    Throughput, Level, Factor, Frequency, Energy, Noise, TimeSpan, Humidity, Flow,
}

public sealed record CpuState(
    string Name,
    int Cores,
    int Threads,
    Reading Load,
    Reading Temperature,
    Reading Clock,
    ClockSource ClockSource,
    Reading Power,
    IReadOnlyList<double> ThreadLoads)
{
    public static CpuState Empty(string name = "") =>
        new(name, 0, 0, Reading.Missing(Unavailable.Pending), Reading.Missing(Unavailable.Pending),
            Reading.Missing(Unavailable.Pending), ClockSource.None, Reading.Missing(Unavailable.Pending), []);
}

public sealed record GpuState(
    string Id,
    string Name,
    GpuVendor Vendor,
    Reading Load,
    Reading Temperature,
    Reading Hotspot,
    Reading MemoryJunction,
    Reading VramUsedMb,
    Reading VramTotalMb,
    Reading CoreClock,
    Reading MemoryClock,
    Reading Power,
    Reading FanRpm,
    Reading FanPercent)
{
    public Reading VramPercent =>
        VramUsedMb.Value is { } used && VramTotalMb.Value is { } total && total > 0
            ? Reading.Of(used / total * 100)
            : Reading.Missing(VramUsedMb.HasValue ? VramTotalMb.Reason : VramUsedMb.Reason);

    public bool IsDiscrete => Vendor is GpuVendor.Nvidia or GpuVendor.Amd;
}

public sealed record MemoryState(
    ulong TotalBytes,
    ulong AvailableBytes,
    ulong CommittedBytes,
    ulong CommitLimitBytes,
    Reading SpeedMts,
    string? Type,
    IReadOnlyList<ulong> ModuleSizes)
{
    public ulong UsedBytes => TotalBytes >= AvailableBytes ? TotalBytes - AvailableBytes : 0;
    public double LoadPercent => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes * 100;

    public static MemoryState Empty { get; } = new(0, 0, 0, 0, Reading.Missing(Unavailable.Pending), null, []);
}

public sealed record VolumeState(string Root, string Label, string FileSystem, ulong TotalBytes, ulong FreeBytes)
{
    public double UsedPercent => TotalBytes == 0 ? 0 : (double)(TotalBytes - FreeBytes) / TotalBytes * 100;
    public double FreePercent => TotalBytes == 0 ? 0 : (double)FreeBytes / TotalBytes * 100;
}

public sealed record DriveState(
    string Id,
    int Index,
    string Model,
    DriveKind Kind,
    string Bus,
    ulong SizeBytes,
    IReadOnlyList<VolumeState> Volumes,
    Reading Temperature,
    Reading WarningTemperature,
    Reading CriticalTemperature,
    Reading ReadBps,
    Reading WriteBps,
    Reading ActivePercent,
    DriveHealth Health,
    Reading LifeRemaining);

public sealed record AdapterState(string Id, string Name, string Description, string Kind, long LinkSpeedBps, double DownBps, double UpBps);

public sealed record NetworkState(IReadOnlyList<AdapterState> Adapters, bool Ready)
{
    public double DownBps => Adapters.Sum(a => a.DownBps);
    public double UpBps => Adapters.Sum(a => a.UpBps);
    public static NetworkState Empty { get; } = new([], false);
}

/// <summary>Um sensor bruto, como o hardware reporta, já validado.</summary>
public sealed record SensorReading(
    string Id,
    string Name,
    string HardwareId,
    string HardwareName,
    HardwareCategory Category,
    SensorKind Kind,
    Reading Value);

/// <summary>Foto completa do sistema em um instante. Imutável: pode ser lida por qualquer thread.</summary>
public sealed record SystemSnapshot(
    long Sequence,
    DateTimeOffset Time,
    CpuState Cpu,
    IReadOnlyList<GpuState> Gpus,
    MemoryState Memory,
    IReadOnlyList<DriveState> Drives,
    NetworkState Network,
    IReadOnlyList<SensorReading> Sensors,
    TimeSpan SampleCost)
{
    /// <summary>GPU principal: a primeira dedicada; se não houver, a primeira encontrada.</summary>
    public GpuState? PrimaryGpu => Gpus.FirstOrDefault(g => g.IsDiscrete) ?? Gpus.FirstOrDefault();

    public double DiskReadBps => Drives.Sum(d => d.ReadBps.Or(0));
    public double DiskWriteBps => Drives.Sum(d => d.WriteBps.Or(0));
}

/// <summary>Informações estáticas, lidas uma vez na inicialização.</summary>
public sealed record SystemInfo(
    string ComputerName,
    string OsName,
    string OsDisplayVersion,
    string OsBuild,
    bool IsElevated,
    bool SensorDriverInstalled,
    string? Motherboard,
    string? Bios)
{
    /// <summary>Sensores de baixo nível da CPU (temperatura, clock, consumo) exigem admin + driver.</summary>
    public bool CpuSensorsAccessible => IsElevated && SensorDriverInstalled;

    public Unavailable CpuSensorReason =>
        !IsElevated ? Unavailable.RequiresAdmin : !SensorDriverInstalled ? Unavailable.RequiresDriver : Unavailable.None;
}
