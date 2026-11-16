namespace ShiftControl.Setup;

/// <summary>Uma linha de "winget list": nome, ID, versão instalada, versão disponível (se houver) e origem.</summary>
public sealed record InstalledEntry(string Name, string Id, string Version, string? Available, string Source);

public enum WinGetOutcome
{
    Success,
    AlreadyInstalled,
    NoUpdate,
    NotFound,
    RebootRequired,
    AppRunning,
    AnotherInstallRunning,
    DiskFull,
    NoNetwork,
    Cancelled,
    BlockedByPolicy,
    NotInstalled,
    Failed,
}

/// <summary>Tradução dos códigos de saída documentados do WinGet (doc/windows/package-manager/winget/returnCodes.md).</summary>
public static class WinGetCodes
{
    public static WinGetOutcome Map(int exitCode) => unchecked((uint)exitCode) switch
    {
        0 => WinGetOutcome.Success,
        0x8A150061 or 0x8A15010D => WinGetOutcome.AlreadyInstalled,
        0x8A15002B or 0x8A15004F or 0x8A15010E => WinGetOutcome.NoUpdate,
        0x8A150014 => WinGetOutcome.NotFound,
        0x8A150109 or 0x8A15010A or 0x8A15010B => WinGetOutcome.RebootRequired,
        0x8A150101 or 0x8A150103 or 0x8A150111 => WinGetOutcome.AppRunning,
        0x8A150102 => WinGetOutcome.AnotherInstallRunning,
        0x8A150105 => WinGetOutcome.DiskFull,
        0x8A150107 => WinGetOutcome.NoNetwork,
        0x8A15010C => WinGetOutcome.Cancelled,
        0x8A15010F => WinGetOutcome.BlockedByPolicy,
        _ => WinGetOutcome.Failed,
    };

    /// <summary>Resultados que contam como "deu certo" no relatório.</summary>
    public static bool IsOk(WinGetOutcome o) => o is WinGetOutcome.Success or WinGetOutcome.AlreadyInstalled or WinGetOutcome.NoUpdate or WinGetOutcome.RebootRequired;

    public static string Hex(int exitCode) => "0x" + unchecked((uint)exitCode).ToString("X8");
}

/// <summary>
/// Lê a tabela de "winget list". O cabeçalho é traduzido conforme o idioma do Windows, mas a ordem das colunas
/// é fixa (Nome, ID, Versão, Disponível, Origem): as posições vêm da linha de cabeçalho.
/// </summary>
public static class WinGetListParser
{
    public static IReadOnlyList<InstalledEntry> Parse(IEnumerable<string> lines)
    {
        var list = new List<InstalledEntry>();
        int[]? cols = null;
        string? header = null;
        foreach (var raw in lines)
        {
            string line = raw.TrimEnd('\r');
            if (cols is null)
            {
                // O cabeçalho é a linha imediatamente antes da linha de traços.
                if (line.Length > 10 && line.Trim().All(c => c == '-'))
                {
                    if (header is not null) cols = ColumnStarts(header);
                    continue;
                }
                if (line.Trim().Length > 0) header = line;
                continue;
            }
            if (line.Trim().Length == 0 || cols.Length < 3) continue;
            // Linhas mais curtas que o início da coluna de ID são continuação ou rodapé.
            if (line.Length <= cols[1]) continue;

            string Col(int i)
            {
                int start = cols[i];
                if (start >= line.Length) return "";
                int end = i + 1 < cols.Length ? Math.Min(cols[i + 1], line.Length) : line.Length;
                return line[start..end].Trim();
            }

            string name = Col(0), id = Col(1), version = Col(2);
            string? available = cols.Length > 4 ? NullIfEmpty(Col(3)) : null;
            string source = cols.Length > 4 ? Col(4) : cols.Length > 3 ? Col(3) : "";
            if (id.Length == 0) continue;
            list.Add(new InstalledEntry(name, id, version, available, source));
        }
        return list;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    /// <summary>Início de cada palavra do cabeçalho (os nomes de coluna não têm espaços em nenhum idioma suportado).</summary>
    internal static int[] ColumnStarts(string header)
    {
        var starts = new List<int>();
        for (int i = 0; i < header.Length; i++)
            if (!char.IsWhiteSpace(header[i]) && (i == 0 || char.IsWhiteSpace(header[i - 1])))
                starts.Add(i);
        return [.. starts];
    }

    /// <summary>
    /// Casa um pacote do catálogo com o que está instalado: pelo ID do WinGet ou, para programas instalados
    /// fora do WinGet, pelo nome em "Programas instalados".
    /// </summary>
    public static InstalledEntry? Match(CatalogPackage package, IReadOnlyList<InstalledEntry> installed)
    {
        var byId = installed.FirstOrDefault(e => string.Equals(e.Id, package.Id, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        foreach (var name in package.Detect)
        {
            var exact = installed.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            // Nomes com versão ("Python 3.13.1", "Node.js 22") ou prefixos ("Microsoft Windows Desktop Runtime - 8.0.11").
            var prefix = installed.FirstOrDefault(e => e.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                && (e.Name.Length == name.Length || !char.IsLetter(e.Name[name.Length])));
            if (prefix is not null) return prefix;
        }
        return null;
    }
}
