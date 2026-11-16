using ShiftControl.Core;

namespace ShiftControl.App.Services;

/// <summary>Formatação de números para exibição, no idioma ativo.</summary>
public static class Fmt
{
    public static TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;

    public static string Unavailable => L.Get("common.unavailable");

    public static string Hint(Unavailable reason) => L.Get($"reason.{reason}");

    public static string Number(double value, int decimals = 0) => value.ToString("N" + decimals, L.Culture);

    public static string Percent(double value) => Number(value, value is > 0 and < 10 ? 1 : 0) + "%";

    public static string Temperature(double celsius) =>
        Number(Units.Temperature(celsius, TemperatureUnit), 0) + " " + Units.TemperatureSymbol(TemperatureUnit);

    public static string Clock(double mhz) =>
        mhz >= 1000 ? Number(mhz / 1000, 2) + " GHz" : Number(mhz, 0) + " MHz";

    public static string Mhz(double mhz) => Number(mhz, 0) + " MHz";

    public static string Watts(double w) => Number(w, w < 10 ? 1 : 0) + " W";

    public static string Rpm(double rpm) => Number(rpm, 0) + " RPM";

    public static string Bytes(double bytes, int decimals = 1)
    {
        var (v, suffix) = Units.Bytes(bytes);
        return Number(v, suffix == "B" ? 0 : decimals) + " " + suffix;
    }

    public static string Rate(double bytesPerSecond)
    {
        var (v, suffix) = Units.Bytes(bytesPerSecond);
        return Number(v, suffix == "B" ? 0 : v < 10 ? 1 : 0) + " " + suffix + "/s";
    }

    public static string Megabytes(double mb) => Bytes(mb * 1024 * 1024);

    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return L.F("time.daysHours", (int)t.TotalDays, t.Hours);
        if (t.TotalHours >= 1) return L.F("time.hoursMinutes", (int)t.TotalHours, t.Minutes);
        return L.F("time.minutes", Math.Max(0, (int)t.TotalMinutes));
    }

    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        if (local.Date == now.ToLocalTime().Date) return L.F("time.todayAt", local.ToString("t", L.Culture));
        if (local.Date == now.ToLocalTime().Date.AddDays(-1)) return L.F("time.yesterdayAt", local.ToString("t", L.Culture));
        return local.ToString("g", L.Culture);
    }

    /// <summary>Formata uma métrica do histórico conforme a unidade.</summary>
    public static string Metric(MetricUnit unit, double value) => unit switch
    {
        MetricUnit.Percent => Percent(value),
        MetricUnit.Celsius => Temperature(value),
        MetricUnit.MHz => Clock(value),
        MetricUnit.Watts => Watts(value),
        MetricUnit.BytesPerSecond => Rate(value),
        _ => Number(value, 1),
    };

    /// <summary>Formata um valor de sensor bruto conforme o tipo.</summary>
    public static string Sensor(SensorKind kind, double value) => kind switch
    {
        SensorKind.Temperature => Temperature(value),
        SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity => Percent(value),
        SensorKind.Clock => Mhz(value),
        SensorKind.Power => Watts(value),
        SensorKind.Voltage => Number(value, 3) + " V",
        SensorKind.Current => Number(value, 2) + " A",
        SensorKind.Fan => Rpm(value),
        SensorKind.Data => Bytes(value * 1024 * 1024 * 1024),
        SensorKind.SmallData => Megabytes(value),
        SensorKind.Throughput => Rate(value),
        SensorKind.Frequency => Number(value, 0) + " Hz",
        SensorKind.Factor => Number(value, 2),
        _ => Number(value, 2) + " " + SensorValidation.Unit(kind),
    };
}
