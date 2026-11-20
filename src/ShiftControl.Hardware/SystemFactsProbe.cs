using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ShiftControl.Diagnostics;

namespace ShiftControl.Hardware;

/// <summary>
/// Coleta, só por leitura, os fatos usados pela Checagem gamer e pelo checklist pós-formatação.
/// Nada aqui altera o sistema. Leituras que falham voltam nulas (a avaliação mostra "não foi possível verificar").
/// </summary>
public static partial class SystemFactsProbe
{
    /// <summary>Leituras rápidas (menos de 1 s).</summary>
    public static SystemFacts ReadFast()
    {
        var (planName, planGuid) = Safe(ReadPowerPlan, ((string?)null, (Guid?)null));
        return new(
            Safe(ReadDisplays, []),
            Safe(ReadMemory, []),
            Safe(ReadGpus, []),
            Safe(ReadGameSettings, new GameSettingsFact(null, null, null, null)),
            planName,
            planGuid,
            Safe<bool?>(() => ReadRebootPending(), null),
            Safe(ReadProblemDevices, []));
    }

    /// <summary>Leituras lentas (licença do Windows e busca de atualizações: de segundos a mais de um minuto).</summary>
    public static (bool? Activated, int? PendingUpdates) ReadSlow() => (Safe<bool?>(ReadActivation, null), Safe<int?>(CountPendingUpdates, null));

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return fallback; }
    }

    #region Monitores

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice dd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);

    private const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4, EnumCurrentSettings = -1;

    private static List<DisplayFact> ReadDisplays()
    {
        var list = new List<DisplayFact>();
        var dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DisplayDevice>())
        {
            if ((dd.StateFlags & AttachedToDesktop) == 0) continue;
            var current = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(dd.DeviceName, EnumCurrentSettings, ref current)) continue;

            int max = current.dmDisplayFrequency;
            var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            for (int m = 0; EnumDisplaySettings(dd.DeviceName, m, ref mode); m++)
                if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight && mode.dmDisplayFrequency > max)
                    max = mode.dmDisplayFrequency;

            list.Add(new DisplayFact(dd.DeviceName, dd.DeviceString.Trim(), current.dmPelsWidth, current.dmPelsHeight,
                current.dmDisplayFrequency, max, (dd.StateFlags & PrimaryDevice) != 0));
        }
        return list;
    }

    #endregion

    internal static List<MemoryModuleFact> ReadMemory() =>
        Wmi.All("SELECT Capacity, Speed, ConfiguredClockSpeed, PartNumber, Manufacturer FROM Win32_PhysicalMemory", o => new MemoryModuleFact(
            Convert.ToUInt64(o["Capacity"] ?? 0UL),
            Convert.ToInt32(o["Speed"] ?? 0),
            Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0),
            (o["PartNumber"] as string)?.Trim(),
            (o["Manufacturer"] as string)?.Trim()));

    internal static List<string> ReadGpus() =>
        Wmi.All("SELECT Name FROM Win32_VideoController", o => (o["Name"] as string ?? "").Trim()).Where(n => n.Length > 0).Distinct().ToList();

    private static GameSettingsFact ReadGameSettings()
    {
        // Modo de Jogo: ligado por padrão desde o Windows 10 1703 quando o valor não existe.
        bool? gameMode = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AutoGameModeEnabled", null) is int g ? g != 0 : true;
        // Agendamento de GPU acelerado por hardware: estado real informado pelo driver; registro (2 = ligado) como alternativa.
        bool? hags = Safe(HagsFromDriver, null)
            ?? (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", null) is int h ? h == 2 : null);
        // Otimizações para jogos em janela e VRR: string "SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;".
        var dx = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", null) as string;
        return new GameSettingsFact(gameMode, hags, Flag(dx, "SwapEffectUpgradeEnable"), Flag(dx, "VRROptimizeEnable"));
    }

    #region HAGS pelo driver (D3DKMT, WDDM 2.7+)

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo2 { public uint Handle; public long Luid; public uint NumOfSources; public uint Present; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters2 { public uint NumAdapters; public nint Adapters; }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo { public uint Adapter; public int Type; public nint PrivateDriverData; public uint PrivateDriverDataSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter { public uint Adapter; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 args);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo args);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter args);

    private const int KmtQaiTypeWddm27Caps = 70;

    /// <summary>Verdadeiro se algum adaptador com HAGS suportado o tem ligado; nulo se nenhum informar.</summary>
    private static bool? HagsFromDriver()
    {
        var e = new EnumAdapters2();
        if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return null;
        int size = Marshal.SizeOf<AdapterInfo2>();
        e.Adapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
        nint caps = Marshal.AllocHGlobal(4);
        bool? result = null;
        try
        {
            if (D3DKMTEnumAdapters2(ref e) != 0) return null;
            for (int i = 0; i < e.NumAdapters; i++)
            {
                var a = Marshal.PtrToStructure<AdapterInfo2>(e.Adapters + i * size);
                Marshal.WriteInt32(caps, 0);
                var q = new QueryAdapterInfo { Adapter = a.Handle, Type = KmtQaiTypeWddm27Caps, PrivateDriverData = caps, PrivateDriverDataSize = 4 };
                if (D3DKMTQueryAdapterInfo(ref q) == 0)
                {
                    int bits = Marshal.ReadInt32(caps);
                    bool supported = (bits & 1) != 0, enabled = (bits & 2) != 0;
                    if (supported) result = (result ?? false) || enabled;
                }
                var c = new CloseAdapter { Adapter = a.Handle };
                D3DKMTCloseAdapter(ref c);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(e.Adapters);
            Marshal.FreeHGlobal(caps);
        }
        return result;
    }

    #endregion

    internal static bool? Flag(string? settings, string key)
    {
        if (settings is null) return null;
        var m = Regex.Match(settings, Regex.Escape(key) + @"=(\d)");
        return m.Success ? m.Groups[1].Value != "0" : null;
    }

    private static (string? Name, Guid? Guid) ReadPowerPlan()
    {
        var psi = new ProcessStartInfo("powercfg", "/getactivescheme") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return ParsePowerPlan(output);
    }

    /// <summary>"GUID do Esquema de Energia: 381b4222-… (Equilibrado)" em qualquer idioma.</summary>
    internal static (string? Name, Guid? Guid) ParsePowerPlan(string output)
    {
        var m = Regex.Match(output, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)");
        return m.Success ? (m.Groups[2].Value.Trim(), Guid.Parse(m.Groups[1].Value)) : (null, null);
    }

    private static bool ReadRebootPending()
    {
        using var lm = Registry.LocalMachine;
        return lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is not null
            || lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is not null;
    }

    private static List<ProblemDevice> ReadProblemDevices() =>
        Wmi.All("SELECT Name, ConfigManagerErrorCode, PNPClass FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0", o => new ProblemDevice(
            (o["Name"] as string ?? "Dispositivo desconhecido").Trim(),
            Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0),
            o["PNPClass"] as string));

    /// <summary>Licença do Windows (ApplicationID do Windows) com chave instalada; 1 = licenciado.</summary>
    private static bool? ReadActivation()
    {
        var status = Wmi.All(
            "SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID = '55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL",
            o => Convert.ToInt32(o["LicenseStatus"] ?? 0));
        return status.Count == 0 ? null : status.Any(s => s == 1);
    }

    /// <summary>Atualizações de software disponíveis e não ocultas, pela API do Windows Update (só consulta, não instala).</summary>
    private static int? CountPendingUpdates()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
        if (type is null) return null;
        dynamic session = Activator.CreateInstance(type)!;
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.Online = true;
        dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
        return (int)result.Updates.Count;
    }
}
