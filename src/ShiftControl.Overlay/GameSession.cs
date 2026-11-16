using ShiftControl.Core;

namespace ShiftControl.Overlay;

/// <summary>Sessão de jogo gravada. Campos nulos = a métrica não estava disponível nesta sessão.</summary>
public sealed record GameSessionRecord(
    long Id,
    string Executable,
    string Name,
    DateTimeOffset Start,
    DateTimeOffset End,
    long Frames,
    double? AverageFps,
    double? OnePercentLowFps,
    double? PointOnePercentLowFps,
    double? AverageFrametimeMs,
    double? P99FrametimeMs,
    int Stutters,
    double? CpuLoadAvg,
    double? GpuLoadAvg,
    double? CpuTempAvg,
    double? CpuTempMax,
    double? GpuTempAvg,
    double? GpuTempMax,
    double? VramMaxMb,
    string? Runtime,
    string? Boost = null)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Uma amostra por segundo da sessão (para o gráfico e a comparação).</summary>
public sealed record SessionPoint(int Second, double? Fps, double? CpuLoad, double? GpuLoad, double? CpuTemp, double? GpuTemp,
    double? CpuClock = null, double? GpuClock = null, double? GpuPower = null);

/// <summary>Acumula uma sessão em andamento. Não guarda frames individuais: as estatísticas vêm de <see cref="FrameStatistics"/>.</summary>
public sealed class SessionAggregator
{
    public const int MinimumSeconds = 30;
    public const long MinimumFrames = 300;

    private RunningStats _cpuLoad, _gpuLoad, _cpuTemp, _gpuTemp;
    private double? _vramMax;
    private readonly List<SessionPoint> _points = [];

    public SessionAggregator(string executable, string name, DateTimeOffset start)
    {
        Executable = executable;
        Name = name;
        Start = start;
    }

    public string Executable { get; }
    public string Name { get; }
    public DateTimeOffset Start { get; }
    public IReadOnlyList<SessionPoint> Points => _points;

    /// <summary>Perfil do Game Boost ativo durante a sessão (nulo = sem boost), para comparar antes/depois.</summary>
    public string? Boost { get; set; }
    private bool _boostObserved;

    public void ObserveBoost(string? profile)
    {
        if (!_boostObserved) { Boost = profile; _boostObserved = true; }
        else if (Boost != profile) Boost = "Mixed";
    }

    public void AddSecond(DateTimeOffset at, double? fps, double? cpuLoad, double? gpuLoad, double? cpuTemp, double? gpuTemp, double? vramMb,
        double? cpuClock = null, double? gpuClock = null, double? gpuPower = null)
    {
        if (cpuLoad is { } c) _cpuLoad.Add(c);
        if (gpuLoad is { } g) _gpuLoad.Add(g);
        if (cpuTemp is { } ct) _cpuTemp.Add(ct);
        if (gpuTemp is { } gt) _gpuTemp.Add(gt);
        if (vramMb is { } v) _vramMax = Math.Max(_vramMax ?? 0, v);
        int second = (int)Math.Max(0, (at - Start).TotalSeconds);
        _points.Add(new SessionPoint(second, fps, cpuLoad, gpuLoad, cpuTemp, gpuTemp, cpuClock, gpuClock, gpuPower));
    }

    /// <summary>Sessões muito curtas (menu, alt+tab acidental) não são gravadas.</summary>
    public bool IsWorthSaving(DateTimeOffset end, FrameStatsSnapshot frames) =>
        (end - Start).TotalSeconds >= MinimumSeconds && frames.Frames >= MinimumFrames;

    public GameSessionRecord Complete(DateTimeOffset end, FrameStatsSnapshot frames, string? runtime)
    {
        static double? Avg(RunningStats s) => s.HasData ? s.Mean : null;
        static double? Max(RunningStats s) => s.HasData ? s.Max : null;
        return new GameSessionRecord(0, Executable, Name, Start, end, frames.Frames,
            frames.AverageFps, frames.OnePercentLowFps, frames.PointOnePercentLowFps,
            frames.AverageFps is { } fps && fps > 0 ? 1000 / fps : null, frames.P99FrametimeMs, frames.Stutters,
            Avg(_cpuLoad), Avg(_gpuLoad), Avg(_cpuTemp), Max(_cpuTemp), Avg(_gpuTemp), Max(_gpuTemp), _vramMax, runtime, Boost);
    }
}

public sealed record MetricComparison(string Key, double? Current, double? Baseline, double? PercentChange, bool HigherIsBetter);

/// <summary>Compara duas sessões. Só calcula diferença quando as duas têm o dado medido.</summary>
public static class SessionComparison
{
    public static IReadOnlyList<MetricComparison> Compare(GameSessionRecord current, GameSessionRecord baseline)
    {
        static MetricComparison M(string key, double? a, double? b, bool higherIsBetter) =>
            new(key, a, b, a is { } x && b is { } y && Math.Abs(y) > 1e-9 ? (x - y) / y * 100 : null, higherIsBetter);

        return
        [
            M("avgFps", current.AverageFps, baseline.AverageFps, true),
            M("low1", current.OnePercentLowFps, baseline.OnePercentLowFps, true),
            M("low01", current.PointOnePercentLowFps, baseline.PointOnePercentLowFps, true),
            M("p99", current.P99FrametimeMs, baseline.P99FrametimeMs, false),
            M("cpuLoad", current.CpuLoadAvg, baseline.CpuLoadAvg, false),
            M("gpuLoad", current.GpuLoadAvg, baseline.GpuLoadAvg, true),
            M("gpuTempMax", current.GpuTempMax, baseline.GpuTempMax, false),
            M("cpuTempMax", current.CpuTempMax, baseline.CpuTempMax, false),
        ];
    }
}
