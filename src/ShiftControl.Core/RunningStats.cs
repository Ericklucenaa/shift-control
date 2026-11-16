namespace ShiftControl.Core;

/// <summary>Mínimo, máximo e média incrementais (média pelo método de Welford, estável numericamente).</summary>
public struct RunningStats
{
    public long Count { get; private set; }
    public double Min { get; private set; }
    public double Max { get; private set; }
    public double Mean { get; private set; }

    public void Add(double value)
    {
        if (!double.IsFinite(value)) return;
        Count++;
        if (Count == 1)
        {
            Min = Max = Mean = value;
            return;
        }
        if (value < Min) Min = value;
        if (value > Max) Max = value;
        Mean += (value - Mean) / Count;
    }

    public readonly bool HasData => Count > 0;
}

/// <summary>Estatísticas por sensor desde a abertura do app (ou do último reset). Thread-safe.</summary>
public sealed class SensorStatsTracker
{
    private readonly Dictionary<string, RunningStats> _stats = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Update(IEnumerable<SensorReading> sensors)
    {
        lock (_gate)
        {
            foreach (var s in sensors)
            {
                if (s.Value.Value is not { } v) continue;
                ref var st = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_stats, s.Id, out _);
                st.Add(v);
            }
        }
    }

    public RunningStats Get(string sensorId)
    {
        lock (_gate) return _stats.TryGetValue(sensorId, out var st) ? st : default;
    }

    public void Reset()
    {
        lock (_gate) _stats.Clear();
    }

    public int Count
    {
        get { lock (_gate) return _stats.Count; }
    }
}
