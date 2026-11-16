using ShiftControl.Core;

namespace ShiftControl.Diagnostics;

public enum FindingSeverity { Info, Attention, Critical }

/// <summary>
/// Um achado do diagnóstico. <see cref="Code"/> identifica a regra (a interface traduz título e texto);
/// <see cref="Evidence"/> guarda os dados medidos que levaram à conclusão.
/// </summary>
public sealed record Finding(string Code, FindingSeverity Severity, string Subject, IReadOnlyDictionary<string, double> Evidence);

public sealed record HealthReport(DateTimeOffset At, HealthLevel Level, IReadOnlyList<Finding> Findings, int ChecksRun);

/// <summary>Médias recentes usadas pelas regras que dependem de comportamento sustentado.</summary>
public sealed record RecentAverages(double? CpuLoad, double? MemoryLoad, int Samples);

/// <summary>
/// Verificação rápida e não destrutiva do estado atual. Só conclui a partir de dados medidos;
/// limites de temperatura vêm do próprio dispositivo, nunca de um valor universal.
/// </summary>
public static class HealthAnalyzer
{
    public const double VolumeAttentionFreePercent = 10;
    public const double VolumeCriticalFreePercent = 5;
    public const double SustainedLoadPercent = 90;
    public const double LifeAttentionPercent = 10;

    /// <summary>Amostras mínimas (≈ 30 s a 1 Hz) para afirmar que uma carga é "sustentada".</summary>
    public const int MinimumSamplesForSustained = 30;

    public static HealthReport Analyze(SystemSnapshot snapshot, SystemInfo info, RecentAverages recent, DateTimeOffset now)
    {
        var findings = new List<Finding>();
        int checks = 0;

        foreach (var drive in snapshot.Drives)
        {
            foreach (var volume in drive.Volumes)
            {
                checks++;
                if (volume.TotalBytes == 0) continue;
                double free = volume.FreePercent;
                if (free < VolumeAttentionFreePercent)
                {
                    findings.Add(new Finding("volume.low",
                        free < VolumeCriticalFreePercent ? FindingSeverity.Critical : FindingSeverity.Attention,
                        volume.Root,
                        new Dictionary<string, double> { ["freePercent"] = free, ["freeBytes"] = volume.FreeBytes, ["totalBytes"] = volume.TotalBytes }));
                }
            }

            checks++;
            if (drive.Health is DriveHealth.Warning or DriveHealth.Unhealthy)
            {
                findings.Add(new Finding("drive.health",
                    drive.Health == DriveHealth.Unhealthy ? FindingSeverity.Critical : FindingSeverity.Attention,
                    drive.Model,
                    new Dictionary<string, double> { ["status"] = (int)drive.Health }));
            }

            checks++;
            if (drive.Temperature.Value is { } temp)
            {
                if (drive.CriticalTemperature.Value is { } crit && temp >= crit)
                    findings.Add(new Finding("drive.temp", FindingSeverity.Critical, drive.Model,
                        new Dictionary<string, double> { ["temperature"] = temp, ["threshold"] = crit }));
                else if (drive.WarningTemperature.Value is { } warn && temp >= warn)
                    findings.Add(new Finding("drive.temp", FindingSeverity.Attention, drive.Model,
                        new Dictionary<string, double> { ["temperature"] = temp, ["threshold"] = warn }));
            }

            checks++;
            if (drive.LifeRemaining.Value is { } life && life <= LifeAttentionPercent)
                findings.Add(new Finding("drive.life", FindingSeverity.Attention, drive.Model,
                    new Dictionary<string, double> { ["lifeRemaining"] = life }));
        }

        if (recent.Samples >= MinimumSamplesForSustained)
        {
            checks++;
            if (recent.MemoryLoad is { } mem && mem >= SustainedLoadPercent)
                findings.Add(new Finding("memory.high", FindingSeverity.Attention, "RAM",
                    new Dictionary<string, double> { ["average"] = mem, ["samples"] = recent.Samples }));

            checks++;
            if (recent.CpuLoad is { } cpu && cpu >= SustainedLoadPercent)
                findings.Add(new Finding("cpu.high", FindingSeverity.Attention, snapshot.Cpu.Name,
                    new Dictionary<string, double> { ["average"] = cpu, ["samples"] = recent.Samples }));
        }

        checks++;
        if (!snapshot.Cpu.Temperature.HasValue)
            findings.Add(new Finding("cpu.sensors", FindingSeverity.Info, snapshot.Cpu.Name,
                new Dictionary<string, double> { ["reason"] = (int)snapshot.Cpu.Temperature.Reason }));

        var level = findings.Any(f => f.Severity == FindingSeverity.Critical) ? HealthLevel.Critical
            : findings.Any(f => f.Severity == FindingSeverity.Attention) ? HealthLevel.Attention
            : HealthLevel.Good;

        var ordered = findings.OrderByDescending(f => f.Severity).ToList();
        return new HealthReport(now, level, ordered, checks);
    }
}
