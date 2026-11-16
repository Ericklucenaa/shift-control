namespace ShiftControl.App.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } =
        Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--data-dir=", StringComparison.Ordinal)) is { } argument
            ? Path.GetFullPath(argument["--data-dir=".Length..])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShiftControl");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string Database => Path.Combine(DataDirectory, "shiftcontrol.db");

    public static string Executable => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ShiftControl.exe");

    public static string Version =>
        typeof(AppPaths).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
