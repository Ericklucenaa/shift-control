namespace ShiftControl.Diagnostics;

public enum ThermalVerdict { NotEnoughData, NoLimitation, PossibleLimitation, Limited }

/// <summary>Uma amostra por segundo da sessão (os mesmos dados gravados em game_session_points).</summary>
public readonly record struct ThermalSample(int Second, double? Load, double? Temp, double? Clock);

public sealed record ThermalEvidence(
    string Component,
    ThermalVerdict Verdict,
    int HighLoadSeconds,
    int ThrottledSeconds,
    double? ReferenceClockMhz,
    double? ThrottledClockMhz,
    double? PeakTemp,
    double? TempWhenThrottled)
{
    /// <summary>Queda do clock nos segundos limitados em relação à referência (clock típico sob carga alta).</summary>
    public double? ClockDropPercent => ReferenceClockMhz is > 0 && ThrottledClockMhz is { } t ? (1 - t / ReferenceClockMhz.Value) * 100 : null;
}

/// <summary>
/// Procura sinais de limitação térmica numa sessão: segundos em que a carga estava alta, a temperatura perto do pico
/// (ou acima do limite do componente) e o clock caiu bem abaixo do clock típico sob a mesma carga.
/// Não "adivinha": sem clock e temperatura medidos o resultado é <see cref="ThermalVerdict.NotEnoughData"/>.
/// </summary>
public static class ThermalAnalyzer
{
    public const double HighLoad = 80;
    public const double ClockDrop = 0.12;       // 12% abaixo da referência
    public const int MinHighLoadSeconds = 60;
    public const double CpuHotLimit = 95, GpuHotLimit = 83;

    public static ThermalEvidence Analyze(string component, IReadOnlyList<ThermalSample> samples, double hotLimit)
    {
        var high = samples.Where(s => s.Load >= HighLoad && s.Clock is > 0 && s.Temp is > 0).ToList();
        if (high.Count < MinHighLoadSeconds)
            return new(component, ThermalVerdict.NotEnoughData, high.Count, 0, null, null, samples.Max(s => s.Temp), null);

        // Referência: percentil 90 do clock sob carga alta (o clock que o componente sustenta quando não limitado).
        var clocks = high.Select(s => s.Clock!.Value).Order().ToList();
        double reference = clocks[(int)Math.Floor((clocks.Count - 1) * 0.9)];
        double peak = high.Max(s => s.Temp!.Value);
        double hotThreshold = Math.Min(hotLimit, peak - 3);

        var throttled = high.Where(s => s.Clock!.Value < reference * (1 - ClockDrop) && s.Temp!.Value >= hotThreshold).ToList();
        double share = throttled.Count / (double)high.Count;
        var verdict = throttled.Count == 0 || share < 0.03 ? ThermalVerdict.NoLimitation
            : share >= 0.10 && peak >= hotLimit - 5 ? ThermalVerdict.Limited
            : ThermalVerdict.PossibleLimitation;

        return new(component, verdict, high.Count, throttled.Count, reference,
            throttled.Count > 0 ? throttled.Average(s => s.Clock!.Value) : null, peak,
            throttled.Count > 0 ? throttled.Average(s => s.Temp!.Value) : null);
    }
}
