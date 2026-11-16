namespace ShiftControl.Hardware;

public static class Elevation
{
    /// <summary>O processo atual está executando como administrador.</summary>
    public static bool IsElevated => SystemInfoReader.IsElevatedCached;
}
