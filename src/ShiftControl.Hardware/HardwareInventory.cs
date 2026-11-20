using ShiftControl.Diagnostics;

namespace ShiftControl.Hardware;

public sealed record ProcessorSpec(string? Name, uint Cores, uint Threads);
public sealed record PhysicalDriveSpec(string? Model, ulong CapacityBytes);

public sealed record HardwareInventory(
    IReadOnlyList<ProcessorSpec> Processors,
    IReadOnlyList<string> GraphicsAdapters,
    IReadOnlyList<MemoryModuleFact> MemoryModules,
    string? Motherboard,
    string? Bios,
    IReadOnlyList<PhysicalDriveSpec> Drives)
{
    public static HardwareInventory Read() => new(
        Wmi.All("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor",
            o => new ProcessorSpec((o["Name"] as string)?.Trim(),
                (uint?)o["NumberOfCores"] ?? 0, (uint?)o["NumberOfLogicalProcessors"] ?? 0)),
        SystemFactsProbe.ReadGpus(),
        SystemFactsProbe.ReadMemory(),
        SystemInfoReader.ReadMotherboard(),
        SystemInfoReader.ReadBios(),
        Wmi.All("SELECT Model, Size FROM Win32_DiskDrive",
            o => new PhysicalDriveSpec((o["Model"] as string)?.Trim(), (ulong?)o["Size"] ?? 0)));
}
