using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ShiftControl.Gaming;

/// <summary>
/// Consultas leves sobre processos: janela em primeiro plano, se um PID ainda existe e um nome amigável.
/// Só usa acesso "limitado de consulta" (o mesmo do Gerenciador de Tarefas): nunca abre o processo do jogo
/// com permissão de leitura de memória, o que poderia ser interpretado como intrusão por sistemas anticheat.
/// </summary>
public static partial class GameProcesses
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;

    public static int? ForegroundProcessId()
    {
        nint hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == 0 ? null : (int)pid;
    }

    public static bool IsAlive(int pid)
    {
        using var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsInvalid) return Marshal.GetLastPInvokeError() == 5; // acesso negado = existe, mas protegido
        return GetExitCodeProcess(handle, out uint code) && code == STILL_ACTIVE;
    }

    public static string? ImagePath(int pid)
    {
        using var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle.IsInvalid) return null;
        Span<char> buffer = stackalloc char[1024];
        int size = buffer.Length;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer[..size].ToString() : null;
    }

    /// <summary>
    /// Nome para exibição: nome do produto gravado no executável (lido do arquivo em disco), senão o título
    /// da janela principal, senão o nome do arquivo.
    /// </summary>
    public static string DisplayName(int pid, string executable)
    {
        try
        {
            if (ImagePath(pid) is { } path)
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var name = (info.ProductName ?? info.FileDescription)?.Trim();
                if (!string.IsNullOrWhiteSpace(name) && name.Length <= 60 && !name.Contains("Unreal", StringComparison.OrdinalIgnoreCase))
                    return name;
            }
            using var p = Process.GetProcessById(pid);
            if (!string.IsNullOrWhiteSpace(p.MainWindowTitle) && p.MainWindowTitle.Length <= 60) return p.MainWindowTitle.Trim();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException or System.ComponentModel.Win32Exception) { }
        return Path.GetFileNameWithoutExtension(executable);
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint code);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, Span<char> name, ref int size);
}
