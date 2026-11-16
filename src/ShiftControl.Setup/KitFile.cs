using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShiftControl.Setup;

/// <summary>
/// Arquivo do kit no mesmo formato do "winget export" (schema 2.0): o kit salvo pelo Shift Control também pode
/// ser usado com "winget import", e um arquivo exportado pelo WinGet pode ser aberto no Shift Control.
/// </summary>
public static class KitFile
{
    private const string Schema = "https://aka.ms/winget-packages.schema.2.0.json";

    private static readonly Dictionary<string, JsonObject> SourceDetails = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winget"] = new JsonObject
        {
            ["Argument"] = "https://cdn.winget.microsoft.com/cache",
            ["Identifier"] = "Microsoft.Winget.Source_8wekyb3d8bbwe",
            ["Name"] = "winget",
            ["Type"] = "Microsoft.PreIndexed.Package",
        },
        ["msstore"] = new JsonObject
        {
            ["Argument"] = "https://storeedgefd.dsx.mp.microsoft.com/v9.0",
            ["Identifier"] = "StoreEdgeFD",
            ["Name"] = "msstore",
            ["Type"] = "Microsoft.Rest",
        },
    };

    public sealed record KitEntry(string Id, string Source);

    public static string Write(IEnumerable<KitEntry> packages, DateTimeOffset createdAt)
    {
        var sources = new JsonArray();
        foreach (var group in packages.GroupBy(p => p.Source, StringComparer.OrdinalIgnoreCase))
        {
            var list = new JsonArray();
            foreach (var p in group.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
                list.Add(new JsonObject { ["PackageIdentifier"] = p.Id });
            var details = SourceDetails.TryGetValue(group.Key, out var d) ? (JsonObject)d.DeepClone() : new JsonObject { ["Name"] = group.Key };
            sources.Add(new JsonObject { ["Packages"] = list, ["SourceDetails"] = details });
        }
        var root = new JsonObject
        {
            ["$schema"] = Schema,
            ["CreationDate"] = createdAt.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            ["Sources"] = sources,
            ["WinGetVersion"] = "1.9",
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Lê um kit (do Shift Control ou do "winget export"). Lança <see cref="InvalidDataException"/> se o formato não for reconhecido.</summary>
    public static IReadOnlyList<KitEntry> Read(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException("O arquivo não é um JSON válido.", ex); }
        if (root?["Sources"] is not JsonArray sources) throw new InvalidDataException("O arquivo não tem a lista \"Sources\" de um kit do WinGet.");

        var list = new List<KitEntry>();
        foreach (var source in sources)
        {
            string name = source?["SourceDetails"]?["Name"]?.GetValue<string>() ?? "winget";
            if (!SourceDetails.ContainsKey(name)) throw new InvalidDataException("Fonte do kit não suportada: " + name);
            if (source?["Packages"] is not JsonArray packages) continue;
            foreach (var p in packages)
                if (p?["PackageIdentifier"]?.GetValue<string>() is { Length: > 0 } id)
                {
                    id = id.Trim();
                    if (id.Length > 256 || id.StartsWith('-') || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\\' or '/'))
                        throw new InvalidDataException("Identificador de pacote inválido.");
                    list.Add(new KitEntry(id, name));
                }
        }
        return list.DistinctBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
