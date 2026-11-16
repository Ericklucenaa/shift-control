namespace ShiftControl.Core;

/// <summary>Descarta leituras impossíveis em vez de exibi-las. Os limites são físicos, não de alerta.</summary>
public static class SensorValidation
{
    public static Reading Validate(SensorKind kind, double? raw)
    {
        if (raw is not { } v) return Reading.Missing(Unavailable.NoSensor);
        if (!double.IsFinite(v)) return Reading.Missing(Unavailable.Invalid);

        bool plausible = kind switch
        {
            SensorKind.Temperature => v is > -40 and < 150,
            SensorKind.Load or SensorKind.Control or SensorKind.Level => v is >= -0.5 and <= 100.5,
            SensorKind.Clock => v is >= 0 and < 100_000,
            SensorKind.Fan => v is >= 0 and < 25_000,
            SensorKind.Power => v is >= 0 and < 5_000,
            SensorKind.Voltage => v is >= 0 and < 400,
            SensorKind.Current => v is >= 0 and < 1_000,
            SensorKind.Data or SensorKind.SmallData or SensorKind.Throughput => v >= 0,
            _ => true,
        };
        if (!plausible) return Reading.Missing(Unavailable.Invalid);

        return kind is SensorKind.Load or SensorKind.Control or SensorKind.Level
            ? Reading.Of(Math.Clamp(v, 0, 100))
            : Reading.Of(v);
    }

    public static string Unit(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.Load or SensorKind.Control or SensorKind.Level => "%",
        SensorKind.Clock => "MHz",
        SensorKind.Power => "W",
        SensorKind.Voltage => "V",
        SensorKind.Current => "A",
        SensorKind.Fan => "RPM",
        SensorKind.Data => "GB",
        SensorKind.SmallData => "MB",
        SensorKind.Throughput => "B/s",
        SensorKind.Frequency => "Hz",
        SensorKind.Energy => "mWh",
        SensorKind.Noise => "dBA",
        SensorKind.TimeSpan => "s",
        SensorKind.Humidity => "%",
        SensorKind.Flow => "L/h",
        _ => "",
    };
}
