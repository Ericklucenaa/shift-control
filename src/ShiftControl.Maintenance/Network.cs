using System.Net.NetworkInformation;

namespace ShiftControl.Maintenance;

public sealed record PingSummary(string Host, int Sent, int Received, double? AvgMs, double? MinMs, double? MaxMs, double? JitterMs, IReadOnlyList<double?> Samples)
{
    public double LossPercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;
}

/// <summary>Teste de conexão: latência (ping), variação (jitter) e perda de pacotes contra um servidor.</summary>
public static class NetworkTest
{
    public static readonly string[] SuggestedHosts = ["1.1.1.1", "8.8.8.8", "9.9.9.9"];

    public static async Task<PingSummary> RunAsync(string host, int count = 30, int intervalMs = 250, IProgress<(int Done, double? Ms)>? progress = null,
        CancellationToken ct = default)
    {
        using var ping = new Ping();
        var samples = new List<double?>();
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            double? ms = null;
            try
            {
                var reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(2), null, null, ct);
                if (reply.Status == IPStatus.Success) ms = reply.RoundtripTime;
            }
            catch (PingException) { }
            samples.Add(ms);
            progress?.Report((i + 1, ms));
            if (i < count - 1) await Task.Delay(intervalMs, ct);
        }
        return Summarize(host, samples);
    }

    /// <summary>Jitter = média da diferença absoluta entre respostas consecutivas (como o RFC 3550 simplificado).</summary>
    internal static PingSummary Summarize(string host, IReadOnlyList<double?> samples)
    {
        var ok = samples.Where(s => s.HasValue).Select(s => s!.Value).ToList();
        double? jitter = ok.Count >= 2 ? ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average() : null;
        return new PingSummary(host, samples.Count, ok.Count,
            ok.Count > 0 ? ok.Average() : null, ok.Count > 0 ? ok.Min() : null, ok.Count > 0 ? ok.Max() : null, jitter, samples);
    }

    public enum Quality { Excellent, Good, Fair, Poor, Offline }

    /// <summary>Classificação para jogos online: perda pesa mais que latência.</summary>
    public static Quality Rate(PingSummary s) =>
        s.Received == 0 ? Quality.Offline
        : s.LossPercent >= 5 || s.AvgMs > 120 || s.JitterMs > 30 ? Quality.Poor
        : s.LossPercent >= 1 || s.AvgMs > 70 || s.JitterMs > 15 ? Quality.Fair
        : s.AvgMs > 35 || s.JitterMs > 6 ? Quality.Good
        : Quality.Excellent;
}
