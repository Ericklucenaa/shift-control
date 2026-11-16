using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace ShiftControl.Maintenance;

public enum RepairTool { Sfc, DismRestoreHealth, ChkdskScan }

public enum RepairOutcome { Healthy, Repaired, ProblemsRemaining, RebootRequired, RequiresAdmin, Failed }

public sealed record RepairResult(RepairTool Tool, RepairOutcome Outcome, int ExitCode, string Output);

/// <summary>Ferramentas oficiais de reparo do Windows (SFC, DISM e verificação de disco online), com o resultado explicado.</summary>
public static class Repair
{
    public static async Task<RepairResult> RunAsync(RepairTool tool, Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (!Shell.IsElevated) return new RepairResult(tool, RepairOutcome.RequiresAdmin, -1, "");
        string sysDrive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        var (file, args, unicode) = tool switch
        {
            RepairTool.Sfc => (Shell.SystemTool("sfc.exe"), "/scannow", true),
            RepairTool.DismRestoreHealth => (Shell.SystemTool("dism.exe"), "/Online /Cleanup-Image /RestoreHealth", false),
            _ => (Shell.SystemTool("chkdsk.exe"), $"{sysDrive} /scan", false),
        };
        var r = await Shell.RunAsync(file, args, onLine, unicode, ct);
        return new RepairResult(tool, Interpret(tool, r.ExitCode, r.Output), r.ExitCode, r.Output);
    }

    /// <summary>
    /// SFC não tem códigos de saída úteis: o resultado vem do texto (que muda com o idioma); por isso procura os
    /// identificadores estáveis das mensagens em português e inglês. DISM e CHKDSK usam código de saída.
    /// </summary>
    internal static RepairOutcome Interpret(RepairTool tool, int exitCode, string output)
    {
        string o = output.ToLowerInvariant();
        switch (tool)
        {
            case RepairTool.Sfc:
                if (o.Contains("não encontrou nenhuma violação") || o.Contains("did not find any integrity violations")) return RepairOutcome.Healthy;
                if (o.Contains("reparou-os com êxito") || o.Contains("successfully repaired")) return RepairOutcome.Repaired;
                if (o.Contains("não pôde corrigir") || o.Contains("unable to fix")) return RepairOutcome.ProblemsRemaining;
                if (o.Contains("reinicie") || o.Contains("reboot")) return RepairOutcome.RebootRequired;
                return RepairOutcome.Failed; // Texto desconhecido não comprova integridade.
            case RepairTool.DismRestoreHealth:
                if (exitCode == 3010) return RepairOutcome.RebootRequired;
                if (exitCode != 0) return RepairOutcome.Failed;
                return o.Contains("corrupção") && o.Contains("reparad") || o.Contains("corruption was repaired") ? RepairOutcome.Repaired : RepairOutcome.Healthy;
            default:
                return exitCode switch { 0 => RepairOutcome.Healthy, 1 => RepairOutcome.Repaired, 2 or 3 => RepairOutcome.ProblemsRemaining, _ => RepairOutcome.Failed };
        }
    }
}

public enum ErrorKind { AppCrash, AppHang, Bsod, UnexpectedShutdown, DriverTimeout, DiskError, HardwareError }

public sealed record SystemErrorEvent(ErrorKind Kind, DateTime At, string Source, string Subject, string Message);

/// <summary>
/// Histórico de erros a partir do Visualizador de Eventos (leitura não exige administrador):
/// programas que travaram, telas azuis, desligamentos inesperados, reinícios do driver de vídeo, erros de disco e de hardware.
/// </summary>
public static class ErrorHistory
{
    private sealed record Rule(ErrorKind Kind, string Log, string Query);

    private static readonly Rule[] Rules =
    [
        new(ErrorKind.AppCrash, "Application", "*[System[Provider[@Name='Application Error'] and (EventID=1000)]]"),
        new(ErrorKind.AppHang, "Application", "*[System[Provider[@Name='Application Hang'] and (EventID=1002)]]"),
        new(ErrorKind.Bsod, "System", "*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001)]]"),
        new(ErrorKind.UnexpectedShutdown, "System", "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41)]]"),
        new(ErrorKind.DriverTimeout, "System", "*[System[Provider[@Name='Display'] and (EventID=4101)]]"),
        new(ErrorKind.DiskError, "System", "*[System[(Provider[@Name='disk'] or Provider[@Name='Microsoft-Windows-Disk']) and (EventID=7 or EventID=51 or EventID=153) and Level<=3]]"),
        new(ErrorKind.HardwareError, "System", "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and Level<=3]]"),
    ];

    public static IReadOnlyList<SystemErrorEvent> Read(TimeSpan window, int maxPerKind = 200)
    {
        var since = DateTime.Now - window;
        var list = new List<SystemErrorEvent>();
        foreach (var rule in Rules)
        {
            try
            {
                var query = new EventLogQuery(rule.Log, PathType.LogName, rule.Query) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                int n = 0;
                for (EventRecord? e = reader.ReadEvent(); e is not null && n < maxPerKind; e = reader.ReadEvent(), n++)
                {
                    using (e)
                    {
                        if (e.TimeCreated is not { } at || at < since) break;
                        string message = SafeMessage(e);
                        list.Add(new SystemErrorEvent(rule.Kind, at, e.ProviderName ?? "", Subject(rule.Kind, e, message), message));
                    }
                }
            }
            catch (EventLogException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list.OrderByDescending(e => e.At).ToList();
    }

    private static string SafeMessage(EventRecord e)
    {
        try { return e.FormatDescription() ?? ""; }
        catch (EventLogException) { return ""; }
    }

    /// <summary>O que falhou: nome do programa (falhas de app), código da tela azul, ou o dispositivo/fonte.</summary>
    internal static string Subject(ErrorKind kind, EventRecord? e, string message)
    {
        switch (kind)
        {
            case ErrorKind.AppCrash or ErrorKind.AppHang:
                if (e?.Properties.Count > 0 && e.Properties[0].Value is string app && app.Length > 0) return app;
                return FirstMatch(message, @"([\w.\-]+\.exe)") ?? "";
            case ErrorKind.Bsod:
                return FirstMatch(message, @"(0x[0-9a-fA-F]{8,16})") ?? "";
            default:
                return e?.ProviderName ?? "";
        }
    }

    private static string? FirstMatch(string text, string pattern) => Regex.Match(text, pattern) is { Success: true } m ? m.Groups[1].Value : null;
}

public sealed record DiskHealth(
    int Index,
    string Model,
    string MediaType,
    string Bus,
    string HealthStatus,
    string? OperationalStatus,
    double? TemperatureC,
    double? WearPercent,
    long? PowerOnHours,
    long? ReadErrors,
    long? WriteErrors,
    bool CountersAvailable);

/// <summary>
/// Saúde dos discos pela API de Armazenamento do Windows: estado (MSFT_PhysicalDisk) e, com administrador,
/// contadores de confiabilidade (MSFT_StorageReliabilityCounter): desgaste, temperatura, horas ligadas e erros.
/// </summary>
public static class DiskHealthReader
{
    public static IReadOnlyList<DiskHealth> Read()
    {
        const string scope = @"root\Microsoft\Windows\Storage";
        var disks = Shell.Wmi("SELECT ObjectId, DeviceId, FriendlyName, MediaType, BusType, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk", o => (
            ObjectId: o["ObjectId"] as string ?? "",
            Index: int.TryParse(o["DeviceId"]?.ToString(), out int i) ? i : -1,
            Model: (o["FriendlyName"] as string ?? "Disco").Trim(),
            Media: Convert.ToInt32(o["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "—" },
            Bus: Convert.ToInt32(o["BusType"] ?? 0) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 10 => "SAS", 8 => "RAID", _ => "—" },
            Health: Convert.ToInt32(o["HealthStatus"] ?? 5) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
            Operational: (o["OperationalStatus"] as ushort[])?.FirstOrDefault() switch { 2 => "OK", 0xD00F => "Predictive failure", null => null, var v => v.ToString() }), scope);

        var result = new List<DiskHealth>();
        foreach (var d in disks.OrderBy(d => d.Index))
        {
            // Contadores ligados ao disco por associação (exige administrador).
            var counters = Shell.Wmi(
                $"ASSOCIATORS OF {{MSFT_PhysicalDisk.ObjectId=\"{d.ObjectId.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"}} WHERE ResultClass = MSFT_StorageReliabilityCounter",
                o => (Temp: ToDouble(o["Temperature"]), Wear: ToDouble(o["Wear"]), Hours: ToLong(o["PowerOnHours"]),
                      ReadErr: ToLong(o["ReadErrorsUncorrected"]), WriteErr: ToLong(o["WriteErrorsUncorrected"])), scope).FirstOrDefault();
            bool has = counters != default;
            result.Add(new DiskHealth(d.Index, d.Model, d.Media, d.Bus, d.Health, d.Operational,
                has && counters.Temp > 0 ? counters.Temp : null, has ? counters.Wear : null, has ? counters.Hours : null,
                has ? counters.ReadErr : null, has ? counters.WriteErr : null, has));
        }
        return result;
    }

    private static double? ToDouble(object? v) => v is null ? null : Convert.ToDouble(v);
    private static long? ToLong(object? v) => v is null ? null : Convert.ToInt64(v);
}
