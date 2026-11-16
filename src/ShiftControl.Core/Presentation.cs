namespace ShiftControl.Core;

/// <summary>Cálculos puros usados pela interface (testáveis sem WinUI).</summary>
public static class Presentation
{
    /// <summary>
    /// Quantas colunas cabem em <paramref name="width"/>. Evita deixar um item sozinho na última linha
    /// (4 itens em 3 colunas vira 2 + 2).
    /// </summary>
    public static int Columns(double width, double minColumn, double spacing, int maxColumns, int count = int.MaxValue)
    {
        if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0) return 1;
        int columns = (int)Math.Floor((width + spacing) / (minColumn + spacing));
        columns = Math.Clamp(columns, 1, Math.Max(1, maxColumns));
        if (count < int.MaxValue)
        {
            columns = Math.Min(columns, Math.Max(1, count));
            while (columns > 2 && count > columns && count % columns == 1) columns--;
        }
        return columns;
    }

    /// <summary>Teto "redondo" para eixos (1, 2, 2,5, 5, 10 × 10ⁿ).</summary>
    public static double NiceCeiling(double v)
    {
        if (v <= 0 || !double.IsFinite(v)) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / exp;
        double nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        return nice * exp;
    }

    /// <summary>Teto "redondo" em unidades binárias (KB, MB…), para eixos de bytes.</summary>
    public static double NiceBinaryCeiling(double v)
    {
        if (v <= 0 || !double.IsFinite(v)) return 1024;
        double scale = 1;
        while (v / scale >= 1024) scale *= 1024;
        return NiceCeiling(v / scale) * scale;
    }
}
