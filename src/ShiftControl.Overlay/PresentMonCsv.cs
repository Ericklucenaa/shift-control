using System.Globalization;

namespace ShiftControl.Overlay;

/// <summary>Um Present() observado pelo PresentMon.</summary>
public readonly record struct PresentRecord(
    string Application,
    int ProcessId,
    ulong SwapChain,
    string Runtime,
    string PresentMode,
    double FrameTimeMs,
    double? DisplayedIntervalMs);

/// <summary>
/// Lê a saída CSV do PresentMon (2.x). As colunas são localizadas pelo cabeçalho, então mudanças de ordem
/// entre versões não quebram a leitura. Frametime: MsBetweenPresents; na ausência, FrameTime ou
/// MsCPUBusy + MsCPUWait. Exibição: MsBetweenDisplayChange ("NA" = frame não exibido).
/// </summary>
public sealed class PresentMonCsv
{
    private int _app = -1, _pid = -1, _swap = -1, _runtime = -1, _mode = -1;
    private int _between = -1, _frameTime = -1, _cpuBusy = -1, _cpuWait = -1, _display = -1;
    private int _columns;
    private readonly Dictionary<string, string> _intern = new(StringComparer.Ordinal);

    public bool HasHeader { get; private set; }

    public static bool LooksLikeHeader(ReadOnlySpan<char> line) =>
        line.StartsWith("Application,", StringComparison.Ordinal);

    public bool TryReadHeader(string line)
    {
        if (!LooksLikeHeader(line)) return false;
        var cols = line.Split(',');
        int Find(string name) => Array.FindIndex(cols, c => string.Equals(c.Trim(), name, StringComparison.OrdinalIgnoreCase));
        _app = Find("Application");
        _pid = Find("ProcessID");
        _swap = Find("SwapChainAddress");
        _runtime = Find("PresentRuntime");
        _mode = Find("PresentMode");
        _between = Find("MsBetweenPresents");
        _frameTime = Find("FrameTime");
        _cpuBusy = Find("MsCPUBusy");
        _cpuWait = Find("MsCPUWait");
        _display = Find("MsBetweenDisplayChange");
        _columns = cols.Length;
        HasHeader = _app >= 0 && _pid >= 0 && (_between >= 0 || _frameTime >= 0 || (_cpuBusy >= 0 && _cpuWait >= 0));
        return HasHeader;
    }

    /// <summary>Lê uma linha de dados. Retorna falso para linhas inválidas ou sem frametime.</summary>
    public bool TryParse(string line, out PresentRecord record)
    {
        record = default;
        if (!HasHeader || line.Length == 0) return false;

        Span<Range> ranges = stackalloc Range[Math.Max(_columns + 1, 8)];
        ReadOnlySpan<char> span = line;
        int count = span.Split(ranges, ',');
        if (count < _columns) return false;

        if (!int.TryParse(Column(span, ranges, count, _pid), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) return false;

        double? frameTime = Number(Column(span, ranges, count, _between));
        if (frameTime is null && _frameTime >= 0) frameTime = Number(Column(span, ranges, count, _frameTime));
        if (frameTime is null && Number(Column(span, ranges, count, _cpuBusy)) is { } busy && Number(Column(span, ranges, count, _cpuWait)) is { } wait) frameTime = busy + wait;
        if (frameTime is not > 0) return false;

        ulong swap = 0;
        var swapText = Column(span, ranges, count, _swap);
        if (swapText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) swapText = swapText[2..];
        if (swapText.Length > 0) ulong.TryParse(swapText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out swap);

        record = new PresentRecord(
            Intern(Column(span, ranges, count, _app)),
            pid,
            swap,
            Intern(Column(span, ranges, count, _runtime)),
            Intern(Column(span, ranges, count, _mode)),
            frameTime.Value,
            Number(Column(span, ranges, count, _display)));
        return true;
    }

    private static ReadOnlySpan<char> Column(ReadOnlySpan<char> line, ReadOnlySpan<Range> ranges, int count, int index) =>
        index >= 0 && index < count ? line[ranges[index]].Trim() : default;

    private static double? Number(ReadOnlySpan<char> text) =>
        text.Length > 0 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;

    /// <summary>Nomes se repetem a cada frame: reaproveitar a mesma string evita alocar milhares por segundo.</summary>
    private string Intern(ReadOnlySpan<char> text)
    {
        if (text.Length == 0) return "";
        var lookup = _intern.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(text, out var existing)) return existing;
        if (_intern.Count > 4096) _intern.Clear();
        var s = text.ToString();
        _intern[s] = s;
        return s;
    }
}
