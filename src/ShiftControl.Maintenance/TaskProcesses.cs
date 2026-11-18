using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ShiftControl.Maintenance;

public enum ProcessSort { Cpu, Memory, Name, Pid }
public enum EndProcessResult { Ended, Ending, AlreadyExited, Protected, AccessDenied, IdentityChanged, CannotVerify, Failed }

public sealed record TaskProcess(int Pid, string Name, long? CreationFileTime, double? CpuPercent = null,
    long? MemoryBytes = null, long? PrivateBytes = null, int? Threads = null, int? SessionId = null,
    bool HasWindow = false, bool NotResponding = false)
{
    public string Key => $"{Pid}:{CreationFileTime}";
    public bool CanEnd => CreationFileTime is > 0 && !TaskProcesses.IsProtected(Pid, Name, SessionId);
}

public sealed record TaskProcessDetails(string? Path, string? Description, string? Publisher, DateTimeOffset? StartedAt, TimeSpan? CpuTime);

/// <summary>Processos com o mesmo nome (ex.: os vários chrome.exe), vistos e encerrados juntos.</summary>
public sealed record TaskProcessGroup(IReadOnlyList<TaskProcess> Members)
{
    public string Name => Members[0].Name;
    public string Key => "group:" + Name.ToUpperInvariant();
    public int Count => Members.Count;
    public bool CanEnd => Members.Any(m => m.CanEnd);

    /// <summary>Uma linha com os totais do grupo. A identidade (PID, horário de criação) é a do membro com janela, ou a
    /// do mais antigo: ela só serve para detalhes e ordenação; o encerramento usa os membros, um a um.</summary>
    public TaskProcess Summary()
    {
        var head = Members.FirstOrDefault(m => m.HasWindow) ?? Members[0];
        return head with
        {
            CpuPercent = Members.Any(m => m.CpuPercent.HasValue) ? Math.Clamp(Members.Sum(m => m.CpuPercent ?? 0), 0, 100) : null,
            MemoryBytes = Members.Any(m => m.MemoryBytes.HasValue) ? Members.Sum(m => m.MemoryBytes ?? 0) : null,
            PrivateBytes = Members.Any(m => m.PrivateBytes.HasValue) ? Members.Sum(m => m.PrivateBytes ?? 0) : null,
            Threads = Members.Any(m => m.Threads.HasValue) ? Members.Sum(m => m.Threads ?? 0) : null,
            HasWindow = Members.Any(m => m.HasWindow),
            NotResponding = Members.Any(m => m.NotResponding),
        };
    }
}

/// <summary>Resultado de encerrar todos os processos de um nome.</summary>
public sealed record GroupEndResult(string Name, int Ended, int Blocked)
{
    public int Total => Ended + Blocked;
}

/// <summary>Uma amostragem por intervalo, sem reter handles. CPU normalizada pela capacidade total do PC.</summary>
public sealed class ProcessSampler
{
    private Dictionary<string, (double Cpu, long At)> _previous = [];

    public IReadOnlyList<TaskProcess> Sample(CancellationToken ct = default)
    {
        long at = Stopwatch.GetTimestamp();
        var next = new Dictionary<string, (double, long)>();
        var result = new List<TaskProcess>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var p in processes)
            {
                ct.ThrowIfCancellationRequested();
                string? name = Read(() => p.ProcessName);
                if (name is null) continue; // Terminou durante a leitura.
                var item = new TaskProcess(p.Id, name + ".exe", Value(() => p.StartTime.ToFileTimeUtc()),
                    MemoryBytes: Value(() => p.WorkingSet64), PrivateBytes: Value(() => p.PrivateMemorySize64),
                    Threads: Value(() => p.Threads.Count), SessionId: Value(() => p.SessionId));
                double? cpu = Value(() => p.TotalProcessorTime.TotalSeconds);
                if (cpu is { } current && item.CreationFileTime is not null)
                {
                    if (_previous.TryGetValue(item.Key, out var last))
                        item = item with { CpuPercent = CpuPercent(current - last.Cpu, Stopwatch.GetElapsedTime(last.At, at).TotalSeconds, Environment.ProcessorCount) };
                    next[item.Key] = (current, at);
                }
                nint window = Value(() => p.MainWindowHandle) ?? 0;
                result.Add(item with { HasWindow = window != 0, NotResponding = window != 0 && TaskProcesses.IsHungAppWindow(window) });
            }
        }
        finally { foreach (var p in processes) p.Dispose(); }
        _previous = next; // Remove processos encerrados e evita acumular PIDs antigos.
        return result;
    }

    public void Reset() => _previous.Clear();

    public static double? CpuPercent(double cpuSeconds, double elapsedSeconds, int processors) =>
        !double.IsFinite(cpuSeconds) || !double.IsFinite(elapsedSeconds) || cpuSeconds < 0 || elapsedSeconds <= 0 || processors <= 0
            ? null : Math.Clamp(cpuSeconds / elapsedSeconds / processors * 100, 0, 100);

    internal static T? Value<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
    }
    private static string? Read(Func<string> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return null; }
    }
}

public static class TaskProcesses
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "explorer", "audiodg", "fontdrvhost", "memory compression", "secure system", "shiftcontrol",
    };

    public static bool IsProtected(int pid, string name, int? sessionId) => pid is 0 or 4 || pid == Environment.ProcessId
        || sessionId is null or 0 || ProtectedNames.Contains(System.IO.Path.GetFileNameWithoutExtension(name));

    public static IReadOnlyList<TaskProcess> Query(IEnumerable<TaskProcess> source, string? search, ProcessSort sort, bool appsOnly)
    {
        var items = source.Where(p => !appsOnly || p.HasWindow);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string text = search.Trim();
            items = items.Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || p.Pid.ToString().Contains(text, StringComparison.Ordinal));
        }
        return (sort switch
        {
            ProcessSort.Cpu => items.OrderByDescending(p => p.CpuPercent),
            ProcessSort.Memory => items.OrderByDescending(p => p.MemoryBytes),
            ProcessSort.Pid => items.OrderBy(p => p.Pid),
            _ => items.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
        }).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid).ToList();
    }

    /// <summary>Um grupo por nome de executável (sem diferenciar maiúsculas). A busca acha o grupo pelo nome ou pelo PID de
    /// qualquer membro; a ordenação usa os totais do grupo.</summary>
    public static IReadOnlyList<TaskProcessGroup> QueryGroups(IEnumerable<TaskProcess> source, string? search, ProcessSort sort, bool appsOnly)
    {
        var groups = source.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TaskProcessGroup(g.OrderBy(p => p.CreationFileTime ?? long.MaxValue).ThenBy(p => p.Pid).ToList()));
        if (appsOnly) groups = groups.Where(g => g.Members.Any(m => m.HasWindow));
        if (!string.IsNullOrWhiteSpace(search))
        {
            string text = search.Trim();
            groups = groups.Where(g => g.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || g.Members.Any(m => m.Pid.ToString().Contains(text, StringComparison.Ordinal)));
        }
        var list = groups.Select(g => (Group: g, Row: g.Summary())).ToList();
        IEnumerable<(TaskProcessGroup Group, TaskProcess Row)> ordered = sort switch
        {
            ProcessSort.Cpu => list.OrderByDescending(x => x.Row.CpuPercent),
            ProcessSort.Memory => list.OrderByDescending(x => x.Row.MemoryBytes),
            ProcessSort.Pid => list.OrderBy(x => x.Group.Members.Min(m => m.Pid)),
            _ => list.OrderBy(x => x.Group.Name, StringComparer.OrdinalIgnoreCase),
        };
        return ordered.Select(x => x.Group).ToList();
    }

    /// <summary>Encerra todos os processos deste nome que estão na lista, o mais antigo primeiro (no Chrome e
    /// parecidos, o processo principal: os filhos costumam sair junto). Cada um passa pelas mesmas verificações de
    /// <see cref="End"/>: protegidos, de serviço ou cuja identidade mudou nunca são tocados.</summary>
    public static IReadOnlyList<(TaskProcess Item, EndProcessResult Result)> EndAll(string name, IEnumerable<TaskProcess> current,
        Func<TaskProcess, EndProcessResult>? end = null)
    {
        end ??= End;
        return current.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.CreationFileTime ?? long.MaxValue).ThenBy(p => p.Pid)
            .Select(p => (p, p.CanEnd ? end(p) : EndProcessResult.Protected)).ToList();
    }

    /// <summary>Conta cada processo uma vez, pelo último resultado (numa segunda passada, um processo que estava
    /// "saindo" aparece de novo e não pode contar duas vezes).</summary>
    public static GroupEndResult Summarize(string name, IEnumerable<(TaskProcess Item, EndProcessResult Result)> results)
    {
        var last = new Dictionary<string, EndProcessResult>();
        foreach (var (item, result) in results) last[item.Key] = result;
        int ended = last.Values.Count(r => r is EndProcessResult.Ended or EndProcessResult.Ending or EndProcessResult.AlreadyExited);
        return new(name, ended, last.Count - ended);
    }

    public static TaskProcessDetails? Details(TaskProcess item)
    {
        if (item.CreationFileTime is not { } identity) return null;
        using var handle = OpenProcess(0x1000 | 0x100000, false, item.Pid);
        if (handle.IsInvalid || WaitForSingleObject(handle, 0) != 0x102 || !GetProcessTimes(handle, out long creation, out _, out long kernel, out long user) || creation != identity) return null;
        var path = new StringBuilder(32768);
        int length = path.Capacity;
        string? file = QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
        string? description = null, company = null;
        if (file is not null)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(file);
                description = version.FileDescription;
                company = version.CompanyName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { }
        }
        return new(file, description, company, DateTimeOffset.FromFileTime(creation), TimeSpan.FromTicks(kernel + user));
    }

    /// <summary>Revalida identidade e criticidade no mesmo handle usado para encerrar. Nunca encerra árvore inteira.</summary>
    public static EndProcessResult End(TaskProcess item)
    {
        if (!item.CanEnd) return EndProcessResult.Protected;
        using var handle = OpenProcess(0x1000 | 0x0001 | 0x100000, false, item.Pid);
        if (handle.IsInvalid) return Marshal.GetLastWin32Error() switch { 87 => EndProcessResult.AlreadyExited, 5 => EndProcessResult.AccessDenied, _ => EndProcessResult.Failed };
        if (!GetProcessTimes(handle, out long creation, out _, out _, out _)) return EndProcessResult.CannotVerify;
        if (creation != item.CreationFileTime) return EndProcessResult.IdentityChanged;
        if (!ProcessIdToSessionId(item.Pid, out uint session) || session == 0) return EndProcessResult.Protected;
        if (!IsProcessCritical(handle, out bool critical)) return EndProcessResult.CannotVerify;
        if (critical) return EndProcessResult.Protected;
        if (WaitForSingleObject(handle, 0) == 0) return EndProcessResult.AlreadyExited;
        if (!TerminateProcess(handle, 1)) return Marshal.GetLastWin32Error() == 5 ? EndProcessResult.AccessDenied : EndProcessResult.Failed;
        return WaitForSingleObject(handle, 2000) == 0 ? EndProcessResult.Ended : EndProcessResult.Ending;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessCritical(SafeProcessHandle process, out bool critical);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(int pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("user32.dll")] internal static extern bool IsHungAppWindow(nint hwnd);
}
