using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ShiftControl.App.Overlay;
using ShiftControl.Core;
using ShiftControl.Maintenance;
using ShiftControl.Storage;

namespace ShiftControl.App.Services;

/// <summary>
/// Game Boost: aplica o perfil (plano de energia + pausa de programas) quando um jogo abre e desfaz quando fecha.
/// O estado ativo fica gravado em disco: se o app fechar no meio, a próxima abertura restaura tudo.
/// </summary>
public sealed class GameBoostService : IDisposable
{
    public const string SettingsKey = "gameBoost";
    public const string UndoKind = "boost.restore";

    private readonly ISettingsStore _store;
    private readonly ChangeJournal _journal;
    private readonly GameSessionStore _sessions;
    private readonly OverlayService _overlay;
    private readonly ILogger<GameBoostService> _log;
    private readonly string _statePath = Path.Combine(AppPaths.DataDirectory, "boost-state.json");
    private DispatcherQueueTimer? _timer;
    private HashSet<string> _knownGames = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _knownGamesAt;

    public GameBoostService(ISettingsStore store, ChangeJournal journal, GameSessionStore sessions, OverlayService overlay, ILogger<GameBoostService> log)
    {
        _store = store;
        _journal = journal;
        _sessions = sessions;
        _overlay = overlay;
        _log = log;
        Settings = (store.LoadSection<GameBoostSettings>(SettingsKey) ?? new GameBoostSettings()).Normalize();
    }

    public GameBoostSettings Settings { get; private set; }
    public BoostState? Active { get; private set; }
    public bool IsActive => Active is not null;

    /// <summary>Estado mudou (ativado/desativado/configuração). Na thread da interface.</summary>
    public event EventHandler? Changed;

    public void Initialize(DispatcherQueue ui)
    {
        RecoverAfterCrash();
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += (_, _) => Detect();
        _timer.Start();
    }

    public void Update(Action<GameBoostSettings> change)
    {
        var json = JsonSerializer.Serialize(Settings);
        var next = JsonSerializer.Deserialize<GameBoostSettings>(json)!;
        change(next);
        Settings = next.Normalize();
        try { _store.SaveSection(SettingsKey, Settings); }
        catch (Exception ex) { _log.LogError(ex, "Não foi possível gravar as configurações do Game Boost."); }
        _knownGamesAt = default;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Jogos conhecidos: os que têm sessões gravadas e os com perfil próprio.</summary>
    public IReadOnlyCollection<string> KnownGames()
    {
        if (DateTime.UtcNow - _knownGamesAt > TimeSpan.FromMinutes(1))
        {
            var set = new HashSet<string>(Settings.PerGame.Keys, StringComparer.OrdinalIgnoreCase);
            try { foreach (var g in _sessions.Games()) set.Add(g); }
            catch (Exception ex) { _log.LogWarning(ex, "Falha ao ler os jogos das sessões."); }
            _knownGames = set;
            _knownGamesAt = DateTime.UtcNow;
        }
        return _knownGames;
    }

    private void Detect()
    {
        try
        {
            var (exe, pid) = RunningGame();
            if (Active is null)
            {
                if (Settings.AutoApply && exe is not null) Activate(Settings.ProfileFor(exe), exe, pid);
            }
            else if (Active.Game is not null && !string.Equals(Active.Game, exe, StringComparison.OrdinalIgnoreCase))
            {
                Deactivate(L.Get("boost.restoredGameClosed"));
            }
            _overlay.ActiveBoost = Active?.Profile.ToString();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha na detecção do Game Boost.");
        }
    }

    /// <summary>O jogo do overlay (detecção por quadros) ou um jogo conhecido em execução.</summary>
    private (string? Exe, int? Pid) RunningGame()
    {
        if (_overlay.Target is { } t) return (global::ShiftControl.Overlay.GameTargetPolicy.Normalize(t.Executable), t.ProcessId);
        foreach (var game in KnownGames())
        {
            var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game));
            try
            {
                if (procs.Length > 0) return (game, procs[0].Id);
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }
        return (null, null);
    }

    public sealed record ActivationResult(bool PlanChanged, string? PlanName, int Paused, bool PlanUnavailable);

    public ActivationResult Activate(BoostProfile profile, string? game, int? gamePid)
    {
        if (Active is not null)
        {
            Deactivate(L.Get("boost.restoredSwitch"));
            if (Active is not null) throw new InvalidOperationException(L.Get("boost.restorePending"));
        }

        var plans = BoostEngine.PowerPlans();
        var previous = plans.FirstOrDefault(p => p.Active)?.Guid;
        var target = BoostEngine.PlanFor(profile, plans);
        bool changed = false;
        // Grava a intenção antes de alterar energia. Cada suspensão também atualiza o arquivo de recuperação.
        var pending = new BoostState(profile, game, previous, target, [], DateTimeOffset.Now, 0);
        BoostEngine.SaveState(_statePath, pending);
        List<SuspendedProcess> suspended;
        try
        {
            if (target is { } t && t != previous && previous is not null) changed = BoostEngine.SetActivePlan(t);
            suspended = profile == BoostProfile.Competitive ? BoostEngine.Suspend(Settings.PauseApps, gamePid,
                paused => BoostEngine.SaveState(_statePath, pending with { Suspended = paused.ToList() })) : [];
        }
        catch
        {
            if (changed && previous is { } old) BoostEngine.SetActivePlan(old);
            throw;
        }

        string planName = plans.FirstOrDefault(p => p.Guid == target)?.Name ?? "";
        string title = game is null ? L.F("boost.journalManual", L.Get("boost.profile." + profile)) : L.F("boost.journalGame", L.Get("boost.profile." + profile), game);
        string detail = L.F("boost.journalDetail", changed ? planName : L.Get("boost.planKept"), suspended.Count);
        var state = new BoostState(profile, game, changed ? previous : null, changed ? target : null, suspended, DateTimeOffset.Now, 0);
        Active = state;
        BoostEngine.SaveState(_statePath, Active);
        try
        {
            long id = _journal.Record("gameboost", title, detail, UndoKind, JsonSerializer.Serialize(state));
            Active = state with { JournalId = id };
            BoostEngine.SaveState(_statePath, Active);
        }
        catch
        {
            Restore(Active);
            Active = null;
            throw;
        }
        _overlay.ActiveBoost = profile.ToString();
        _log.LogInformation("Game Boost {Profile} ativado ({Game}); plano {Plan}, {Paused} processos pausados.", profile, game ?? "manual", changed ? planName : "mantido", suspended.Count);
        Changed?.Invoke(this, EventArgs.Empty);
        return new ActivationResult(changed, planName, suspended.Count, target is null);
    }

    /// <summary>Desfaz o boost ativo: retoma os programas e volta ao plano anterior.</summary>
    public string Deactivate(string reason)
    {
        var state = Active;
        if (state is null) return "";
        var attempt = RestoreAttempt(state);
        string result = attempt.Message;
        Active = attempt.Pending;
        if (Active is null) BoostEngine.ClearState(_statePath);
        else BoostEngine.SaveState(_statePath, Active);
        _overlay.ActiveBoost = Active?.Profile.ToString();
        if (state.JournalId > 0) _journal.SetState(state.JournalId, Active is null ? ChangeState.Reverted : ChangeState.RevertFailed, reason + " " + result);
        _log.LogInformation("Game Boost desativado: {Reason} {Result}", reason, result);
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>Reverte um estado (usado também pela Central de desfazer). Retorna o resumo.</summary>
    public static string Restore(BoostState state)
    {
        return RestoreAttempt(state).Message;
    }

    private static (string Message, BoostState? Pending) RestoreAttempt(BoostState state)
    {
        var resumed = BoostEngine.ResumePending(state.Suspended);
        bool restored = state.PreviousPlan is not { } prev || BoostEngine.SetActivePlan(prev);
        bool complete = restored && resumed.Pending.Count == 0;
        string message = L.F("boost.restoreSummary", resumed.Resumed, restored ? L.Get("boost.planRestored") : L.Get("boost.planUnchanged"));
        if (!complete) message += " " + L.Get("boost.restorePending");
        return (message, complete ? null : state with { Suspended = resumed.Pending, PreviousPlan = restored ? null : state.PreviousPlan });
    }

    /// <summary>Chamado pela Central de desfazer: se for o boost ativo, desativa; senão só restaura o registrado.</summary>
    public (bool Ok, string Message) RevertEntry(ChangeEntry entry)
    {
        if (Active is { } a && a.JournalId == entry.Id)
        {
            var msg = Deactivate(L.Get("boost.restoredManual"));
            return (Active is null, msg);
        }
        var state = JsonSerializer.Deserialize<BoostState>(entry.UndoData ?? "");
        if (state is null) return (false, L.Get("undo.notRevertible"));
        var result = RestoreAttempt(state);
        return (result.Pending is null, result.Message);
    }

    private void RecoverAfterCrash()
    {
        var state = BoostEngine.LoadState(_statePath);
        if (state is null) return;
        var attempt = RestoreAttempt(state);
        string result = attempt.Message;
        Active = attempt.Pending;
        if (Active is null) BoostEngine.ClearState(_statePath);
        else BoostEngine.SaveState(_statePath, Active);
        if (state.JournalId > 0) _journal.SetState(state.JournalId, Active is null ? ChangeState.Reverted : ChangeState.RevertFailed, L.Get("boost.restoredRecovery") + " " + result);
        _log.LogWarning("O app fechou com o Game Boost ativo; estado restaurado: {Result}", result);
    }

    public void Dispose()
    {
        _timer?.Stop();
        if (Active is not null) Deactivate(L.Get("boost.restoredExit"));
    }
}
