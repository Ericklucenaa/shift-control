using ShiftControl.App.Services;
using ShiftControl.Core;
using ShiftControl.Overlay;

namespace ShiftControl.App.Overlay;

/// <summary>Valores prontos para desenhar, indexados por <see cref="OverlayMetric"/>. Reaproveitado a cada atualização.</summary>
public sealed class OverlayFrame
{
    public static readonly int Count = Enum.GetValues<OverlayMetric>().Length;

    public string[] Text { get; } = new string[Count];
    public bool[] Available { get; } = new bool[Count];
    /// <summary>Preenchimento de barra (0–100) ou NaN quando a métrica não tem barra.</summary>
    public double[] Bar { get; } = new double[Count];
    public double[] FrametimeGraph { get; } = new double[240];
    public int FrametimeCount { get; set; }
    public IReadOnlyList<double> ThreadLoads { get; set; } = [];
}

/// <summary>Monta o <see cref="OverlayFrame"/> a partir das fontes reais. Sem dado = "n/d", nunca zero.</summary>
public static class OverlayComposer
{
    public static string NotAvailable => L.Get("overlay.na");

    public static void Compose(
        OverlayFrame f,
        SystemSnapshot? s,
        FrameStatsSnapshot frames,
        FrameStatistics? stats,
        bool captureRunning,
        LatencySnapshot? latency,
        string? gameName,
        TimeSpan? sessionTime)
    {
        Array.Fill(f.Bar, double.NaN);

        void Set(OverlayMetric m, string? text, double bar = double.NaN)
        {
            int i = (int)m;
            f.Available[i] = text is not null;
            f.Text[i] = text ?? NotAvailable;
            f.Bar[i] = text is null ? double.NaN : bar;
        }

        void SetReading(OverlayMetric m, Reading r, Func<double, string> fmt, Func<double, double>? bar = null) =>
            Set(m, r.Value is { } v ? fmt(v) : null, r.Value is { } b && bar is not null ? bar(b) : double.NaN);

        // Sessão
        Set(OverlayMetric.GameName, gameName);
        Set(OverlayMetric.SessionTime, sessionTime is { } t ? Clock(t) : null);

        // Frames: só existem com captura ativa e um jogo alvo.
        string? Fps(double? v) => captureRunning && v is { } x ? Fmt.Number(x, 0) : null;
        Set(OverlayMetric.Fps, Fps(frames.InstantFps));
        Set(OverlayMetric.FpsAverage, Fps(frames.AverageFps));
        Set(OverlayMetric.FpsMinimum, Fps(frames.MinimumFps));
        Set(OverlayMetric.FpsMaximum, Fps(frames.MaximumFps));
        Set(OverlayMetric.OnePercentLow, Fps(frames.OnePercentLowFps));
        Set(OverlayMetric.PointOnePercentLow, Fps(frames.PointOnePercentLowFps));
        Set(OverlayMetric.Frametime, captureRunning && frames.FrametimeMs is { } ft ? Fmt.Number(ft, 1) + " ms" : null);
        Set(OverlayMetric.Stutter, captureRunning && frames.Frames > 0 ? Fmt.Number(frames.Stutters) : null);
        if (captureRunning && stats is not null)
        {
            f.FrametimeCount = stats.CopyRecent(f.FrametimeGraph);
            Set(OverlayMetric.FrametimeGraph, f.FrametimeCount > 1 ? "" : null);
        }
        else
        {
            f.FrametimeCount = 0;
            Set(OverlayMetric.FrametimeGraph, null);
        }

        // Hardware (mesmo motor de coleta do Dashboard).
        if (s is null)
        {
            foreach (var m in OverlayCatalog.All.Where(m => OverlayCatalog.SourceOf(m) == OverlaySource.Sensors)) Set(m, null);
        }
        else
        {
            var gpu = s.PrimaryGpu;
            var none = Reading.Missing(Unavailable.NotSupported);
            SetReading(OverlayMetric.CpuLoad, s.Cpu.Load, Fmt.Percent, v => v);
            f.ThreadLoads = s.Cpu.ThreadLoads;
            Set(OverlayMetric.CpuCores, s.Cpu.ThreadLoads.Count > 0 ? "" : null);
            SetReading(OverlayMetric.CpuTemperature, s.Cpu.Temperature, Fmt.Temperature);
            SetReading(OverlayMetric.CpuClock, s.Cpu.Clock, Fmt.Clock);
            SetReading(OverlayMetric.CpuPower, s.Cpu.Power, Fmt.Watts);
            SetReading(OverlayMetric.GpuLoad, gpu?.Load ?? none, Fmt.Percent, v => v);
            SetReading(OverlayMetric.GpuTemperature, gpu?.Temperature ?? none, Fmt.Temperature);
            SetReading(OverlayMetric.GpuHotspot, gpu?.Hotspot ?? none, Fmt.Temperature);
            SetReading(OverlayMetric.GpuClock, gpu?.CoreClock ?? none, Fmt.Mhz);
            SetReading(OverlayMetric.GpuMemoryClock, gpu?.MemoryClock ?? none, Fmt.Mhz);
            SetReading(OverlayMetric.GpuPower, gpu?.Power ?? none, Fmt.Watts);
            SetReading(OverlayMetric.GpuFan, gpu?.FanRpm ?? none, Fmt.Rpm);
            if (gpu?.VramUsedMb.Value is { } used && gpu.VramTotalMb.Value is { } total && total > 0)
                Set(OverlayMetric.Vram, $"{Fmt.Number(used / 1024, 1)}/{Fmt.Number(total / 1024, 0)} GB", used / total * 100);
            else
                SetReading(OverlayMetric.Vram, gpu?.VramUsedMb ?? none, Fmt.Megabytes);
            Set(OverlayMetric.Ram, s.Memory.TotalBytes > 0
                    ? $"{Fmt.Number(s.Memory.UsedBytes / 1073741824.0, 1)}/{Fmt.Number(s.Memory.TotalBytes / 1073741824.0, 0)} GB"
                    : null,
                s.Memory.LoadPercent);
            Set(OverlayMetric.DiskActivity, s.Drives.Any(d => d.ReadBps.HasValue)
                ? $"{Fmt.Rate(s.DiskReadBps)} · {Fmt.Rate(s.DiskWriteBps)}"
                : null);
        }

        // Rede
        Set(OverlayMetric.Ping, latency?.LastMs is { } ping ? Fmt.Number(ping, 0) + " ms" : null);
        Set(OverlayMetric.Jitter, latency?.JitterMs is { } jitter ? Fmt.Number(jitter, 1) + " ms" : null);
        Set(OverlayMetric.PacketLoss, latency?.LossPercent is { } loss && latency.Probes >= 5 ? Fmt.Number(loss, 0) + "%" : null);
    }

    public static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    public static string DefaultLabel(OverlayMetric metric) => L.Get($"overlay.metric.{metric}");
}
