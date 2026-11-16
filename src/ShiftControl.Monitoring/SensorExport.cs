using System.Globalization;
using System.Text;
using System.Text.Json;
using ShiftControl.Core;

namespace ShiftControl.Monitoring;

public sealed record SensorExportRow(
    string Hardware, string Category, string Sensor, string Type, string Unit,
    double? Value, string? Unavailable, double? Min, double? Max, double? Average, long Samples);

/// <summary>Exportação dos sensores (valores atuais e estatísticas da sessão) em CSV e JSON.</summary>
public static class SensorExport
{
    public static IReadOnlyList<SensorExportRow> Rows(IEnumerable<SensorReading> sensors, SensorStatsTracker stats) =>
        sensors.Select(s =>
        {
            var st = stats.Get(s.Id);
            return new SensorExportRow(
                s.HardwareName, s.Category.ToString(), s.Name, s.Kind.ToString(), SensorValidation.Unit(s.Kind),
                s.Value.Value, s.Value.HasValue ? null : s.Value.Reason.ToString(),
                st.HasData ? st.Min : null, st.HasData ? st.Max : null, st.HasData ? st.Mean : null, st.Count);
        }).ToList();

    /// <summary>CSV com vírgula e ponto decimal (invariante), para abrir em qualquer planilha ou script.</summary>
    public static string ToCsv(IEnumerable<SensorExportRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("hardware,category,sensor,type,unit,value,unavailable,min,max,average,samples");
        foreach (var r in rows)
        {
            sb.Append(Escape(r.Hardware)).Append(',')
              .Append(Escape(r.Category)).Append(',')
              .Append(Escape(r.Sensor)).Append(',')
              .Append(Escape(r.Type)).Append(',')
              .Append(Escape(r.Unit)).Append(',')
              .Append(Num(r.Value)).Append(',')
              .Append(Escape(r.Unavailable ?? "")).Append(',')
              .Append(Num(r.Min)).Append(',')
              .Append(Num(r.Max)).Append(',')
              .Append(Num(r.Average)).Append(',')
              .Append(r.Samples.ToString(CultureInfo.InvariantCulture))
              .AppendLine();
        }
        return sb.ToString();
    }

    public static string ToJson(IEnumerable<SensorExportRow> rows, DateTimeOffset exportedAt) =>
        JsonSerializer.Serialize(new { exportedAt, sensors = rows }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    internal static string Escape(string value)
    {
        bool quote = value.IndexOfAny([',', '"', '\n', '\r']) >= 0;
        // Evita injeção de fórmulas ao abrir no Excel (valores iniciando com =, +, -, @).
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static string Num(double? v) => v is { } d ? d.ToString("0.###", CultureInfo.InvariantCulture) : "";
}
