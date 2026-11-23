using System.Globalization;
using System.Management;
using System.Text;
using Microsoft.Win32;
using ShiftControl.Core;

namespace ShiftControl.Hardware;

public sealed record ProcessorSpec(string? Name, uint Cores, uint Threads, uint MaxMhz = 0, string? Socket = null, uint L2Kb = 0, uint L3Kb = 0);

public sealed record GpuSpec(string Name, ulong? VramBytes, string? DriverVersion, DateTime? DriverDate, int Width = 0, int Height = 0, int RefreshHz = 0);

public sealed record MemoryModuleSpec(string? Slot, ulong CapacityBytes, int RatedMts, int ConfiguredMts, string? Type, string? Manufacturer, string? PartNumber)
{
    /// <summary>Configurada abaixo da velocidade nominal do módulo: em geral XMP/EXPO desligado na BIOS.</summary>
    public bool BelowRated => RatedMts > 0 && ConfiguredMts > 0 && ConfiguredMts < RatedMts;
}

public sealed record BoardSpec(string? Manufacturer, string? Product, string? BiosVendor, string? BiosVersion, DateTime? BiosDate);

public sealed record DriveSpec(string? Model, ulong CapacityBytes, DriveKind Kind, string? Bus);

public sealed record MonitorSpec(string? Manufacturer, string? Name);

public sealed record NetworkAdapterSpec(string Name, ulong? SpeedBps);

/// <summary>
/// As peças do computador como o Windows as identifica (WMI, SMBIOS e registro de vídeo).
/// Dispositivos virtuais (Parsec, VPNs, áudio virtual da NVIDIA) ficam de fora: não são peças.
/// </summary>
public sealed record HardwareInventory(
    IReadOnlyList<ProcessorSpec> Processors,
    IReadOnlyList<GpuSpec> Gpus,
    IReadOnlyList<MemoryModuleSpec> MemoryModules,
    int MemorySlots,
    BoardSpec Board,
    IReadOnlyList<DriveSpec> Drives,
    IReadOnlyList<MonitorSpec> Monitors,
    IReadOnlyList<NetworkAdapterSpec> Network,
    IReadOnlyList<string> Audio)
{
    public ulong TotalMemoryBytes => MemoryModules.Aggregate(0UL, (sum, m) => sum + m.CapacityBytes);

    /// <summary>Tipo de memória comum a todos os módulos ("DDR4"), ou null se misturado/desconhecido.</summary>
    public string? MemoryType => MemoryModules.Select(m => m.Type).Distinct().ToList() is [{ } only] ? only : null;

    public static HardwareInventory Read()
    {
        var vram = ReadVramFromRegistry();
        return new(
            Safe(() => Wmi.All("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation, L2CacheSize, L3CacheSize FROM Win32_Processor",
                o => new ProcessorSpec(Str(o["Name"]), U(o["NumberOfCores"]), U(o["NumberOfLogicalProcessors"]), U(o["MaxClockSpeed"]),
                    Str(o["SocketDesignation"]), U(o["L2CacheSize"]), U(o["L3CacheSize"])))),
            Safe(() => Wmi.All("SELECT Name, PNPDeviceID, AdapterRAM, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController",
                    o => (Pnp: Str(o["PNPDeviceID"]), Gpu: new GpuSpec(Str(o["Name"]) ?? "", null, Str(o["DriverVersion"]), CimDate(o["DriverDate"]),
                        (int)U(o["CurrentHorizontalResolution"]), (int)U(o["CurrentVerticalResolution"]), (int)U(o["CurrentRefreshRate"]))))
                .Where(x => x.Gpu.Name.Length > 0 && IsPhysicalDevice(x.Pnp))
                // AdapterRAM é um inteiro de 32 bits (no máximo 4 GB): o valor real vem do registro do driver.
                .Select(x => x.Gpu with { VramBytes = vram.TryGetValue(x.Gpu.Name, out var bytes) ? bytes : null })
                .DistinctBy(g => g.Name).ToList()),
            Safe(() => Wmi.All("SELECT DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Manufacturer, PartNumber FROM Win32_PhysicalMemory",
                o => new MemoryModuleSpec(Str(o["DeviceLocator"]), Convert.ToUInt64(o["Capacity"] ?? 0UL, CultureInfo.InvariantCulture), (int)U(o["Speed"]),
                    (int)U(o["ConfiguredClockSpeed"]), MemoryTypeName(U(o["SMBIOSMemoryType"])), CleanVendor(Str(o["Manufacturer"])), Str(o["PartNumber"])))),
            Wmi.All("SELECT MemoryDevices, Use FROM Win32_PhysicalMemoryArray", o => (Slots: (int)U(o["MemoryDevices"]), Use: U(o["Use"])))
                .Where(a => a.Use == 3 /* memória do sistema */).Sum(a => a.Slots),
            ReadBoard(),
            Safe(ReadDrives),
            Safe(ReadMonitors),
            Safe(() => Wmi.All("SELECT Name, Speed, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE",
                    o => (Pnp: Str(o["PNPDeviceID"]), Spec: new NetworkAdapterSpec(Str(o["Name"]) ?? "", o["Speed"] is { } s ? Convert.ToUInt64(s, CultureInfo.InvariantCulture) : null)))
                .Where(x => x.Spec.Name.Length > 0 && IsPhysicalDevice(x.Pnp)).Select(x => x.Spec).ToList()),
            Safe(() => Wmi.All("SELECT Name, PNPDeviceID FROM Win32_SoundDevice", o => (Pnp: Str(o["PNPDeviceID"]), Name: Str(o["Name"]) ?? ""))
                .Where(x => x.Name.Length > 0 && IsPhysicalDevice(x.Pnp)).Select(x => x.Name).Distinct().ToList()));
    }

    /// <summary>Peça física: ligada ao barramento PCI, USB ou HD Audio. ROOT\ e SWD\ são dispositivos criados por software.</summary>
    public static bool IsPhysicalDevice(string? pnpId) =>
        pnpId is { Length: > 0 } id && (id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Código SMBIOS do tipo de memória (Win32_PhysicalMemory.SMBIOSMemoryType).</summary>
    public static string? MemoryTypeName(uint code) => code switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4",
        34 => "DDR5", 35 => "LPDDR5", _ => null,
    };

    /// <summary>MSFT_PhysicalDisk: BusType 17 = NVMe, 7 = USB; MediaType 3 = HDD, 4 = SSD (mesma regra da tela de Discos).</summary>
    public static DriveKind DriveKindFrom(uint mediaType, uint busType = 0) => busType switch
    {
        7 => DriveKind.Removable,
        17 => DriveKind.NvmeSsd,
        _ => mediaType switch { 3 => DriveKind.Hdd, 4 => DriveKind.Ssd, _ => DriveKind.Unknown },
    };

    /// <summary>MSFT_PhysicalDisk.BusType nos nomes que o usuário conhece.</summary>
    public static string? BusName(uint busType) => busType switch
    {
        7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 17 => "NVMe", _ => null,
    };

    /// <summary>
    /// Resumo em uma linha ("Ryzen 7 5800X3D · RTX 5070 12 GB · 32 GB DDR4 3000 MT/s · TUF GAMING B550M-PLUS"),
    /// para o topo da tela e para colar em fóruns.
    /// </summary>
    public string Headline(Func<double, string> bytes)
    {
        var parts = new List<string>();
        if (Processors.FirstOrDefault()?.Name is { } cpu) parts.Add(ShortCpu(cpu));
        if (Gpus.FirstOrDefault() is { } gpu) parts.Add(gpu.VramBytes is { } v ? $"{ShortGpu(gpu.Name)} {bytes(v)}" : ShortGpu(gpu.Name));
        if (TotalMemoryBytes > 0)
        {
            int mts = MemoryModules.Select(m => m.ConfiguredMts).DefaultIfEmpty(0).Min();
            parts.Add(string.Join(' ', new[] { bytes(TotalMemoryBytes), MemoryType, mts > 0 ? $"{mts} MT/s" : null }.Where(p => p is not null)));
        }
        if (Board.Product is { } board) parts.Add(board);
        return string.Join(" · ", parts);
    }

    /// <summary>Texto simples com todas as peças, para copiar.</summary>
    public string ToText(Func<double, string> bytes, Func<string, string> label)
    {
        var sb = new StringBuilder();
        void Line(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) sb.Append(label(key)).Append(": ").AppendLine(value); }
        foreach (var cpu in Processors) Line("components.cpu", $"{cpu.Name?.Trim()} ({cpu.Cores}C/{cpu.Threads}T)");
        foreach (var gpu in Gpus) Line("components.gpu", gpu.VramBytes is { } v ? $"{gpu.Name} {bytes(v)}" : gpu.Name);
        if (TotalMemoryBytes > 0)
            Line("components.memory", $"{bytes(TotalMemoryBytes)} {MemoryType} ({string.Join(" + ", MemoryModules.Select(m => bytes(m.CapacityBytes)))}) "
                + string.Join(", ", MemoryModules.Select(m => m.ConfiguredMts).Distinct().Select(s => $"{s} MT/s")));
        Line("components.motherboard", Join(Board.Manufacturer, Board.Product));
        Line("components.biosLabel", Join(Board.BiosVendor, Board.BiosVersion));
        foreach (var d in Drives) Line("components.storage", Join(d.Model, bytes(d.CapacityBytes), d.Bus, d.Kind switch { DriveKind.Ssd or DriveKind.NvmeSsd => "SSD", DriveKind.Hdd => "HDD", _ => null }));
        foreach (var m in Monitors) Line("components.monitors", Join(m.Manufacturer, m.Name));
        return sb.ToString().TrimEnd();
    }

    /// <summary>"AMD Ryzen 7 5800X3D 8-Core Processor" → "Ryzen 7 5800X3D"; "Intel(R) Core(TM) i7-12700K" → "Core i7-12700K".</summary>
    public static string ShortCpu(string name)
    {
        string s = name.Replace("(R)", "").Replace("(TM)", "").Replace("AMD ", "").Replace("Intel ", "").Split(" with ")[0].Trim();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+\d+-Core Processor$|\s+Processor$|\s+CPU\s+@.*$", "");
        return Collapse(s);
    }

    /// <summary>"NVIDIA GeForce RTX 5070" → "RTX 5070".</summary>
    public static string ShortGpu(string name) => Collapse(name.Replace("NVIDIA ", "").Replace("GeForce ", "").Replace("AMD ", "").Replace("Radeon(TM) ", "Radeon "));

    private static string Collapse(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? Join(params string?[] parts)
    {
        var text = string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return text.Length == 0 ? null : text;
    }

    private static BoardSpec ReadBoard()
    {
        var board = Wmi.First("SELECT Manufacturer, Product FROM Win32_BaseBoard", o => (M: Str(o["Manufacturer"]), P: Str(o["Product"])));
        var bios = Wmi.First("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS",
            o => (V: Str(o["Manufacturer"]), Ver: Str(o["SMBIOSBIOSVersion"]), D: CimDate(o["ReleaseDate"])));
        return new BoardSpec(CleanVendor(board.M), board.P, bios.V, bios.Ver, bios.D);
    }

    private static List<DriveSpec> ReadDrives()
    {
        var modern = Wmi.All("SELECT FriendlyName, Size, MediaType, BusType FROM MSFT_PhysicalDisk",
            o => new DriveSpec(Str(o["FriendlyName"]), Convert.ToUInt64(o["Size"] ?? 0UL, CultureInfo.InvariantCulture),
                DriveKindFrom(U(o["MediaType"]), U(o["BusType"])), BusName(U(o["BusType"]))), @"root\Microsoft\Windows\Storage");
        if (modern.Count > 0) return modern.OrderBy(d => d.Model, StringComparer.CurrentCultureIgnoreCase).ToList();
        return Wmi.All("SELECT Model, Size FROM Win32_DiskDrive",
            o => new DriveSpec(Str(o["Model"]), Convert.ToUInt64(o["Size"] ?? 0UL, CultureInfo.InvariantCulture), DriveKind.Unknown, null));
    }

    private static List<MonitorSpec> ReadMonitors() =>
        Wmi.All("SELECT ManufacturerName, UserFriendlyName, Active FROM WmiMonitorID",
                o => (Active: o["Active"] is true, Spec: new MonitorSpec(MonitorVendor(EdidText(o["ManufacturerName"])), EdidText(o["UserFriendlyName"]))), @"root\wmi")
            .Where(x => x.Active && (x.Spec.Name ?? x.Spec.Manufacturer) is not null).Select(x => x.Spec).ToList();

    /// <summary>Os campos de texto do EDID vêm como arrays de códigos (UInt16), terminados em zero.</summary>
    public static string? EdidText(object? value) =>
        value is ushort[] codes && new string(codes.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>Código PNP de fabricante do EDID para os nomes mais comuns; os demais ficam como vieram.</summary>
    public static string? MonitorVendor(string? pnp) => pnp?.ToUpperInvariant() switch
    {
        null => null,
        "AOC" => "AOC", "ACR" => "Acer", "AUS" => "ASUS", "BNQ" => "BenQ", "DEL" => "Dell", "GSM" => "LG", "HWP" or "HPN" => "HP",
        "LEN" => "Lenovo", "MSI" or "MST" => "MSI", "SAM" or "SEC" => "Samsung", "PHL" => "Philips", "SNY" => "Sony", "VSC" => "ViewSonic",
        "GBT" => "Gigabyte", "AOR" => "AORUS", "LGD" => "LG Display", "BOE" => "BOE", "AUO" => "AU Optronics", "CMN" => "Innolux",
        var other => other,
    };

    /// <summary>Remove sufixos jurídicos repetitivos ("ASUSTeK COMPUTER INC." → "ASUSTeK").</summary>
    public static string? CleanVendor(string? vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor)) return null;
        string v = vendor.Trim();
        foreach (var suffix in new[] { " COMPUTER INC.", " Computer Inc.", " Corporation", " Corp.", " Co., Ltd.", " Inc.", " Ltd." })
            if (v.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) v = v[..^suffix.Length];
        return v.Trim().TrimEnd(',');
    }

    private static Dictionary<string, ulong> ReadVramFromRegistry()
    {
        var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null) return result;
            foreach (string sub in cls.GetSubKeyNames())
            {
                try
                {
                    using var key = cls.OpenSubKey(sub);
                    if (key?.GetValue("DriverDesc") is not string name) continue;
                    ulong bytes = key.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => (ulong)l,
                        byte[] { Length: >= 8 } b => BitConverter.ToUInt64(b, 0),
                        byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
                        int i => (uint)i,
                        _ => key.GetValue("HardwareInformation.MemorySize") switch { int i => (uint)i, byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0), _ => 0UL },
                    };
                    if (bytes > 0) result.TryAdd(name, bytes);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return result;
    }

    private static string? Str(object? o) => o?.ToString()?.Trim() is { Length: > 0 } s ? s : null;
    private static uint U(object? o) => o is null ? 0 : Convert.ToUInt32(o, CultureInfo.InvariantCulture);

    private static DateTime? CimDate(object? o)
    {
        if (o is not string s || s.Length < 8) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s).Date; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>
    /// O helper de WMI já trata falhas de consulta; aqui ficam as de conversão (um valor fora do esperado num computador
    /// específico), para que uma seção com problema não derrube as outras.
    /// </summary>
    private static T Safe<T>(Func<T> read) where T : new()
    {
        try { return read(); }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException) { return new T(); }
    }
}
