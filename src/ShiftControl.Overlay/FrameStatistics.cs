using ShiftControl.Core;

namespace ShiftControl.Overlay;

/// <summary>Um frame apresentado pelo jogo. <see cref="FrameTimeMs"/> = intervalo entre duas chamadas Present().</summary>
public readonly record struct FrameSample(double FrameTimeMs, double? DisplayedIntervalMs = null);

/// <summary>Estatísticas de fluidez calculadas a partir de frames reais. Valores nulos = amostras insuficientes.</summary>
public sealed record FrameStatsSnapshot(
    long Frames,
    TimeSpan CapturedTime,
    double? InstantFps,
    double? AverageFps,
    double? MinimumFps,
    double? MaximumFps,
    double? OnePercentLowFps,
    double? PointOnePercentLowFps,
    double? FrametimeMs,
    double? LastFrametimeMs,
    double? P99FrametimeMs,
    double? DisplayedFps,
    int Stutters,
    TimeSpan? SinceLastStutter)
{
    public static FrameStatsSnapshot Empty { get; } = new(0, TimeSpan.Zero, null, null, null, null, null, null, null, null, null, null, 0, null);
}

/// <summary>
/// Metodologia (documentada em docs/OVERLAY.md):
/// <list type="bullet">
/// <item><b>Frametime</b>: intervalo entre Present() consecutivos da cadeia de troca principal do jogo (PresentMon: MsBetweenPresents).</item>
/// <item><b>FPS instantâneo</b>: frames do último segundo ÷ soma dos seus frametimes (não é a média de FPS por frame).</item>
/// <item><b>FPS médio</b>: total de frames ÷ tempo total capturado = 1000 ÷ frametime médio.</item>
/// <item><b>Mín./máx.</b>: menor e maior FPS entre janelas completas de 1 s (evita que um único frame isolado defina o mínimo).</item>
/// <item><b>1% / 0,1% Low</b>: 1000 ÷ média dos 1% (0,1%) frametimes mais longos da sessão. Exige ≥ 100 (≥ 1000) frames.</item>
/// <item><b>Travamento (stutter)</b>: frame com frametime &gt; 2,5× a mediana dos 64 anteriores e ao menos 10 ms acima dela.</item>
/// </list>
/// Usa histograma de 0,05 ms para os percentis: memória e custo constantes por frame, sem alocação durante o jogo.
/// </summary>
public sealed class FrameStatistics
{
    public const double BinMs = 0.05;
    public const int Bins = 20_000; // até 1000 ms; acima disso vai para o balde de excesso
    public const int OnePercentMinFrames = 100;
    public const int PointOnePercentMinFrames = 1000;
    public const double MaxFrameTimeMs = 5000; // pausas maiores (alt+tab, carregamento) não são frames
    private const int MedianWindow = 64;
    private const int MedianMinFrames = 16;
    public const double StutterFactor = 2.5;
    public const double StutterMinExtraMs = 10.0;

    private readonly int[] _histogram = new int[Bins + 1];
    private double _overflowSumMs;
    private readonly RingBuffer<double> _recent;
    private readonly double[] _medianRing = new double[MedianWindow];
    private readonly double[] _medianScratch = new double[MedianWindow];
    private int _medianCount, _medianIndex;
    private readonly Lock _gate = new();

    private long _frames;
    private double _sumMs;
    private double _windowMs;
    private int _windowFrames;
    private double _minSecondFps = double.PositiveInfinity, _maxSecondFps;
    private bool _hasSecond;
    private long _displayed;
    private double _displayedSumMs;
    private int _stutters;
    private double _msSinceStutter = double.NaN;

    public FrameStatistics(int recentCapacity = 1024) => _recent = new RingBuffer<double>(recentCapacity);

    public void Add(FrameSample frame)
    {
        double ft = frame.FrameTimeMs;
        if (!(ft > 0) || ft > MaxFrameTimeMs || !double.IsFinite(ft)) return;

        lock (_gate)
        {
            _frames++;
            _sumMs += ft;
            int bin = (int)(ft / BinMs);
            if (bin >= Bins)
            {
                _histogram[Bins]++;
                _overflowSumMs += ft;
            }
            else _histogram[bin]++;
            _recent.Add(ft);

            if (frame.DisplayedIntervalMs is { } d && d > 0 && double.IsFinite(d))
            {
                _displayed++;
                _displayedSumMs += d;
            }

            _windowFrames++;
            _windowMs += ft;
            if (_windowMs >= 1000)
            {
                double fps = _windowFrames * 1000 / _windowMs;
                _minSecondFps = Math.Min(_minSecondFps, fps);
                _maxSecondFps = Math.Max(_maxSecondFps, fps);
                _hasSecond = true;
                _windowFrames = 0;
                _windowMs = 0;
            }

            if (!double.IsNaN(_msSinceStutter)) _msSinceStutter += ft;
            if (_medianCount >= MedianMinFrames)
            {
                double median = Median();
                if (ft > StutterFactor * median && ft - median >= StutterMinExtraMs)
                {
                    _stutters++;
                    _msSinceStutter = 0;
                }
            }
            _medianRing[_medianIndex] = ft;
            _medianIndex = (_medianIndex + 1) % MedianWindow;
            if (_medianCount < MedianWindow) _medianCount++;
        }
    }

    private double Median()
    {
        Array.Copy(_medianRing, _medianScratch, _medianCount);
        var span = _medianScratch.AsSpan(0, _medianCount);
        span.Sort();
        return _medianCount % 2 == 1 ? span[_medianCount / 2] : (span[_medianCount / 2 - 1] + span[_medianCount / 2]) / 2;
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_histogram);
            _overflowSumMs = 0;
            _recent.Clear();
            _medianCount = _medianIndex = 0;
            _frames = 0;
            _sumMs = _windowMs = 0;
            _windowFrames = 0;
            _minSecondFps = double.PositiveInfinity;
            _maxSecondFps = 0;
            _hasSecond = false;
            _displayed = 0;
            _displayedSumMs = 0;
            _stutters = 0;
            _msSinceStutter = double.NaN;
        }
    }

    public long Frames
    {
        get { lock (_gate) return _frames; }
    }

    /// <summary>Copia os frametimes mais recentes (mais antigo primeiro) para o gráfico. Sem alocação.</summary>
    public int CopyRecent(Span<double> destination)
    {
        lock (_gate) return _recent.CopyTo(destination);
    }

    public FrameStatsSnapshot Snapshot()
    {
        lock (_gate)
        {
            if (_frames == 0) return FrameStatsSnapshot.Empty;

            // Último segundo de frames (somando frametimes do mais novo para trás).
            double sum = 0;
            int n = 0;
            for (int i = _recent.Count - 1; i >= 0 && sum < 1000; i--)
            {
                sum += _recent[i];
                n++;
            }
            double? instant = sum > 0 ? n * 1000 / sum : null;

            return new FrameStatsSnapshot(
                _frames,
                TimeSpan.FromMilliseconds(_sumMs),
                instant,
                _frames * 1000 / _sumMs,
                _hasSecond ? _minSecondFps : null,
                _hasSecond ? _maxSecondFps : null,
                _frames >= OnePercentMinFrames ? LowFps(0.01) : null,
                _frames >= PointOnePercentMinFrames ? LowFps(0.001) : null,
                sum > 0 ? sum / n : null,
                _recent.Count > 0 ? _recent.Last : null,
                _frames >= OnePercentMinFrames ? Percentile(0.99) : null,
                _displayed > 0 && _displayedSumMs > 0 ? _displayed * 1000 / _displayedSumMs : null,
                _stutters,
                double.IsNaN(_msSinceStutter) ? null : TimeSpan.FromMilliseconds(_msSinceStutter));
        }
    }

    /// <summary>1000 ÷ média dos frametimes mais longos que compõem a fração <paramref name="fraction"/> da sessão.</summary>
    private double LowFps(double fraction)
    {
        long k = Math.Max(1, (long)Math.Ceiling(_frames * fraction));
        long taken = 0;
        double sumMs = 0;

        long overflow = _histogram[Bins];
        if (overflow > 0)
        {
            long use = Math.Min(overflow, k);
            sumMs += _overflowSumMs / overflow * use;
            taken += use;
        }
        for (int bin = Bins - 1; bin >= 0 && taken < k; bin--)
        {
            int count = _histogram[bin];
            if (count == 0) continue;
            long use = Math.Min(count, k - taken);
            sumMs += (bin + 0.5) * BinMs * use;
            taken += use;
        }
        return taken == 0 || sumMs <= 0 ? 0 : 1000 / (sumMs / taken);
    }

    /// <summary>Frametime no percentil <paramref name="q"/> (ex.: 0,99), pelo centro do balde do histograma.</summary>
    private double Percentile(double q)
    {
        long target = (long)Math.Ceiling(_frames * q);
        long seen = 0;
        for (int bin = 0; bin < Bins; bin++)
        {
            seen += _histogram[bin];
            if (seen >= target) return (bin + 0.5) * BinMs;
        }
        return _histogram[Bins] > 0 ? _overflowSumMs / _histogram[Bins] : Bins * BinMs;
    }
}
