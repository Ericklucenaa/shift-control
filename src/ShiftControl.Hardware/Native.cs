using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ShiftControl.Hardware;

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(SafeFileHandle device, uint code, ref StoragePropertyQuery input, int inputSize,
        Span<byte> output, int outputSize, out int returned, nint overlapped);

    internal const uint IoctlStorageQueryProperty = 0x002D1400;
    internal const int StorageDeviceTemperatureProperty = 52;
    internal const uint FileShareReadWrite = 3;
    internal const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public int AdditionalParameters;
    }

    public static MemoryStatusEx MemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status : default;
    }

    /// <summary>
    /// Temperatura reportada pelo próprio disco (STORAGE_TEMPERATURE_DATA_DESCRIPTOR).
    /// Abre o disco com acesso 0 (só consulta), o que não exige administrador.
    /// </summary>
    public static DiskTemperature? QueryDiskTemperature(int physicalIndex)
    {
        using var handle = CreateFile(@"\\.\PhysicalDrive" + physicalIndex, 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid) return null;

        var query = new StoragePropertyQuery { PropertyId = StorageDeviceTemperatureProperty };
        Span<byte> buffer = stackalloc byte[256];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, ref query, Marshal.SizeOf<StoragePropertyQuery>(),
                buffer, buffer.Length, out int returned, 0) || returned < 32)
            return null;

        return ParseTemperatureDescriptor(buffer[..returned]);
    }

    /// <summary>
    /// Layout: Version(4) Size(4) CriticalTemperature(2) WarningTemperature(2) InfoCount(2) Reserved(2+8),
    /// depois STORAGE_TEMPERATURE_INFO[]: Index(2) Temperature(2) ...
    /// </summary>
    internal static DiskTemperature? ParseTemperatureDescriptor(ReadOnlySpan<byte> data)
    {
        if (data.Length < 28) return null;
        short critical = BitConverter.ToInt16(data[8..]);
        short warning = BitConverter.ToInt16(data[10..]);
        ushort count = BitConverter.ToUInt16(data[12..]);
        if (count == 0) return null;
        short temperature = BitConverter.ToInt16(data[26..]);
        // Valores sentinela (0x8000 / 0xFFFF) significam "não informado".
        static double? Valid(short v) => v is > -40 and < 150 && v != 0 ? v : null;
        var t = Valid(temperature);
        return t is null ? null : new DiskTemperature(t.Value, Valid(warning), Valid(critical));
    }
}

internal readonly record struct DiskTemperature(double Celsius, double? Warning, double? Critical);
