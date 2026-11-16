namespace ShiftControl.Setup;

public enum JobKind { Install, Upgrade }

public enum JobState { Queued, Running, Done, Failed, Skipped, Cancelled }

/// <summary>Um item da fila. Mutável só pela fila (na thread dela); a interface lê pelos eventos.</summary>
public sealed class InstallJob
{
    public required CatalogPackage Package { get; init; }
    public required JobKind Kind { get; init; }
    public JobState State { get; internal set; } = JobState.Queued;
    public WinGetOutcome? Outcome { get; internal set; }
    public int? ExitCode { get; internal set; }
    public string? LastLine { get; internal set; }
    public string? OutputTail { get; internal set; }
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }
}

public sealed record InstallReport(IReadOnlyList<InstallJob> Jobs, DateTimeOffset Started, DateTimeOffset Finished)
{
    public int Succeeded => Jobs.Count(j => j.State == JobState.Done);
    public int Failed => Jobs.Count(j => j.State == JobState.Failed);
    public bool RebootRequired => Jobs.Any(j => j.Outcome == WinGetOutcome.RebootRequired);
}

/// <summary>
/// Executa instalações uma por vez (o WinGet não instala em paralelo com segurança) e reporta cada passo.
/// Um item que falha não interrompe os demais. Cancelar interrompe o item atual e marca o resto como cancelado.
/// </summary>
public sealed class InstallQueue
{
    private readonly WinGetClient _winget;

    public InstallQueue(WinGetClient winget) => _winget = winget;

    /// <summary>Disparado na thread da fila quando um item muda (estado ou linha de progresso).</summary>
    public event Action<InstallJob>? JobChanged;

    /// <summary>Disparado quando uma instalação termina com sucesso (para registrar na Central de desfazer).</summary>
    public event Action<InstallJob>? Installed;

    public async Task<InstallReport> RunAsync(IReadOnlyList<InstallJob> jobs, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        foreach (var job in jobs)
        {
            if (ct.IsCancellationRequested)
            {
                job.State = JobState.Cancelled;
                JobChanged?.Invoke(job);
                continue;
            }

            job.State = JobState.Running;
            job.StartedAt = DateTimeOffset.Now;
            JobChanged?.Invoke(job);

            void OnLine(string line)
            {
                if (WinGetClient.Progress(line) is not { } p || p == job.LastLine) return;
                job.LastLine = p;
                JobChanged?.Invoke(job);
            }

            try
            {
                var (outcome, result) = job.Kind == JobKind.Install
                    ? await _winget.InstallAsync(job.Package, OnLine, ct)
                    : await _winget.UpgradeAsync(job.Package.Id, job.Package.Source, OnLine, ct);
                job.Outcome = outcome;
                job.ExitCode = result.ExitCode;
                job.OutputTail = Tail(result.Output, 12);
                job.State = WinGetCodes.IsOk(outcome) ? JobState.Done : JobState.Failed;
                if (job.Kind == JobKind.Install && outcome is WinGetOutcome.Success or WinGetOutcome.RebootRequired)
                    Installed?.Invoke(job);
            }
            catch (OperationCanceledException)
            {
                job.State = JobState.Cancelled;
                job.Outcome = WinGetOutcome.Cancelled;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                job.State = JobState.Failed;
                job.Outcome = WinGetOutcome.Failed;
                job.OutputTail = ex.Message;
            }
            job.FinishedAt = DateTimeOffset.Now;
            JobChanged?.Invoke(job);
        }
        return new InstallReport(jobs, started, DateTimeOffset.Now);
    }

    internal static string Tail(string output, int lines)
    {
        var all = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => WinGetClient.Progress(l) is not null && !l.Contains('█') && !l.Contains('▒'))
            .ToList();
        return string.Join('\n', all.Skip(Math.Max(0, all.Count - lines)));
    }
}
