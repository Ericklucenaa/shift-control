using ShiftControl.Core;

namespace ShiftControl.Overlay;

public sealed record LatencySnapshot(string Host, double? LastMs, double? AverageMs, double? JitterMs, double? LossPercent, int Probes)
{
    public static LatencySnapshot Empty(string host) => new(host, null, null, null, null, 0);
}

/// <summary>
/// Resultado de pings ICMP periódicos (teste controlado: 1 pacote pequeno por segundo, só enquanto a métrica estiver visível).
/// Jitter = média das diferenças absolutas entre RTTs consecutivos bem-sucedidos (estilo RFC 3550).
/// Perda = respostas ausentes ÷ pings enviados na janela.
/// </summary>
public sealed class LatencyStats
{
    private readonly RingBuffer<double> _samples;
    private readonly Lock _gate = new();

    public LatencyStats(int window = 60) => _samples = new RingBuffer<double>(window);

    /// <summary>RTT em ms, ou nulo quando não houve resposta.</summary>
    public void Add(double? rttMs)
    {
        lock (_gate) _samples.Add(rttMs is { } v && v >= 0 && double.IsFinite(v) ? v : double.NaN);
    }

    public void Clear()
    {
        lock (_gate) _samples.Clear();
    }

    public LatencySnapshot Snapshot(string host)
    {
        lock (_gate)
        {
            int n = _samples.Count;
            if (n == 0) return LatencySnapshot.Empty(host);
            int ok = 0, lost = 0, diffs = 0;
            double sum = 0, jitter = 0, prev = double.NaN, last = double.NaN;
            for (int i = 0; i < n; i++)
            {
                double v = _samples[i];
                if (double.IsNaN(v)) { lost++; continue; }
                ok++;
                sum += v;
                if (!double.IsNaN(prev)) { jitter += Math.Abs(v - prev); diffs++; }
                prev = v;
                last = v;
            }
            double newest = _samples[n - 1];
            return new LatencySnapshot(
                host,
                double.IsNaN(newest) ? null : newest,
                ok > 0 ? sum / ok : null,
                diffs > 0 ? jitter / diffs : null,
                100.0 * lost / n,
                n);
        }
    }
}
