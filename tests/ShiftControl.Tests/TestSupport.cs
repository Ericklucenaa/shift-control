using ShiftControl.Core;

namespace ShiftControl.Tests;

/// <summary>Fonte de hardware falsa, só para testes. Nunca é usada pelo aplicativo.</summary>
internal sealed class FakeHardwareSource : IHardwareSource
{
    private readonly Func<long, SystemSnapshot> _sample;
    public Exception? InitializeError { get; set; }
    public int Samples;
    public bool Disposed;

    public FakeHardwareSource(Func<long, SystemSnapshot>? sample = null) => _sample = sample ?? (seq => Snapshots.Basic(seq));

    public SystemInfo Initialize()
    {
        if (InitializeError is not null) throw InitializeError;
        return Snapshots.Info();
    }

    public SystemSnapshot Sample(long sequence)
    {
        Interlocked.Increment(ref Samples);
        return _sample(sequence);
    }

    public void Dispose() => Disposed = true;
}

internal static class Snapshots
{
    public static SystemInfo Info(bool elevated = false, bool driver = false) =>
        new("TEST-PC", "Windows 11 Pro", "24H2", "26100.1", elevated, driver, null, null);

    public static SystemSnapshot Basic(long seq = 1, double cpuLoad = 20, double? cpuTemp = null,
        IReadOnlyList<DriveState>? drives = null, IReadOnlyList<GpuState>? gpus = null, double memLoad = 50)
    {
        var cpu = new CpuState("Test CPU", 8, 16, Reading.Of(cpuLoad),
            cpuTemp is { } t ? Reading.Of(t) : Reading.Missing(Unavailable.RequiresAdmin),
            Reading.Of(4000), ClockSource.WindowsEstimate, Reading.Missing(Unavailable.RequiresAdmin), [10, 20]);
        ulong total = 32UL << 30;
        var memory = new MemoryState(total, (ulong)(total * (1 - memLoad / 100)), 0, 0, Reading.Of(3200), "DDR4", [16UL << 30, 16UL << 30]);
        var sensors = new List<SensorReading>
        {
            new("/cpu/load/0", "CPU Total", "/cpu", "Test CPU", HardwareCategory.Cpu, SensorKind.Load, Reading.Of(cpuLoad)),
            new("/cpu/temp/0", "Core", "/cpu", "Test CPU", HardwareCategory.Cpu, SensorKind.Temperature, cpu.Temperature),
        };
        return new SystemSnapshot(seq, DateTimeOffset.Now, cpu, gpus ?? [Gpu()], memory, drives ?? [], NetworkState.Empty, sensors, TimeSpan.FromMilliseconds(1));
    }

    public static GpuState Gpu(double load = 30, GpuVendor vendor = GpuVendor.Nvidia, string name = "Test GPU") =>
        new($"/gpu/{name}", name, vendor, Reading.Of(load), Reading.Of(50), Reading.Missing(Unavailable.NoSensor),
            Reading.Of(52), Reading.Of(2048), Reading.Of(8192), Reading.Of(1800), Reading.Of(7000), Reading.Of(120),
            Reading.Of(0), Reading.Of(0));

    public static DriveState Drive(int index = 0, ulong free = 400UL << 30, ulong total = 1000UL << 30,
        double? temp = 40, double? warn = 70, double? crit = 80, DriveHealth health = DriveHealth.Healthy, double? life = null) =>
        new(index.ToString(), index, $"Disk {index}", DriveKind.NvmeSsd, "NVMe", total,
            [new VolumeState("C:\\", "", "NTFS", total, free)],
            temp is { } t ? Reading.Of(t) : Reading.Missing(Unavailable.NotSupported),
            warn is { } w ? Reading.Of(w) : Reading.Missing(Unavailable.NotSupported),
            crit is { } c ? Reading.Of(c) : Reading.Missing(Unavailable.NotSupported),
            Reading.Of(0), Reading.Of(0), Reading.Of(0), health,
            life is { } l ? Reading.Of(l) : Reading.Missing(Unavailable.RequiresAdmin));
}

/// <summary>Pasta temporária apagada ao final do teste.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shiftcontrol-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
