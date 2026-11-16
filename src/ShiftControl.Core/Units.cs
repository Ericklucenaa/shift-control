namespace ShiftControl.Core;

public static class Units
{
    public static double ToFahrenheit(double celsius) => celsius * 9 / 5 + 32;

    public static double Temperature(double celsius, TemperatureUnit unit) =>
        unit == TemperatureUnit.Fahrenheit ? ToFahrenheit(celsius) : celsius;

    public static string TemperatureSymbol(TemperatureUnit unit) => unit == TemperatureUnit.Fahrenheit ? "°F" : "°C";

    /// <summary>Escolhe a escala binária (KB, MB, GB…) para um número de bytes.</summary>
    public static (double Value, string Suffix) Bytes(double bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
        int i = 0;
        double v = Math.Abs(bytes);
        while (v >= 1024 && i < suffixes.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return (Math.Sign(bytes) * v, suffixes[i]);
    }
}
