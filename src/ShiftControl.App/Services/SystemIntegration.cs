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
    private static string StartupTaskName => "ShiftControl-" + Environment.UserName;

    /// <summary>Uma tarefa por usuário permite iniciar elevado no logon.</summary>
    public static void SetStartWithWindows(bool enabled, bool minimized)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder("\\");
        if (enabled)
        {
            dynamic task = scheduler.NewTask(0);
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            task.RegistrationInfo.Description = "Shift Control";
            task.Principal.UserId = sid;
            task.Principal.LogonType = 3; // InteractiveToken: no stored password.
            task.Principal.RunLevel = 1; // HighestAvailable.
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.ExecutionTimeLimit = "PT0S";
            dynamic trigger = task.Triggers.Create(9); // Logon.
            trigger.UserId = sid;
            dynamic action = task.Actions.Create(0);
            action.Path = AppPaths.Executable;
            action.Arguments = minimized ? MinimizedArgument : "";
            action.WorkingDirectory = AppContext.BaseDirectory;
            folder.RegisterTaskDefinition(StartupTaskName, task, 6, sid, null, 3);
        }
        else
        {
            try { folder.DeleteTask(StartupTaskName, 0); }
            catch (Exception ex) when (IsMissingTask(ex)) { }
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    public static bool IsStartWithWindowsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(RunValue) is string) return true;
        return IsStartupTaskEnabled(StartupTaskName);
    }

    internal static bool IsStartupTaskEnabled(string taskName)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        scheduler.Connect();
        try { return scheduler.GetFolder("\\").GetTask(taskName).Enabled; }
        catch (Exception ex) when (IsMissingTask(ex)) { return false; }
    }

    // .NET maps missing COM tasks/folders to IO exceptions instead of COMException.
    internal static bool IsMissingTask(Exception ex) =>
        ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003);

    public static void MigrateLegacyStartup(bool minimized)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(RunValue) is string) SetStartWithWindows(true, minimized);
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
