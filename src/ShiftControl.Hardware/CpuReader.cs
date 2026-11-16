using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using ShiftControl.Core;

namespace ShiftControl.Hardware;

internal sealed class CpuReader : IDisposable
{
    private readonly string _name;
    private readonly int _cores;
    private readonly int _threads;
    private readonly double _baseMhz;
    private readonly SystemInfo _info;
    private PerformanceCounter? _performance;
    private bool _performanceFailed;

    public CpuReader(SystemInfo info)
    {
        _info = info;
        var cpus = Wmi.All("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor",
            o => (Name: (o["Name"] as string ?? "").Trim(),
                  Cores: Convert.ToInt32(o["NumberOfCores"] ?? 0),
                  Threads: Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0),
                  Mhz: Convert.ToDouble(o["MaxClockSpeed"] ?? 0)));
        _name = cpus.Count > 0 ? cpus[0].Name : "Processador";
        _cores = cpus.Sum(c => c.Cores);
        _threads = cpus.Count > 0 ? cpus.Sum(c => c.Threads) : Environment.ProcessorCount;
        _baseMhz = cpus.Count > 0 ? cpus[0].Mhz : 0;
    }

    public CpuState Read(IHardware? cpu)
    {
        Reading load = Reading.Missing(Unavailable.NotSupported);
        Reading temperature = Reading.Missing(_info.CpuSensorsAccessible ? Unavailable.NoSensor : _info.CpuSensorReason);
        Reading power = temperature;
        Reading clock = Reading.Missing(Unavailable.Pending);
        var clockSource = ClockSource.None;
        var threadLoads = new List<double>(_threads);

        if (cpu is not null)
        {
            load = Lhm.Read(cpu, SensorType.Load, Unavailable.NotSupported, "CPU Total");
            foreach (var s in cpu.Sensors
                         .Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core #", StringComparison.Ordinal))
                         .OrderBy(s => s.Index))
                threadLoads.Add(Math.Clamp(s.Value ?? 0, 0, 100));

            if (_info.CpuSensorsAccessible)
            {
                temperature = FirstPositive(cpu, SensorType.Temperature,
                    "Core (Tctl/Tdie)", "CPU Package", "Core (Tdie)", "Core (Tctl)", "Core Average", "CPU Cores");
                power = FirstPositive(cpu, SensorType.Power, "Package", "CPU Package", "CPU Cores");
                var sensorClock = FirstPositive(cpu, SensorType.Clock, "Cores (Average)", "Cores (Average Effective)");
                if (!sensorClock.HasValue)
                {
                    var perCore = cpu.Sensors
                        .Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #", StringComparison.Ordinal)
                                    && !s.Name.Contains("Effective", StringComparison.Ordinal) && s.Value > 0)
                        .Select(s => (double)s.Value!.Value).ToList();
                    if (perCore.Count > 0) sensorClock = Reading.Of(perCore.Average());
                }
                if (sensorClock.HasValue)
                {
                    clock = sensorClock;
                    clockSource = ClockSource.Sensor;
                }
            }
        }

        if (clockSource == ClockSource.None)
        {
            clock = EstimateClock();
            if (clock.HasValue) clockSource = ClockSource.WindowsEstimate;
        }

        return new CpuState(_name, _cores, _threads, load, temperature, clock, clockSource, power, threadLoads);
    }

    private static Reading FirstPositive(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            var r = Lhm.Read(hw, type, Unavailable.NoSensor, name);
            if (r.Value > 0) return r;
        }
        return Reading.Missing(Unavailable.NoSensor);
    }

    /// <summary>
    /// Sem driver de sensores, o Windows ainda informa o desempenho relativo do processador
    /// (% Processor Performance). Multiplicado pela frequência base, dá a frequência efetiva média.
    /// </summary>
    private Reading EstimateClock()
    {
        if (_performanceFailed || _baseMhz <= 0) return Reading.Missing(Unavailable.NotSupported);
        try
        {
            if (_performance is null)
            {
                _performance = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", readOnly: true);
                _performance.NextValue();
                return Reading.Missing(Unavailable.Pending);
            }
            double pct = _performance.NextValue();
            return pct > 0 ? Reading.Of(_baseMhz * pct / 100) : Reading.Missing(Unavailable.Pending);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _performanceFailed = true;
            _performance?.Dispose();
            _performance = null;
            return Reading.Missing(Unavailable.NotSupported);
        }
    }

    public void Dispose() => _performance?.Dispose();
}
