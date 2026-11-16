using ShiftControl.Core;

namespace ShiftControl.Hardware;

internal sealed class MemoryReader
{
    private readonly Reading _speed;
    private readonly string? _type;
    private readonly IReadOnlyList<ulong> _modules;

    public MemoryReader()
    {
        var modules = Wmi.All("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory",
            o => (Capacity: Convert.ToUInt64(o["Capacity"] ?? 0UL),
                  Speed: Convert.ToDouble(o["Speed"] ?? 0),
                  Configured: Convert.ToDouble(o["ConfiguredClockSpeed"] ?? 0),
                  Type: Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0)));
        _modules = modules.Select(m => m.Capacity).Where(c => c > 0).ToList();

        // ConfiguredClockSpeed é a velocidade em uso (respeita XMP/EXPO); Speed é a nominal do módulo.
        double speed = modules.Select(m => m.Configured > 0 ? m.Configured : m.Speed).DefaultIfEmpty(0).Min();
        _speed = speed > 0 ? Reading.Of(speed) : Reading.Missing(Unavailable.NotSupported);
        _type = modules.Select(m => MemoryTypeName(m.Type)).FirstOrDefault(t => t is not null);
    }

    internal static string? MemoryTypeName(int smbiosType) => smbiosType switch
    {
        18 => "DDR",
        19 => "DDR2",
        20 => "DDR2 FB-DIMM",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };

    public MemoryState Read()
    {
        var status = Native.MemoryStatus();
        return new MemoryState(
            status.TotalPhys,
            status.AvailPhys,
            status.TotalPageFile - status.AvailPageFile,
            status.TotalPageFile,
            _speed,
            _type,
            _modules);
    }
}
