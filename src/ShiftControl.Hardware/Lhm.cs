using LibreHardwareMonitor.Hardware;
using ShiftControl.Core;

namespace ShiftControl.Hardware;

/// <summary>Tradução dos tipos do LibreHardwareMonitor para o modelo do app.</summary>
internal static class Lhm
{
    public static HardwareCategory Category(HardwareType type) => type switch
    {
        HardwareType.Cpu => HardwareCategory.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => HardwareCategory.Gpu,
        HardwareType.Memory => HardwareCategory.Memory,
        HardwareType.Storage => HardwareCategory.Storage,
        HardwareType.Network => HardwareCategory.Network,
        HardwareType.Motherboard or HardwareType.SuperIO => HardwareCategory.Motherboard,
        HardwareType.Cooler or HardwareType.EmbeddedController => HardwareCategory.Controller,
        HardwareType.Battery => HardwareCategory.Battery,
        HardwareType.Psu => HardwareCategory.Psu,
        _ => HardwareCategory.Other,
    };

    public static SensorKind Kind(SensorType type) =>
        Enum.TryParse<SensorKind>(type.ToString(), out var kind) ? kind : SensorKind.Other;

    public static GpuVendor Vendor(HardwareType type) => type switch
    {
        HardwareType.GpuNvidia => GpuVendor.Nvidia,
        HardwareType.GpuAmd => GpuVendor.Amd,
        HardwareType.GpuIntel => GpuVendor.Intel,
        _ => GpuVendor.Other,
    };

    public static bool IsGpu(HardwareType type) =>
        type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    public static ISensor? Find(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var name in names)
            foreach (var s in hw.Sensors)
                if (s.SensorType == type && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    return s;
        return null;
    }

    public static Reading Read(IHardware hw, SensorType type, Unavailable whenMissing, params string[] names)
    {
        var sensor = Find(hw, type, names);
        return sensor is null ? Reading.Missing(whenMissing) : SensorValidation.Validate(Kind(type), sensor.Value);
    }

    /// <summary>Tipos de sensor da CPU que dependem do driver de baixo nível (sem ele, a biblioteca devolve 0).</summary>
    public static bool NeedsLowLevelAccess(SensorType type) =>
        type is SensorType.Temperature or SensorType.Clock or SensorType.Power or SensorType.Voltage
            or SensorType.Current or SensorType.Factor or SensorType.Energy;

    public static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> roots)
    {
        foreach (var hw in roots)
        {
            yield return hw;
            foreach (var sub in Flatten(hw.SubHardware))
                yield return sub;
        }
    }
}
