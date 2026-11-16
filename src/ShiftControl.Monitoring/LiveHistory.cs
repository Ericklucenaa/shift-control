using ShiftControl.Core;

namespace ShiftControl.Monitoring;

/// <summary>
/// Últimas N amostras de cada série, em memória, para os gráficos ao vivo.
/// Métrica indisponível entra como NaN para manter as séries alinhadas no tempo.
/// </summary>
public sealed class LiveHistory
{
    private readonly Dictionary<string, RingBuffer<double>> _series;
    private readonly RingBuffer<DateTimeOffset> _times;
    private readonly Lock _gate = new();

    public LiveHistory(int capacity)
    {
        Capacity = capacity;
        _times = new RingBuffer<DateTimeOffset>(capacity);
        _series = MetricKeys.All.ToDictionary(m => m.Key, _ => new RingBuffer<double>(capacity), StringComparer.Ordinal);
    }

    public int Capacity { get; }

    public int Count
    {
        get { lock (_gate) return _times.Count; }
    }

    public void Push(SystemSnapshot snapshot)
    {
        lock (_gate)
        {
            _times.Add(snapshot.Time);
            foreach (var (key, reading) in MetricExtractor.Readings(snapshot))
                if (_series.TryGetValue(key, out var buffer))
                    buffer.Add(reading.Value ?? double.NaN);
        }
    }

    /// <summary>Copia as últimas amostras de uma série (mais antiga primeiro). Retorna quantas copiou.</summary>
    public int Copy(string key, Span<double> destination)
    {
        lock (_gate)
            return _series.TryGetValue(key, out var buffer) ? buffer.CopyTo(destination) : 0;
    }

    public double[] Snapshot(string key)
    {
        lock (_gate)
            return _series.TryGetValue(key, out var buffer) ? buffer.ToArray() : [];
    }

    /// <summary>Média das últimas <paramref name="samples"/> amostras válidas; nulo se não houver nenhuma.</summary>
    public double? RecentAverage(string key, int samples)
    {
        lock (_gate)
        {
            if (!_series.TryGetValue(key, out var buffer) || buffer.Count == 0) return null;
            double sum = 0;
            int n = 0;
            for (int i = Math.Max(0, buffer.Count - samples); i < buffer.Count; i++)
            {
                double v = buffer[i];
                if (double.IsNaN(v)) continue;
                sum += v;
                n++;
            }
            return n == 0 ? null : sum / n;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _times.Clear();
            foreach (var b in _series.Values) b.Clear();
        }
    }
}
