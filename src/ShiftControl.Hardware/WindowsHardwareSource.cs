using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShiftControl.Core;

namespace ShiftControl.Hardware;

/// <summary>
/// Fonte de hardware real do Windows. Combina LibreHardwareMonitor (CPU, GPU, placa-mãe, SMART),
/// APIs nativas (memória, temperatura de discos), WMI (inventário) e contadores de desempenho.
/// Cada componente é lido isoladamente: a falha de um não derruba os demais.
/// </summary>
public sealed class WindowsHardwareSource : IHardwareSource
{
    // Discos via LHM fazem consultas SMART: atualizar a cada poucas rodadas basta.
    private const int StorageLhmEveryTicks = 5;

    private readonly ILogger _log;
    private readonly HashSet<string> _reportedFailures = [];
    private Computer? _computer;
    private SystemInfo? _info;
    private CpuReader? _cpu;
    private MemoryReader? _memory;
    private StorageReader? _storage;
    private NetworkReader? _network;
    private long _ticks;

    public WindowsHardwareSource(ILogger<WindowsHardwareSource>? log = null)
    {
        _log = log ?? NullLogger<WindowsHardwareSource>.Instance;
    }

    public SystemInfo Initialize()
    {
        var sw = Stopwatch.StartNew();
        _info = SystemInfoReader.Read();
        _cpu = new CpuReader(_info);
        _memory = new MemoryReader();
        _storage = new StorageReader();
        _network = new NetworkReader();

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            // Sem admin, a biblioteca não lê SMART nem o chip Super I/O; habilitar só gastaria tempo.
            IsStorageEnabled = _info.IsElevated,
            IsMotherboardEnabled = _info.IsElevated,
            IsControllerEnabled = _info.IsElevated,
            IsNetworkEnabled = false,
            IsPsuEnabled = false,
            IsBatteryEnabled = true,
        };
        try
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            // Sem LHM o app continua: memória, discos e rede vêm de APIs do Windows.
            _log.LogError(ex, "LibreHardwareMonitor não abriu; sensores de CPU/GPU ficarão indisponíveis.");
            _computer = null;
        }

        _log.LogInformation(
            "Hardware inicializado em {Ms} ms. Admin={Admin}, PawnIO={Driver}, componentes LHM={Count}.",
            sw.ElapsedMilliseconds, _info.IsElevated, _info.SensorDriverInstalled, _computer?.Hardware.Count ?? 0);
        return _info;
    }

    public SystemSnapshot Sample(long sequence)
    {
        if (_info is null) throw new InvalidOperationException("Initialize() não foi chamado.");
        var sw = Stopwatch.StartNew();
        _ticks++;

        var roots = _computer?.Hardware ?? (IList<IHardware>)[];
        IHardware? cpuHw = null;
        var gpus = new List<IHardware>();
        var storage = new List<IHardware>();
        foreach (var hw in roots)
        {
            bool isStorage = hw.HardwareType == HardwareType.Storage;
            if (isStorage && _ticks % StorageLhmEveryTicks != 1) { storage.Add(hw); continue; }
            Guard($"update:{hw.HardwareType}", () => Update(hw));
            if (hw.HardwareType == HardwareType.Cpu) cpuHw ??= hw;
            else if (Lhm.IsGpu(hw.HardwareType)) gpus.Add(hw);
            else if (isStorage) storage.Add(hw);
        }

        var cpu = Guard("cpu", () => _cpu!.Read(cpuHw)) ?? CpuState.Empty();
        var gpuStates = gpus.Select(g => Guard($"gpu:{g.Identifier}", () => GpuReader.Read(g))).OfType<GpuState>().ToList();
        var memory = Guard("memory", () => _memory!.Read()) ?? MemoryState.Empty;
        var drives = Guard("storage", () => _storage!.Read(storage)) ?? [];
        var network = Guard("network", () => _network!.Read()) ?? NetworkState.Empty;
        var sensors = Guard("sensors", () => FlattenSensors(roots, drives, network)) ?? [];

        return new SystemSnapshot(sequence, DateTimeOffset.Now, cpu, gpuStates, memory, drives, network, sensors, sw.Elapsed);
    }

    private static void Update(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) sub.Update();
    }

    private List<SensorReading> FlattenSensors(IList<IHardware> roots, IReadOnlyList<DriveState> drives, NetworkState network)
    {
        var list = new List<SensorReading>(256);
        foreach (var hw in Lhm.Flatten(roots))
        {
            var category = Lhm.Category(hw.HardwareType);
            string hwId = hw.Identifier.ToString();
            foreach (var s in hw.Sensors)
            {
                var kind = Lhm.Kind(s.SensorType);
                Reading value;
                if (category == HardwareCategory.Cpu && Lhm.NeedsLowLevelAccess(s.SensorType) && !_info!.CpuSensorsAccessible)
                    value = Reading.Missing(_info.CpuSensorReason); // a biblioteca devolveria 0, não um valor real
                else
                    value = SensorValidation.Validate(kind, s.Value);
                list.Add(new SensorReading(s.Identifier.ToString(), s.Name, hwId, hw.Name, category, kind, value));
            }
        }

        // Discos e rede lidos fora do LHM (sem admin) também entram na lista de sensores.
        bool lhmHasStorage = list.Any(s => s.Category == HardwareCategory.Storage);
        if (!lhmHasStorage)
        {
            foreach (var d in drives)
            {
                string hwId = $"/disk/{d.Index}";
                list.Add(new SensorReading($"{hwId}/temperature", "Temperature", hwId, d.Model, HardwareCategory.Storage, SensorKind.Temperature, d.Temperature));
                list.Add(new SensorReading($"{hwId}/activity", "Activity", hwId, d.Model, HardwareCategory.Storage, SensorKind.Load, d.ActivePercent));
                list.Add(new SensorReading($"{hwId}/read", "Read Rate", hwId, d.Model, HardwareCategory.Storage, SensorKind.Throughput, d.ReadBps));
                list.Add(new SensorReading($"{hwId}/write", "Write Rate", hwId, d.Model, HardwareCategory.Storage, SensorKind.Throughput, d.WriteBps));
            }
        }
        foreach (var a in network.Adapters)
        {
            string hwId = $"/net/{a.Id}";
            var pending = Reading.Missing(Unavailable.Pending);
            list.Add(new SensorReading($"{hwId}/down", "Download Speed", hwId, a.Name, HardwareCategory.Network, SensorKind.Throughput, network.Ready ? Reading.Of(a.DownBps) : pending));
            list.Add(new SensorReading($"{hwId}/up", "Upload Speed", hwId, a.Name, HardwareCategory.Network, SensorKind.Throughput, network.Ready ? Reading.Of(a.UpBps) : pending));
        }
        return list;
    }

    private T? Guard<T>(string area, Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            // Cada área é registrada uma vez por sessão; a coleta segue com as demais.
            if (_reportedFailures.Add(area)) _log.LogError(ex, "Falha ao ler {Area}.", area);
            return default;
        }
    }

    private void Guard(string area, Action action) => Guard<bool>(area, () => { action(); return true; });

    public void Dispose()
    {
        var computer = Interlocked.Exchange(ref _computer, null);
        try { computer?.Close(); }
        catch (Exception ex) { _log.LogWarning(ex, "Falha ao fechar o LibreHardwareMonitor."); }
        Interlocked.Exchange(ref _cpu, null)?.Dispose();
        Interlocked.Exchange(ref _storage, null)?.Dispose();
    }
}
