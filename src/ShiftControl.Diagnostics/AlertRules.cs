namespace ShiftControl.Diagnostics;

public enum AlertLevel { Warning, Critical }

/// <summary>Regras configuráveis de alerta (gravadas na seção "alerts").</summary>
public sealed class AlertSettings
{
    public bool Enabled { get; set; } = true;
    public bool CpuTemp { get; set; } = true;
    public double CpuTempLimit { get; set; } = 90;
    public bool GpuTemp { get; set; } = true;
    public double GpuTempLimit { get; set; } = 85;
    public bool DiskFree { get; set; } = true;
    public double DiskFreePercent { get; set; } = 10;
    public bool Memory { get; set; } = true;
    public double MemoryPercent { get; set; } = 92;
    public bool DiskHealth { get; set; } = true;
    /// <summary>Tempo que a condição precisa durar antes de alertar (evita picos de 1 s).</summary>
    public int SustainSeconds { get; set; } = 20;
    /// <summary>Intervalo mínimo entre dois alertas da mesma regra.</summary>
    public int CooldownMinutes { get; set; } = 30;

    public AlertSettings Normalize()
    {
        CpuTempLimit = Math.Clamp(CpuTempLimit, 60, 110);
        GpuTempLimit = Math.Clamp(GpuTempLimit, 60, 105);
        DiskFreePercent = Math.Clamp(DiskFreePercent, 1, 50);
        MemoryPercent = Math.Clamp(MemoryPercent, 50, 99);
        SustainSeconds = Math.Clamp(SustainSeconds, 0, 600);
        CooldownMinutes = Math.Clamp(CooldownMinutes, 1, 1440);
        return this;
    }
}

/// <summary>Leitura atual usada pelas regras. Nulo = sem sensor (a regra não dispara).</summary>
public sealed record AlertInput(
    double? CpuTemp,
    double? GpuTemp,
    double? MemoryPercent,
    IReadOnlyList<(string Drive, double FreePercent)> Drives,
    IReadOnlyList<(string Disk, string Health)> DiskHealth);

public sealed record AlertFired(string Rule, AlertLevel Level, double? Value, string? Subject);

/// <summary>
/// Avalia as regras com sustentação (a condição precisa durar N segundos) e espera entre repetições.
/// Puro: o relógio é passado de fora, para testes.
/// </summary>
public sealed class AlertEvaluator
{
    private readonly Dictionary<string, DateTimeOffset> _since = [];
    private readonly Dictionary<string, DateTimeOffset> _lastFired = [];

    public IReadOnlyList<AlertFired> Evaluate(AlertInput input, AlertSettings s, DateTimeOffset now)
    {
        var fired = new List<AlertFired>();
        if (!s.Enabled) { _since.Clear(); return fired; }
        // Ausência de sensor ou desativação interrompe a sustentação da condição.
        if (!s.CpuTemp || input.CpuTemp is null || !double.IsFinite(input.CpuTemp.Value)) _since.Remove("cpu.temp");
        if (!s.GpuTemp || input.GpuTemp is null || !double.IsFinite(input.GpuTemp.Value)) _since.Remove("gpu.temp");
        if (!s.Memory || input.MemoryPercent is null || !double.IsFinite(input.MemoryPercent.Value)) _since.Remove("memory");

        void Check(string key, string rule, bool active, AlertLevel level, double? value, string? subject, bool sustained = true)
        {
            if (!active)
            {
                _since.Remove(key);
                return;
            }
            if (!_since.TryGetValue(key, out var since)) _since[key] = since = now;
            if (sustained && (now - since).TotalSeconds < s.SustainSeconds) return;
            if (_lastFired.TryGetValue(key, out var last) && (now - last).TotalMinutes < s.CooldownMinutes) return;
            _lastFired[key] = now;
            fired.Add(new AlertFired(rule, level, value, subject));
        }

        if (s.CpuTemp && input.CpuTemp is { } ct)
            Check("cpu.temp", "cpu.temp", ct >= s.CpuTempLimit, ct >= s.CpuTempLimit + 5 ? AlertLevel.Critical : AlertLevel.Warning, ct, null);
        if (s.GpuTemp && input.GpuTemp is { } gt)
            Check("gpu.temp", "gpu.temp", gt >= s.GpuTempLimit, gt >= s.GpuTempLimit + 5 ? AlertLevel.Critical : AlertLevel.Warning, gt, null);
        if (s.Memory && input.MemoryPercent is { } mem)
            Check("memory", "memory", mem >= s.MemoryPercent, AlertLevel.Warning, mem, null);
        if (s.DiskFree)
            foreach (var (drive, free) in input.Drives)
                Check("disk.free:" + drive, "disk.free", free <= s.DiskFreePercent, free <= s.DiskFreePercent / 2 ? AlertLevel.Critical : AlertLevel.Warning, free, drive, sustained: false);
        if (s.DiskHealth)
            foreach (var (disk, health) in input.DiskHealth)
                Check("disk.health:" + disk, "disk.health", health is "Warning" or "Unhealthy", health == "Unhealthy" ? AlertLevel.Critical : AlertLevel.Warning, null, disk, sustained: false);
        return fired;
    }
}
