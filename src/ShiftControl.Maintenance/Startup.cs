using System.Diagnostics;
using Microsoft.Win32;

namespace ShiftControl.Maintenance;

public enum StartupSource { RunUser, RunMachine, RunMachine32, StartupFolderUser, StartupFolderCommon }

public sealed record StartupItem(
    string Name,
    string Command,
    string? ExecutablePath,
    string? Publisher,
    StartupSource Source,
    bool Enabled,
    bool RequiresAdmin)
{
    /// <summary>Identificador estável (origem + nome) para registrar e desfazer alterações.</summary>
    public string Key => $"{Source}|{Name}";
}

/// <summary>
/// Programas que abrem com o Windows, lidos das mesmas origens do Gerenciador de Tarefas. Habilitar/desabilitar usa as
/// chaves "StartupApproved" (o mesmo mecanismo do Gerenciador de Tarefas): nada é apagado e tudo pode ser revertido.
/// </summary>
public static class Startup
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static IReadOnlyList<StartupItem> List()
    {
        var list = new List<StartupItem>();
        ReadRun(list, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", StartupSource.RunUser);
        ReadRun(list, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", StartupSource.RunMachine);
        ReadRun(list, Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", StartupSource.RunMachine32);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.StartupFolderUser);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.StartupFolderCommon);
        return list.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static (RegistryKey Hive, string Path) ApprovedLocation(StartupSource s) => s switch
    {
        StartupSource.RunUser => (Registry.CurrentUser, Approved + "Run"),
        StartupSource.RunMachine => (Registry.LocalMachine, Approved + "Run"),
        StartupSource.RunMachine32 => (Registry.LocalMachine, Approved + "Run32"),
        StartupSource.StartupFolderUser => (Registry.CurrentUser, Approved + "StartupFolder"),
        _ => (Registry.LocalMachine, Approved + "StartupFolder"),
    };

    /// <summary>Primeiro byte do valor em StartupApproved: par = habilitado, ímpar = desabilitado. Sem valor = habilitado.</summary>
    internal static bool IsEnabled(byte[]? approved) => approved is not { Length: > 0 } || approved[0] % 2 == 0;

    private static bool ReadEnabled(StartupSource source, string name)
    {
        var (hive, path) = ApprovedLocation(source);
        using var key = hive.OpenSubKey(path);
        return IsEnabled(key?.GetValue(name) as byte[]);
    }

    private static void ReadRun(List<StartupItem> list, RegistryKey hive, string path, StartupSource source)
    {
        using var key = hive.OpenSubKey(path);
        if (key is null) return;
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrWhiteSpace(name) || key.GetValue(name) is not string command) continue;
            string? exe = ExecutableFrom(command);
            list.Add(new StartupItem(name, command, exe, Publisher(exe), source, ReadEnabled(source, name), hive == Registry.LocalMachine));
        }
    }

    private static void ReadFolder(List<StartupItem> list, string folder, StartupSource source)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            string name = Path.GetFileName(file);
            list.Add(new StartupItem(Path.GetFileNameWithoutExtension(file), file, file, null, source, ReadEnabled(source, name),
                source == StartupSource.StartupFolderCommon));
        }
    }

    /// <summary>Extrai o executável de uma linha de comando ("C:\x\a.exe" --flag ou C:\x\a.exe /b).</summary>
    internal static string? ExecutableFrom(string command)
    {
        string c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
    }

    private static string? Publisher(string? exe)
    {
        if (exe is null || !File.Exists(exe)) return null;
        try { return FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim() is { Length: > 0 } c ? c : null; }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>Habilita ou desabilita como o Gerenciador de Tarefas faz. Itens do computador inteiro exigem administrador.</summary>
    public static bool SetEnabled(StartupItem item, bool enabled)
    {
        if (item.RequiresAdmin && !Shell.IsElevated) return false;
        var (hive, path) = ApprovedLocation(item.Source);
        string valueName = item.Source is StartupSource.StartupFolderUser or StartupSource.StartupFolderCommon ? Path.GetFileName(item.Command) : item.Name;
        using var key = hive.CreateSubKey(path, writable: true);
        var data = new byte[12];
        data[0] = (byte)(enabled ? 2 : 3);
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTimeUtc()).CopyTo(data, 4); // horário em que foi desabilitado
        key.SetValue(valueName, data, RegistryValueKind.Binary);
        return true;
    }

    public static StartupItem? Find(string key) => List().FirstOrDefault(i => i.Key == key);
}
