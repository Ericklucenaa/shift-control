using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShiftControl.Maintenance;

public enum BoostProfile { Competitive, Balanced, Quiet }

public sealed record PowerPlan(Guid Guid, string Name, bool Active);

/// <summary>Configuração do Game Boost (gravada na seção "gameBoost").</summary>
public sealed class GameBoostSettings
{
    public bool AutoApply { get; set; }
    public BoostProfile DefaultProfile { get; set; } = BoostProfile.Competitive;
    /// <summary>Perfil por executável (minúsculo, sem caminho). Ausente = perfil padrão.</summary>
    public Dictionary<string, BoostProfile> PerGame { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Programas (nome do executável) a pausar enquanto o jogo roda — só no perfil Competitivo.</summary>
    public List<string> PauseApps { get; set; } = [];

    public GameBoostSettings Normalize()
    {
        PerGame = (PerGame ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Key) && Enum.IsDefined(p.Value))
            .GroupBy(p => Path.GetFileName(p.Key).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
        PauseApps = (PauseApps ?? []).Where(a => !string.IsNullOrWhiteSpace(a) && !BoostEngine.IsProtected(a))
            .Select(a => a.Trim().ToLowerInvariant()).Distinct().ToList();
        if (!Enum.IsDefined(DefaultProfile)) DefaultProfile = BoostProfile.Competitive;
        return this;
    }

    public BoostProfile ProfileFor(string? executable) =>
        executable is not null && PerGame.TryGetValue(executable, out var p) ? p : DefaultProfile;
}

/// <summary>Processo pausado: PID + horário de início (o PID pode ser reutilizado depois que o processo fecha).</summary>
public sealed record SuspendedProcess(int Pid, string Name, long StartTicks);

/// <summary>Estado de um boost ativo, gravado em disco para restaurar tudo se o app fechar no meio.</summary>
public sealed record BoostState(BoostProfile Profile, string? Game, Guid? PreviousPlan, Guid? AppliedPlan, List<SuspendedProcess> Suspended, DateTimeOffset At, long JournalId);

public sealed record RunningApp(string Executable, string Name, int Processes, long MemoryBytes);

/// <summary>
/// Aplica e desfaz o Game Boost: plano de energia do perfil e pausa (suspensão) dos programas escolhidos.
/// Tudo é revertido ao sair do jogo, ao desativar, ou na próxima abertura do app se ele tiver fechado com o boost ativo.
/// </summary>
public static partial class BoostEngine
{
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid PowerSaver = new("a1841308-3334-4a96-8b33-3f8da2c0f53d");

    /// <summary>Processos que nunca são pausados (sistema, áudio, entrada, o próprio app, lojas de jogos que o jogo usa).</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer",
        "fontdrvhost", "audiodg", "ctfmon", "sihost", "taskhostw", "runtimebroker", "searchhost", "startmenuexperiencehost",
        "shellexperiencehost", "textinputhost", "conhost", "msmpeng", "securityhealthservice", "nissrv", "shiftcontrol",
        "presentmon", "steam", "steamwebhelper", "epicgameslauncher", "eadesktop", "battle.net", "riotclientservices",
        "vgc", "vgtray", "easyanticheat", "beservice", "nvcontainer", "nvdisplay.container", "amdrsserv", "lghub", "icue",
    };

    public static bool IsProtected(string executable) =>
        Protected.Contains(Path.GetFileNameWithoutExtension(executable.Trim()));

    #region Plano de energia

    public static IReadOnlyList<PowerPlan> PowerPlans()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        try
        {
            var psi = new ProcessStartInfo(Shell.SystemTool("powercfg.exe"), "/list")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return ParsePlans(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return []; }
    }

    /// <summary>Linhas "GUID: xxxxxxxx-… (Nome) *" — o asterisco marca o ativo.</summary>
    internal static IReadOnlyList<PowerPlan> ParsePlans(string output) =>
        PlanLine().Matches(output).Select(m => new PowerPlan(Guid.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Success)).ToList();

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)\s*(\*)?")]
    private static partial Regex PlanLine();

    /// <summary>Plano para o perfil, entre os que existem no PC. Nulo = manter o atual.</summary>
    public static Guid? PlanFor(BoostProfile profile, IReadOnlyList<PowerPlan> plans)
    {
        bool Has(Guid g) => plans.Any(p => p.Guid == g);
        return profile switch
        {
            BoostProfile.Competitive => Has(UltimatePerformance) ? UltimatePerformance : Has(HighPerformance) ? HighPerformance : null,
            BoostProfile.Balanced => Has(Balanced) ? Balanced : null,
            _ => Has(PowerSaver) ? PowerSaver : Has(Balanced) ? Balanced : null,
        };
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(nint root, ref Guid scheme);

    /// <summary>Troca o plano ativo (não exige administrador para planos existentes).</summary>
    public static bool SetActivePlan(Guid plan)
    {
        var g = plan;
        return PowerSetActiveScheme(0, ref g) == 0;
    }

    #endregion

    #region Pausar programas

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(nint handle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(nint handle);

    /// <summary>Programas com janela ou consumo relevante rodando agora (candidatos a pausar), agrupados por executável.</summary>
    public static IReadOnlyList<RunningApp> RunningApps()
    {
        int self = Environment.ProcessId;
        var groups = new Dictionary<string, (string Name, int Count, long Mem)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.SessionId == 0 || IsProtected(p.ProcessName)) continue;
                    string exe = p.ProcessName.ToLowerInvariant() + ".exe";
                    long mem = p.WorkingSet64;
                    string name = p.MainWindowTitle.Length > 0 ? p.ProcessName : p.ProcessName;
                    try { name = p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d : name; }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    groups[exe] = groups.TryGetValue(exe, out var g) ? (g.Name, g.Count + 1, g.Mem + mem) : (name, 1, mem);
                }
                catch (InvalidOperationException) { } // fechou durante a leitura
            }
        }
        return groups.Where(g => g.Value.Mem >= 30 * 1024 * 1024)
            .Select(g => new RunningApp(g.Key, g.Value.Name, g.Value.Count, g.Value.Mem))
            .OrderByDescending(a => a.MemoryBytes).ToList();
    }

    /// <summary>Pausa todos os processos dos executáveis indicados (exceto o jogo e os protegidos).</summary>
    public static List<SuspendedProcess> Suspend(IEnumerable<string> executables, int? gamePid, Action<IReadOnlyList<SuspendedProcess>>? persisted = null)
    {
        var done = new List<SuspendedProcess>();
        foreach (var exe in executables.Where(e => !IsProtected(e)))
        {
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                using (p)
                {
                    try
                    {
                        if (p.Id == gamePid || p.Id == Environment.ProcessId || p.SessionId != Process.GetCurrentProcess().SessionId) continue;
                        var identity = new SuspendedProcess(p.Id, p.ProcessName, p.StartTime.Ticks);
                        if (NtSuspendProcess(p.Handle) == 0)
                        {
                            done.Add(identity);
                            try { persisted?.Invoke(done); }
                            catch
                            {
                                Resume(done);
                                throw;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                }
            }
        }
        return done;
    }

    /// <summary>Retoma os processos pausados que ainda existem (mesmo PID e mesmo horário de início).</summary>
    public static int Resume(IEnumerable<SuspendedProcess> suspended) => ResumePending(suspended).Resumed;

    public static (int Resumed, List<SuspendedProcess> Pending) ResumePending(IEnumerable<SuspendedProcess> suspended)
    {
        int ok = 0;
        var pending = new List<SuspendedProcess>();
        foreach (var s in suspended)
        {
            try
            {
                using var p = Process.GetProcessById(s.Pid);
                if (p.StartTime.Ticks != s.StartTicks) continue;
                if (NtResumeProcess(p.Handle) == 0) ok++;
                else pending.Add(s);
            }
            catch (System.ComponentModel.Win32Exception) { pending.Add(s); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { } // Já terminou.
        }
        return (ok, pending);
    }

    #endregion

    #region Estado persistido (recuperação após fechar no meio)

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void SaveState(string path, BoostState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
        File.Move(temporary, path, overwrite: true);
    }

    public static BoostState? LoadState(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<BoostState>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    public static void ClearState(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    #endregion
}
