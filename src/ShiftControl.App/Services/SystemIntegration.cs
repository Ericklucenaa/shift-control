using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Win32;

namespace ShiftControl.App.Services;

/// <summary>Integração com o Windows: inicialização automática, elevação e abertura de pastas/links.</summary>
public static class SystemIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ShiftControl";
    public const string MinimizedArgument = "--minimized";
    public const string PawnIoUrl = "https://pawnio.eu/";

    /// <summary>Inicialização com o Windows pela chave Run do usuário (não exige administrador).</summary>
    public static void SetStartWithWindows(bool enabled, bool minimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(RunValue, $"\"{AppPaths.Executable}\"" + (minimized ? " " + MinimizedArgument : ""));
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    public static bool IsStartWithWindowsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    /// <summary>Reinicia elevado. Retorna falso se o usuário recusou o UAC (o app atual continua).</summary>
    public static bool RestartElevated()
    {
        try
        {
            // A nova instância espera esta liberar o bloqueio de instância única (Application.Exit em seguida).
            Process.Start(new ProcessStartInfo(AppPaths.Executable)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // ERROR_CANCELLED: o usuário clicou em "Não" no UAC
        }
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
