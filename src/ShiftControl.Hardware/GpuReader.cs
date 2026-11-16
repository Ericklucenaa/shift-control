using LibreHardwareMonitor.Hardware;
using ShiftControl.Core;

namespace ShiftControl.Hardware;

internal static class GpuReader
{
    public static GpuState Read(IHardware gpu)
    {
        const Unavailable none = Unavailable.NoSensor;

        var load = Lhm.Read(gpu, SensorType.Load, none, "GPU Core", "D3D 3D");

        var vramUsed = Lhm.Read(gpu, SensorType.SmallData, none, "GPU Memory Used", "D3D Dedicated Memory Used");
        var vramTotal = Lhm.Read(gpu, SensorType.SmallData, none, "GPU Memory Total", "D3D Dedicated Memory Total");

        var power = Lhm.Read(gpu, SensorType.Power, none, "GPU Package", "GPU Power", "GPU Core");
        if (!power.HasValue)
        {
            var any = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Value > 0);
            if (any is not null) power = SensorValidation.Validate(SensorKind.Power, any.Value);
        }

        var fan = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
        var control = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control);

        return new GpuState(
            gpu.Identifier.ToString(),
            gpu.Name,
            Lhm.Vendor(gpu.HardwareType),
            load,
            Lhm.Read(gpu, SensorType.Temperature, none, "GPU Core"),
            Lhm.Read(gpu, SensorType.Temperature, none, "GPU Hot Spot", "GPU Hotspot"),
            Lhm.Read(gpu, SensorType.Temperature, none, "GPU Memory Junction", "GPU Memory"),
            vramUsed,
            vramTotal,
            Lhm.Read(gpu, SensorType.Clock, none, "GPU Core"),
            Lhm.Read(gpu, SensorType.Clock, none, "GPU Memory"),
            power,
            fan is null ? Reading.Missing(none) : SensorValidation.Validate(SensorKind.Fan, fan.Value),
            control is null ? Reading.Missing(none) : SensorValidation.Validate(SensorKind.Control, control.Value));
    }
}
