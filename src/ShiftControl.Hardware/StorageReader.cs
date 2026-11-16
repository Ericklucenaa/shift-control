using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using ShiftControl.Core;

namespace ShiftControl.Hardware;

/// <summary>
/// Discos físicos: identificação e saúde (Storage Management/WMI), atividade (contadores PhysicalDisk),
/// temperatura (IOCTL do próprio disco, sem admin) e volumes. Com admin, o LibreHardwareMonitor
/// complementa vida útil e temperatura de discos que não respondem ao IOCTL.
/// </summary>
internal sealed class StorageReader : IDisposable
{
    private static readonly TimeSpan InventoryEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CountersEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VolumesEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SsdTemperatureEvery = TimeSpan.FromSeconds(10);
    // HDDs: consulta espaçada para não atrapalhar o desligamento do motor por inatividade.
    private static readonly TimeSpan HddTemperatureEvery = TimeSpan.FromMinutes(2);

    private sealed record DiskInfo(int Index, string Model, DriveKind Kind, string Bus, ulong Size, DriveHealth Health);

    private sealed class DiskCounters(PerformanceCounter read, PerformanceCounter write, PerformanceCounter idle) : IDisposable
    {
        public PerformanceCounter Read { get; } = read;
        public PerformanceCounter Write { get; } = write;
        public PerformanceCounter Idle { get; } = idle;
        public bool Primed { get; set; }

        public void Dispose()
        {
            Read.Dispose();
            Write.Dispose();
            Idle.Dispose();
        }
    }

    private sealed class TemperatureCache
    {
        public DiskTemperature? Value;
        public bool Unsupported;
        public long LastQuery = long.MinValue;
    }

    private readonly Dictionary<int, DiskCounters> _counters = [];
    private readonly Dictionary<int, string[]> _letters = [];
    private readonly Dictionary<int, TemperatureCache> _temperatures = [];
    private List<DiskInfo> _disks = [];
    private List<VolumeState> _volumes = [];
    private long _lastInventory = long.MinValue;
    private long _lastCounters = long.MinValue;
    private long _lastVolumes = long.MinValue;
    private bool _countersFailed;

    public IReadOnlyList<DriveState> Read(IReadOnlyList<IHardware> lhmStorage)
    {
        if (Elapsed(ref _lastInventory, InventoryEvery)) _disks = ReadInventory();
        if (!_countersFailed && Elapsed(ref _lastCounters, CountersEvery)) RefreshCounters();
        if (Elapsed(ref _lastVolumes, VolumesEvery)) _volumes = ReadVolumes();

        var result = new List<DriveState>(_disks.Count);
        foreach (var disk in _disks)
        {
            var lhm = lhmStorage.FirstOrDefault(h => LhmIndex(h) == disk.Index);
            var (read, write, active) = ReadActivity(disk.Index);
            var temp = ReadTemperature(disk, lhm);
            var letters = _letters.TryGetValue(disk.Index, out var l) ? l : [];
            var volumes = _volumes.Where(v => letters.Contains(v.Root[..2], StringComparer.OrdinalIgnoreCase)).ToList();

            result.Add(new DriveState(
                disk.Index.ToString(),
                disk.Index,
                disk.Model,
                disk.Kind,
                disk.Bus,
                disk.Size,
                volumes,
                temp.Value is { } t ? Reading.Of(t.Celsius) : Reading.Missing(temp.Reason),
                temp.Value?.Warning is { } w ? Reading.Of(w) : Reading.Missing(Unavailable.NotSupported),
                temp.Value?.Critical is { } c ? Reading.Of(c) : Reading.Missing(Unavailable.NotSupported),
                read,
                write,
                active,
                disk.Health,
                LifeRemaining(lhm)));
        }
        return result;
    }

    private static bool Elapsed(ref long last, TimeSpan every)
    {
        long now = Stopwatch.GetTimestamp();
        if (last != long.MinValue && Stopwatch.GetElapsedTime(last, now) < every) return false;
        last = now;
        return true;
    }

    private static List<DiskInfo> ReadInventory() =>
        Wmi.All("SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk",
                o =>
                {
                    int bus = Convert.ToInt32(o["BusType"] ?? 0);
                    int media = Convert.ToInt32(o["MediaType"] ?? 0);
                    var kind = bus == 7 ? DriveKind.Removable
                        : bus == 17 ? DriveKind.NvmeSsd
                        : media == 3 ? DriveKind.Hdd
                        : media == 4 ? DriveKind.Ssd
                        : DriveKind.Unknown;
                    return new DiskInfo(
                        int.TryParse(o["DeviceId"]?.ToString(), out var idx) ? idx : -1,
                        (o["FriendlyName"] as string ?? "Disco").Trim(),
                        kind,
                        BusName(bus),
                        Convert.ToUInt64(o["Size"] ?? 0UL),
                        Convert.ToInt32(o["HealthStatus"] ?? 5) switch
                        {
                            0 => DriveHealth.Healthy,
                            1 => DriveHealth.Warning,
                            2 => DriveHealth.Unhealthy,
                            _ => DriveHealth.Unknown,
                        });
                }, @"root\Microsoft\Windows\Storage")
            .Where(d => d.Index >= 0)
            .OrderBy(d => d.Index)
            .ToList();

    internal static string BusName(int bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "File Backed Virtual", 16 => "Storage Spaces",
        17 => "NVMe", 18 => "SCM", 19 => "UFS",
        _ => "",
    };

    private void RefreshCounters()
    {
        try
        {
            var category = new PerformanceCounterCategory("PhysicalDisk");
            var instances = category.GetInstanceNames().Where(n => n != "_Total").ToList();
            var seen = new HashSet<int>();
            foreach (var instance in instances)
            {
                // Formato "2 C: E:": número do disco seguido das letras dos volumes.
                var parts = instance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !int.TryParse(parts[0], out int index)) continue;
                seen.Add(index);
                _letters[index] = parts.Skip(1).ToArray();
                if (_counters.ContainsKey(index)) continue;
                _counters[index] = new DiskCounters(
                    new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instance, true),
                    new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instance, true),
                    new PerformanceCounter("PhysicalDisk", "% Idle Time", instance, true));
            }
            foreach (var gone in _counters.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _counters[gone].Dispose();
                _counters.Remove(gone);
                _letters.Remove(gone);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _countersFailed = true;
        }
    }

    private (Reading Read, Reading Write, Reading Active) ReadActivity(int index)
    {
        var unsupported = Reading.Missing(Unavailable.NotSupported);
        if (_countersFailed || !_counters.TryGetValue(index, out var c)) return (unsupported, unsupported, unsupported);
        try
        {
            double r = c.Read.NextValue(), w = c.Write.NextValue(), idle = c.Idle.NextValue();
            if (!c.Primed)
            {
                // Contadores de taxa precisam de duas leituras; a primeira sempre volta 0.
                c.Primed = true;
                var pending = Reading.Missing(Unavailable.Pending);
                return (pending, pending, pending);
            }
            return (Reading.Of(r), Reading.Of(w), Reading.Of(Math.Clamp(100 - idle, 0, 100)));
        }
        catch (InvalidOperationException)
        {
            // Disco removido entre duas leituras: os contadores serão recriados no próximo inventário.
            c.Dispose();
            _counters.Remove(index);
            var error = Reading.Missing(Unavailable.Error);
            return (error, error, error);
        }
    }

    private (DiskTemperature? Value, Unavailable Reason) ReadTemperature(DiskInfo disk, IHardware? lhm)
    {
        if (!_temperatures.TryGetValue(disk.Index, out var cache))
            _temperatures[disk.Index] = cache = new TemperatureCache();

        if (!cache.Unsupported)
        {
            var every = disk.Kind == DriveKind.Hdd ? HddTemperatureEvery : SsdTemperatureEvery;
            if (Elapsed(ref cache.LastQuery, every))
            {
                cache.Value = Native.QueryDiskTemperature(disk.Index);
                if (cache.Value is null) cache.Unsupported = true;
            }
            if (cache.Value is not null) return (cache.Value, Unavailable.None);
        }

        // Alguns SSDs SATA não respondem ao IOCTL; com admin, o SMART lido pelo LHM costuma ter a temperatura.
        var lhmTemp = lhm?.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150);
        if (lhmTemp is not null) return (new DiskTemperature(lhmTemp.Value!.Value, null, null), Unavailable.None);

        return (null, lhm is null && !SystemInfoReader.IsElevatedCached ? Unavailable.RequiresAdmin : Unavailable.NotSupported);
    }

    private static Reading LifeRemaining(IHardware? lhm)
    {
        if (lhm is null) return Reading.Missing(SystemInfoReader.IsElevatedCached ? Unavailable.NotSupported : Unavailable.RequiresAdmin);
        var used = lhm.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Level && s.Name == "Percentage Used");
        if (used?.Value is { } u) return Reading.Of(Math.Clamp(100 - u, 0, 100));
        var remaining = lhm.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Level
                                                        && (s.Name.Contains("Remaining Life", StringComparison.OrdinalIgnoreCase)
                                                            || s.Name.Equals("Life", StringComparison.OrdinalIgnoreCase)));
        return remaining?.Value is { } r ? Reading.Of(Math.Clamp(r, 0, 100)) : Reading.Missing(Unavailable.NotSupported);
    }

    /// <summary>Identificadores do LHM terminam no número do disco físico ("/nvme/2", "/hdd/0").</summary>
    internal static int LhmIndex(IHardware hw) => LhmIndex(hw.Identifier.ToString());

    internal static int LhmIndex(string identifier)
    {
        var last = identifier.TrimEnd('/').Split('/').LastOrDefault();
        return int.TryParse(last, out int i) ? i : -1;
    }

    private static List<VolumeState> ReadVolumes()
    {
        var list = new List<VolumeState>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                list.Add(new VolumeState(d.RootDirectory.FullName, d.VolumeLabel, d.DriveFormat, (ulong)d.TotalSize, (ulong)d.AvailableFreeSpace));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    public void Dispose()
    {
        foreach (var c in _counters.Values) c.Dispose();
        _counters.Clear();
    }
}
