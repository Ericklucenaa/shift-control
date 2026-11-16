using System.Diagnostics;

namespace ShiftControl.Maintenance;

public static class UpdateInstallation
{
    public static bool CanApply(bool enabled, bool gameActive, bool operationActive, bool dialogOpen) =>
        enabled && !gameActive && !operationActive && !dialogOpen;

    // NSIS requires /D to be the final argument, without quotes, including paths with spaces.
    public static ProcessStartInfo StartInfo(string installer, string directory, bool installed, bool visible = false)
    {
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (directory.Contains('"') || directory.Contains('\r') || directory.Contains('\n'))
            throw new ArgumentException("Invalid installation directory.", nameof(directory));
        return new ProcessStartInfo(Path.GetFullPath(installer))
        {
            UseShellExecute = true,
            Arguments = $"{(visible ? "" : "/S ")}/UPDATE /WAITPID={Environment.ProcessId} {(installed ? "" : "/PORTABLE ")}/D={directory}"
        };
    }
}
