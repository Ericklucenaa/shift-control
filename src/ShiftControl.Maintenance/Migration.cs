using System.Text.Json;
using ShiftControl.Setup;

namespace ShiftControl.Maintenance;

public sealed record MigrationOptions(bool Programs, bool Drivers, bool Wifi, bool AppSettings);

public sealed record MigrationManifest(
    int Version,
    DateTimeOffset CreatedAt,
    string WindowsVersion,
    int Programs,
    int Drivers,
    int WifiProfiles,
    bool AppSettings);

public enum MigrationStep { Programs, Drivers, Wifi, AppSettings }

public sealed record MigrationStepResult(MigrationStep Step, bool Ok, int Count, string? Detail);

/// <summary>
/// Pacote de migração para formatar sem perder nada: lista de programas (formato do WinGet), drivers (PnPUtil),
/// redes Wi-Fi (netsh) e preferências do Shift Control, numa pasta escolhida pelo usuário (ex.: pendrive).
/// </summary>
public static class Migration
{
    public const string ManifestFile = "shift-control-migracao.json";
    public const string ProgramsFile = "programas.json";
    public const string DriversFolder = "drivers";
    public const string WifiFolder = "wifi";
    public const string SettingsFile = "preferencias.json";

    /// <summary>
    /// Cria o pacote. <paramref name="appSettingsJson"/> são as preferências serializadas pelo app.
    /// As redes Wi-Fi são exportadas com a senha em texto (é o que permite restaurá-las), por isso é opcional.
    /// </summary>
    public static async Task<IReadOnlyList<MigrationStepResult>> CreateAsync(string folder, MigrationOptions options, WinGetClient winget,
        string? appSettingsJson, Action<MigrationStep>? onStep = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var results = new List<MigrationStepResult>();
        int programs = 0, drivers = 0, wifi = 0;

        if (options.Programs)
        {
            onStep?.Invoke(MigrationStep.Programs);
            try
            {
                var installed = await winget.ListInstalledAsync(ct);
                var catalog = Catalog.Load();
                // Programas reconhecidos pelo WinGet (ID real) + do catálogo instalados fora do WinGet (Chrome, Discord…).
                var entries = installed
                    .Where(e => !e.Id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase) && !e.Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase)
                                && e.Source is "winget" or "msstore")
                    .Select(e => new KitFile.KitEntry(e.Id, e.Source))
                    .Concat(catalog.Packages.Where(p => WinGetListParser.Match(p, installed) is not null).Select(p => new KitFile.KitEntry(p.Id, p.Source)))
                    .DistinctBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                programs = entries.Count;
                await File.WriteAllTextAsync(Path.Combine(folder, ProgramsFile), KitFile.Write(entries, DateTimeOffset.Now), ct);
                results.Add(new(MigrationStep.Programs, true, programs, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                results.Add(new(MigrationStep.Programs, false, 0, ex.Message));
            }
        }

        if (options.Drivers)
        {
            onStep?.Invoke(MigrationStep.Drivers);
            var r = await Drivers.BackupAsync(Path.Combine(folder, DriversFolder), null, ct);
            drivers = r.Ok && int.TryParse(r.Output.Split('\n')[0], out int n) ? n : 0;
            results.Add(new(MigrationStep.Drivers, r.Ok, drivers, r.Ok ? null : r.Summary));
        }

        if (options.Wifi)
        {
            onStep?.Invoke(MigrationStep.Wifi);
            string dir = Path.Combine(folder, WifiFolder);
            Directory.CreateDirectory(dir);
            var r = await Shell.RunAsync(Shell.SystemTool("netsh.exe"), $"wlan export profile key=clear folder=\"{dir}\"", ct: ct);
            wifi = Directory.GetFiles(dir, "*.xml").Length;
            // Sem adaptador Wi-Fi o netsh retorna erro: não é falha do pacote.
            results.Add(new(MigrationStep.Wifi, wifi > 0 || r.ExitCode == 0, wifi, wifi == 0 ? "noProfiles" : null));
        }

        if (options.AppSettings && appSettingsJson is not null)
        {
            onStep?.Invoke(MigrationStep.AppSettings);
            await File.WriteAllTextAsync(Path.Combine(folder, SettingsFile), appSettingsJson, ct);
            results.Add(new(MigrationStep.AppSettings, true, 1, null));
        }

        var manifest = new MigrationManifest(1, DateTimeOffset.Now, Environment.OSVersion.VersionString, programs, drivers, wifi,
            options.AppSettings && appSettingsJson is not null);
        await File.WriteAllTextAsync(Path.Combine(folder, ManifestFile),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
        return results;
    }

    /// <summary>Lê o manifesto de uma pasta de migração; nulo se a pasta não for um pacote do Shift Control.</summary>
    public static MigrationManifest? Read(string folder)
    {
        string path = Path.Combine(folder, ManifestFile);
        if (!File.Exists(path)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<MigrationManifest>(File.ReadAllText(path));
            return manifest is { Version: 1, Programs: >= 0, Drivers: >= 0, WifiProfiles: >= 0 } ? manifest : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Restaura as redes Wi-Fi do pacote (não exige administrador para o usuário atual).</summary>
    public static async Task<MigrationStepResult> RestoreWifiAsync(string folder, CancellationToken ct = default)
    {
        string dir = Path.Combine(folder, WifiFolder);
        if (!Directory.Exists(dir)) return new(MigrationStep.Wifi, true, 0, "none");
        int ok = 0;
        var files = Directory.GetFiles(dir, "*.xml");
        foreach (var xml in files)
        {
            var r = await Shell.RunAsync(Shell.SystemTool("netsh.exe"), $"wlan add profile filename=\"{xml}\" user=current", ct: ct);
            if (r.ExitCode == 0) ok++;
        }
        return new(MigrationStep.Wifi, ok == files.Length, ok, ok == files.Length ? null : "partial");
    }

    public static Task<DriverOperationResult> RestoreDriversAsync(string folder, Action<string>? onLine = null, CancellationToken ct = default) =>
        Drivers.RestoreAsync(Path.Combine(folder, DriversFolder), onLine, ct);

    public static string? ProgramsKit(string folder) =>
        File.Exists(Path.Combine(folder, ProgramsFile)) ? File.ReadAllText(Path.Combine(folder, ProgramsFile)) : null;

    public static string? AppSettings(string folder) =>
        File.Exists(Path.Combine(folder, SettingsFile)) ? File.ReadAllText(Path.Combine(folder, SettingsFile)) : null;
}
