using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShiftControl.App.Services;
using ShiftControl.Diagnostics;
using ShiftControl.Maintenance;
using ShiftControl.Overlay;
using ShiftControl.Storage;

namespace ShiftControl.App.ViewModels;

/// <summary>Jogo com perfil próprio do Game Boost (linha da lista "Perfis por jogo").</summary>
public sealed partial class GameProfileVm : ObservableObject
{
    private readonly Action<GameProfileVm> _changed;

    public GameProfileVm(string executable, int profileIndex, Action<GameProfileVm> changed)
    {
        Executable = executable;
        _changed = changed;
        ProfileIndex = profileIndex;
        _ready = true;
    }

    private readonly bool _ready;
    public string Executable { get; }

    /// <summary>0 = padrão, 1 = Competitivo, 2 = Equilibrado, 3 = Silencioso.</summary>
    [ObservableProperty] public partial int ProfileIndex { get; set; }

    partial void OnProfileIndexChanged(int value)
    {
        if (_ready) _changed(this);
    }
}

public sealed partial class GameBoostViewModel : ToolViewModel
{
    private readonly GameBoostService _boost;
    private readonly GameSessionStore _sessions;
    private readonly ILogger<GameBoostViewModel> _log;
    private bool _loading;

    public GameBoostViewModel(GameBoostService boost, GameSessionStore sessions, ILogger<GameBoostViewModel> log)
    {
        _boost = boost;
        _sessions = sessions;
        _log = log;
        _boost.Changed += (_, _) => RefreshState();
        ProfileOptions = [L.Get("boost.profile.default"), L.Get("boost.profile.Competitive"), L.Get("boost.profile.Balanced"), L.Get("boost.profile.Quiet")];
    }

    public IReadOnlyList<string> ProfileOptions { get; }
    public ObservableCollection<GameProfileVm> Games { get; } = [];
    public ObservableCollection<RowVm> Apps { get; } = [];
    public ObservableCollection<RowVm> Comparison { get; } = [];
    public ObservableCollection<RowVm> Thermal { get; } = [];

    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial string StateTitle { get; set; } = "";
    [ObservableProperty] public partial string StateDetail { get; set; } = "";
    [ObservableProperty] public partial bool AutoApply { get; set; }
    [ObservableProperty] public partial int DefaultProfile { get; set; }
    [ObservableProperty] public partial bool HasGames { get; set; }
    [ObservableProperty] public partial bool HasComparison { get; set; }
    [ObservableProperty] public partial bool HasThermal { get; set; }
    [ObservableProperty] public partial string ThermalTitle { get; set; } = "";
    [ObservableProperty] public partial string PlanInfo { get; set; } = "";
    [ObservableProperty] public partial string NewGame { get; set; } = "";

    [RelayCommand]
    private void AddGame()
    {
        string exe = Path.GetFileName(NewGame.Trim()).ToLowerInvariant();
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || BoostEngine.IsProtected(exe))
        {
            Error(L.Get("boost.invalidGame"));
            return;
        }
        _boost.Update(s => s.PerGame[exe] = s.DefaultProfile);
        if (Games.All(g => !g.Executable.Equals(exe, StringComparison.OrdinalIgnoreCase)))
            Games.Add(new GameProfileVm(exe, DefaultProfile + 1, OnGameProfileChanged));
        HasGames = true;
        NewGame = "";
    }

    partial void OnAutoApplyChanged(bool value)
    {
        if (!_loading) _boost.Update(s => s.AutoApply = value);
    }

    partial void OnDefaultProfileChanged(int value)
    {
        if (!_loading) _boost.Update(s => s.DefaultProfile = (BoostProfile)value);
    }

    public async Task LoadAsync()
    {
        _loading = true;
        AutoApply = _boost.Settings.AutoApply;
        DefaultProfile = (int)_boost.Settings.DefaultProfile;
        _loading = false;
        RefreshState();

        Games.Clear();
        foreach (var g in _boost.KnownGames().OrderBy(g => g))
            Games.Add(new GameProfileVm(g, _boost.Settings.PerGame.TryGetValue(g, out var p) ? (int)p + 1 : 0, OnGameProfileChanged));
        HasGames = Games.Count > 0;

        var plans = await Task.Run(BoostEngine.PowerPlans);
        string Plan(BoostProfile p) => BoostEngine.PlanFor(p, plans) is { } g ? plans.First(x => x.Guid == g).Name : L.Get("boost.planKept");
        PlanInfo = L.F("boost.planInfo", Plan(BoostProfile.Competitive), Plan(BoostProfile.Balanced), Plan(BoostProfile.Quiet));

        await RefreshAppsAsync();
        await Task.Run(BuildEvidence).ContinueWith(t => ApplyEvidence(t.Result), TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnGameProfileChanged(GameProfileVm game) => _boost.Update(s =>
    {
        if (game.ProfileIndex == 0) s.PerGame.Remove(game.Executable);
        else s.PerGame[game.Executable] = (BoostProfile)(game.ProfileIndex - 1);
    });

    private void RefreshState()
    {
        IsActive = _boost.IsActive;
        if (_boost.Active is { } a)
        {
            StateTitle = L.F("boost.activeTitle", L.Get("boost.profile." + a.Profile));
            StateDetail = L.F("boost.activeDetail", a.Game ?? L.Get("boost.manual"), a.Suspended.Count, a.At.LocalDateTime.ToString("t", L.Culture));
        }
        else
        {
            StateTitle = L.Get("boost.inactiveTitle");
            StateDetail = L.Get(_boost.Settings.AutoApply ? "boost.inactiveAuto" : "boost.inactiveManual");
        }
    }

    [RelayCommand]
    private async Task RefreshAppsAsync()
    {
        var running = await Task.Run(BoostEngine.RunningApps);
        var chosen = _boost.Settings.PauseApps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Apps.Clear();
        foreach (var a in running)
            Apps.Add(new RowVm(a.Name, a.Executable, a.Processes > 1 ? L.F("boost.processes", a.Processes) : "", Tone.Muted)
            { Selectable = true, IsChecked = chosen.Contains(a.Executable), Value = Shell.Size(a.MemoryBytes), Tag = a.Executable, Changed = OnAppToggled });
        // Escolhidos que não estão abertos agora continuam na lista (para poder desmarcar).
        foreach (var exe in chosen.Where(c => running.All(r => !r.Executable.Equals(c, StringComparison.OrdinalIgnoreCase))))
            Apps.Add(new RowVm(exe, L.Get("boost.notRunning"), "", Tone.Muted) { Selectable = true, IsChecked = true, Tag = exe, Changed = OnAppToggled });
    }

    private void OnAppToggled(RowVm row) => _boost.Update(s =>
    {
        string exe = (string)row.Tag!;
        s.PauseApps.RemoveAll(a => a.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (row.IsChecked) s.PauseApps.Add(exe);
    });

    [RelayCommand]
    private void Toggle()
    {
        if (_boost.IsActive)
        {
            Info(L.Get("boost.deactivated") + " " + _boost.Deactivate(L.Get("boost.restoredManual")));
            return;
        }
        var r = _boost.Activate((BoostProfile)DefaultProfile, null, null);
        Info(r.PlanUnavailable ? L.Get("boost.activatedNoPlan") : L.F("boost.activated", r.PlanChanged ? r.PlanName : L.Get("boost.planKept"), r.Paused));
    }

    private sealed record Evidence(List<(string Game, string Detail, string Meta, Tone Tone, string Status)> Comparison, string ThermalTitle, List<ThermalEvidence> Thermal);

    /// <summary>Antes/depois por jogo (média das sessões com e sem boost) e evidência térmica da última sessão.</summary>
    private Evidence BuildEvidence()
    {
        var comparison = new List<(string, string, string, Tone, string)>();
        var thermal = new List<ThermalEvidence>();
        string thermalTitle = "";
        try
        {
            var sessions = _sessions.List(limit: 500);
            foreach (var game in sessions.GroupBy(s => s.Executable, StringComparer.OrdinalIgnoreCase))
            {
                var with = game.Where(s => s.Boost is not null and not "Mixed" && s.AverageFps is > 0).ToList();
                var without = game.Where(s => s.Boost is null && s.AverageFps is not null).ToList();
                if (with.Count == 0 || without.Count == 0) continue;
                double fpsA = without.Average(s => s.AverageFps!.Value), fpsB = with.Average(s => s.AverageFps!.Value);
                double? lowA = without.Average(s => s.OnePercentLowFps);
                double? lowB = with.Average(s => s.OnePercentLowFps);
                double delta = (fpsB - fpsA) / fpsA * 100;
                var tone = delta >= 3 ? Tone.Good : delta <= -3 ? Tone.Critical : Tone.Neutral;
                comparison.Add((game.First().Name,
                    L.F("boost.compare.detail", fpsA.ToString("0", L.Culture), fpsB.ToString("0", L.Culture), lowA?.ToString("0", L.Culture) ?? "—", lowB?.ToString("0", L.Culture) ?? "—"),
                    L.F("boost.compare.meta", without.Count, with.Count), tone, (delta >= 0 ? "+" : "") + delta.ToString("0.0", L.Culture) + "%"));
            }

            var last = sessions.FirstOrDefault();
            if (last is not null)
            {
                var points = _sessions.Points(last.Id);
                thermalTitle = L.F("boost.thermal.session", last.Name, last.Start.LocalDateTime.ToString("g", L.Culture));
                thermal.Add(ThermalAnalyzer.Analyze("CPU", points.Select(p => new ThermalSample(p.Second, p.CpuLoad, p.CpuTemp, p.CpuClock)).ToList(), ThermalAnalyzer.CpuHotLimit));
                thermal.Add(ThermalAnalyzer.Analyze("GPU", points.Select(p => new ThermalSample(p.Second, p.GpuLoad, p.GpuTemp, p.GpuClock)).ToList(), ThermalAnalyzer.GpuHotLimit));
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao montar a comparação do Game Boost.");
        }
        return new Evidence(comparison, thermalTitle, thermal);
    }

    private void ApplyEvidence(Evidence e)
    {
        Comparison.Clear();
        foreach (var c in e.Comparison) Comparison.Add(new RowVm(c.Game, c.Detail, c.Meta, c.Tone, c.Status));
        HasComparison = Comparison.Count > 0;
        Thermal.Clear();
        foreach (var t in e.Thermal)
        {
            var tone = t.Verdict switch { ThermalVerdict.Limited => Tone.Critical, ThermalVerdict.PossibleLimitation => Tone.Attention, ThermalVerdict.NoLimitation => Tone.Good, _ => Tone.Muted };
            string detail = t.Verdict switch
            {
                ThermalVerdict.NotEnoughData => L.F("boost.thermal.noData", t.HighLoadSeconds),
                ThermalVerdict.NoLimitation => L.F("boost.thermal.ok", t.HighLoadSeconds, Fmt.Mhz(t.ReferenceClockMhz ?? 0), t.PeakTemp is { } pk ? Fmt.Temperature(pk) : "—"),
                _ => L.F("boost.thermal.limited", t.ThrottledSeconds, t.HighLoadSeconds, Fmt.Mhz(t.ReferenceClockMhz ?? 0), Fmt.Mhz(t.ThrottledClockMhz ?? 0),
                    (t.ClockDropPercent ?? 0).ToString("0", L.Culture), Fmt.Temperature(t.TempWhenThrottled ?? 0)),
            };
            Thermal.Add(new RowVm(t.Component, detail, t.Verdict is ThermalVerdict.Limited or ThermalVerdict.PossibleLimitation ? L.Get("boost.thermal.tip") : "", tone,
                L.Get("boost.thermal.verdict." + t.Verdict)));
        }
        ThermalTitle = e.ThermalTitle;
        HasThermal = Thermal.Count > 0;
    }
}
