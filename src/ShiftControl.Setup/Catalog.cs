using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShiftControl.Setup;

public sealed record CatalogPackage
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    /// <summary>"winget" (padrão) ou "msstore".</summary>
    public string Source { get; init; } = "winget";
    /// <summary>Nomes como aparecem em "Programas instalados", para reconhecer instalações feitas fora do WinGet.</summary>
    public IReadOnlyList<string> Detect { get; init; } = [];
    public IReadOnlyList<string> Profiles { get; init; } = [];
    public string? Description { get; init; }
    public string? Note { get; init; }
    /// <summary>Hardware necessário para o pacote fazer sentido (ex.: "nvidia").</summary>
    public string? Requires { get; init; }
    /// <summary>O programa se atualiza sozinho (o WinGet pode não conseguir atualizá-lo).</summary>
    public bool SelfUpdating { get; init; }
    /// <summary>Não aparece em "Programas instalados" (ex.: DirectX); não dá para saber se já está instalado.</summary>
    public bool NoDetect { get; init; }
    /// <summary>
    /// Chaves do registro (HKLM\… ou HKCU\…) cujo valor <c>pv</c> é a versão instalada, para componentes que o
    /// WinGet não lista (ex.: WebView2, instalado pelo atualizador do Edge).
    /// </summary>
    public IReadOnlyList<string> DetectRegistry { get; init; } = [];
}

public static class RegistryDetection
{
    /// <summary>Versão encontrada em alguma das chaves do pacote, ou nulo.</summary>
    public static InstalledEntry? Find(CatalogPackage package)
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var path in package.DetectRegistry)
        {
            try
            {
                if (Microsoft.Win32.Registry.GetValue(path, "pv", null) is string v && v.Length > 0 && v != "0.0.0.0")
                    return new InstalledEntry(package.Name, package.Id, v, null, "registry");
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or ArgumentException) { }
        }
        return null;
    }
}

public sealed record CatalogProfile(string Key, string Name);

public sealed record Catalog(IReadOnlyList<string> Categories, IReadOnlyList<CatalogProfile> Profiles, IReadOnlyList<CatalogPackage> Packages)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public static Catalog Load()
    {
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream(
            typeof(Catalog).Assembly.GetManifestResourceNames().Single(n => n.EndsWith("catalog.json", StringComparison.Ordinal)))!;
        return JsonSerializer.Deserialize<Catalog>(stream, Options) ?? throw new InvalidDataException("Catálogo vazio.");
    }

    public IEnumerable<CatalogPackage> ForProfile(string key) => Packages.Where(p => p.Profiles.Contains(key));

    public CatalogPackage? Find(string id) => Packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
