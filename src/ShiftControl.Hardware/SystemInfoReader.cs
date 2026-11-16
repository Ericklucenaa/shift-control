using System.Management;
using System.Security.Principal;
using Microsoft.Win32;
using ShiftControl.Core;

namespace ShiftControl.Hardware;

internal static class SystemInfoReader
{
    private static readonly Lazy<bool> Elevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    public static bool IsElevated() => Elevated.Value;

    public static bool IsElevatedCached => Elevated.Value;

    /// <summary>LibreHardwareMonitor 0.9.x lê registradores da CPU pelo driver PawnIO, instalado à parte.</summary>
    public static bool IsPawnIoInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key is not null;
    }

    public static SystemInfo Read()
    {
        using var nt = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = nt?.GetValue("ProductName") as string ?? "Windows";
        string display = nt?.GetValue("DisplayVersion") as string ?? nt?.GetValue("ReleaseId") as string ?? "";
        string build = nt?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
        int ubr = nt?.GetValue("UBR") is int u ? u : 0;

        // O registro do Windows 11 ainda diz "Windows 10" em ProductName; o build é a referência correta.
        if (int.TryParse(build, out int b) && b >= 22000 && product.StartsWith("Windows 10", StringComparison.Ordinal))
            product = "Windows 11" + product["Windows 10".Length..];

        return new SystemInfo(
            Environment.MachineName,
            product,
            display,
            ubr > 0 ? $"{build}.{ubr}" : build,
            IsElevated(),
            IsPawnIoInstalled(),
            Wmi.First("SELECT Manufacturer, Product FROM Win32_BaseBoard",
                o => Join(o["Manufacturer"], o["Product"])),
            Wmi.First("SELECT Manufacturer, SMBIOSBIOSVersion FROM Win32_BIOS",
                o => Join(o["Manufacturer"], o["SMBIOSBIOSVersion"])));
    }

    private static string? Join(params object?[] parts)
    {
        var text = string.Join(' ', parts.Select(p => p?.ToString()?.Trim()).Where(p => !string.IsNullOrEmpty(p)));
        return text.Length == 0 ? null : text;
    }
}

internal static class Wmi
{
    public static T? First<T>(string query, Func<ManagementBaseObject, T> map, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();
            foreach (var o in results)
                using (o)
                    return map(o);
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return default;
    }

    public static List<T> All<T>(string query, Func<ManagementBaseObject, T> map, string scope = @"root\cimv2")
    {
        var list = new List<T>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();
            foreach (var o in results)
                using (o)
                    list.Add(map(o));
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return list;
    }
}
