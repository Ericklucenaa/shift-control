using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShiftControl.App.Services;
using ShiftControl.Overlay;
using ShiftControl.Storage;

namespace ShiftControl.App.ViewModels;

public sealed record SessionItem(GameSessionRecord Record)
{
    public string Title => Record.Name;
    public string When => Fmt.When(Record.Start, DateTimeOffset.Now);
    public string Duration => Fmt.Duration(Record.Duration);
    public string AvgFps => Record.AverageFps is { } v ? Fmt.Number(v, 0) : "—";
    public string Low1 => Record.OnePercentLowFps is { } v ? Fmt.Number(v, 0) : "—";
    public string Stutters => Fmt.Number(Record.Stutters);
    public override string ToString() => $"{Record.Name} · {Fmt.When(Record.Start, DateTimeOffset.Now)}";
}

public sealed record ComparisonRow(string Label, string Current, string Baseline, string Delta, bool? Better);

public sealed partial class SessionsViewModel : ObservableObject
{
    private readonly GameSessionStore _store;
    private readonly ILogger<SessionsViewModel> _log;
    private bool _loading;

    public SessionsViewModel(GameSessionStore store, ILogger<SessionsViewModel> log)
    {
        _store = store;
        _log = log;
    }

    public ObservableCollection<Choice<string?>> Games { get; } = [];
    public ObservableCollection<SessionItem> Sessions { get; } = [];
    public ObservableCollection<SessionItem> Baselines { get; } = [];
    public ObservableCollection<ComparisonRow> Comparison { get; } = [];
    public ObservableCollection<MetricText> Details { get; } = [];

    [ObservableProperty] public partial Choice<string?>? SelectedGame { get; set; }
    [ObservableProperty] public partial SessionItem? SelectedSession { get; set; }
    [ObservableProperty] public partial SessionItem? Baseline { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial bool HasSelection { get; set; }
    [ObservableProperty] public partial bool HasBaseline { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string DetailTitle { get; set; } = "";
    [ObservableProperty] public partial string DetailSubtitle { get; set; } = "";
    [ObservableProperty] public partial double[]? FpsSeries { get; set; }
    [ObservableProperty] public partial double[]? BaselineSeries { get; set; }
    [ObservableProperty] public partial string FpsLegend { get; set; } = "";
    [ObservableProperty] public partial string BaselineLegend { get; set; } = "";

    public Func<double, string> FpsFormatter { get; } = v => Fmt.Number(v, 0) + " FPS";

    public async Task LoadAsync()
    {
        try
        {
            var games = await Task.Run(_store.Games);
            _loading = true;
            string? keep = SelectedGame?.Value;
            Games.Clear();
            Games.Add(new Choice<string?>(null, L.Get("sessions.allGames")));
            foreach (var g in games) Games.Add(new Choice<string?>(g, g));
            SelectedGame = Games.FirstOrDefault(g => g.Value == keep) ?? Games[0];
            _loading = false;
            await LoadSessionsAsync();
        }
        catch (Exception ex)
        {
            _loading = false;
            _log.LogError(ex, "Falha ao carregar as sessões.");
        }
    }

    partial void OnSelectedGameChanged(Choice<string?>? value)
    {
        if (!_loading) _ = LoadSessionsAsync();
    }

    private async Task LoadSessionsAsync()
    {
        string? game = SelectedGame?.Value;
        var list = await Task.Run(() => _store.List(game));
        long? keep = SelectedSession?.Record.Id;
        Sessions.Clear();
        foreach (var s in list) Sessions.Add(new SessionItem(s));
        IsEmpty = Sessions.Count == 0;
        SelectedSession = Sessions.FirstOrDefault(s => s.Record.Id == keep) ?? Sessions.FirstOrDefault();
    }

    partial void OnSelectedSessionChanged(SessionItem? value)
    {
        HasSelection = value is not null;
        Baselines.Clear();
        if (value is null)
        {
            Baseline = null;
            return;
        }
        // Comparação só entre sessões do mesmo jogo; por padrão, a anterior.
        foreach (var s in Sessions.Where(s => s != value && string.Equals(s.Record.Executable, value.Record.Executable, StringComparison.OrdinalIgnoreCase)))
            Baselines.Add(s);
        if (SelectedGame?.Value is null)
        {
            // Lista "todos os jogos" não traz as outras sessões deste jogo: busca direto.
            foreach (var s in _store.List(value.Record.Executable).Where(s => s.Id != value.Record.Id && Baselines.All(b => b.Record.Id != s.Id)))
                Baselines.Add(new SessionItem(s));
        }
        var previous = Baselines.Where(b => b.Record.Start < value.Record.Start).OrderByDescending(b => b.Record.Start).FirstOrDefault();
        Baseline = previous ?? Baselines.FirstOrDefault();
        _ = ShowDetailAsync();
    }

    partial void OnBaselineChanged(SessionItem? value) => _ = ShowDetailAsync();

    private async Task ShowDetailAsync()
    {
        var current = SelectedSession;
        var baseline = Baseline;
        HasBaseline = baseline is not null;
        if (current is null) return;

        var r = current.Record;
        DetailTitle = r.Name;
        DetailSubtitle = L.F("sessions.detailSubtitle", r.Executable, r.Start.ToLocalTime().ToString("g", L.Culture), Fmt.Duration(r.Duration),
            string.IsNullOrWhiteSpace(r.Runtime) ? "—" : r.Runtime);

        Details.Clear();
        void Add(string key, double? v, Func<double, string> f)
        {
            var m = new MetricText();
            m.Set(v is { } x ? Core.Reading.Of(x) : Core.Reading.Missing(Core.Unavailable.NotSupported), f);
            m.Hint = L.Get(key);
            Details.Add(m);
        }
        Add("sessions.m.avgFps", r.AverageFps, x => Fmt.Number(x, 0) + " FPS");
        Add("sessions.m.low1", r.OnePercentLowFps, x => Fmt.Number(x, 0) + " FPS");
        Add("sessions.m.low01", r.PointOnePercentLowFps, x => Fmt.Number(x, 0) + " FPS");
        Add("sessions.m.frametime", r.AverageFrametimeMs, x => Fmt.Number(x, 1) + " ms");
        Add("sessions.m.p99", r.P99FrametimeMs, x => Fmt.Number(x, 1) + " ms");
        Add("sessions.m.stutters", r.Stutters, x => Fmt.Number(x));
        Add("sessions.m.cpu", r.CpuLoadAvg, Fmt.Percent);
        Add("sessions.m.gpu", r.GpuLoadAvg, Fmt.Percent);
        Add("sessions.m.cpuTemp", r.CpuTempMax, Fmt.Temperature);
        Add("sessions.m.gpuTemp", r.GpuTempMax, Fmt.Temperature);
        Add("sessions.m.vram", r.VramMaxMb, Fmt.Megabytes);

        var points = await Task.Run(() => _store.Points(r.Id));
        FpsSeries = points.Select(p => p.Fps ?? double.NaN).ToArray();
        FpsLegend = L.F("sessions.legendCurrent", current.When);

        Comparison.Clear();
        Summary = "";
        if (baseline is not null)
        {
            var basePoints = await Task.Run(() => _store.Points(baseline.Record.Id));
            BaselineSeries = basePoints.Select(p => p.Fps ?? double.NaN).ToArray();
            BaselineLegend = L.F("sessions.legendBaseline", baseline.When);
            foreach (var c in SessionComparison.Compare(r, baseline.Record))
            {
                Func<double, string> f = c.Key is "p99" ? x => Fmt.Number(x, 1) + " ms"
                    : c.Key is "cpuLoad" or "gpuLoad" ? Fmt.Percent
                    : c.Key is "gpuTempMax" or "cpuTempMax" ? Fmt.Temperature
                    : x => Fmt.Number(x, 0);
                string delta = c.PercentChange is { } p ? (p >= 0 ? "+" : "") + Fmt.Number(p, 1) + "%" : "—";
                bool? better = c.PercentChange is { } d && Math.Abs(d) >= 1 ? (d > 0) == c.HigherIsBetter : null;
                Comparison.Add(new ComparisonRow(L.Get($"sessions.c.{c.Key}"),
                    c.Current is { } a ? f(a) : "—", c.Baseline is { } b ? f(b) : "—", delta, better));
            }
            var avg = SessionComparison.Compare(r, baseline.Record).First(c => c.Key == "avgFps");
            if (avg.PercentChange is { } pct)
                Summary = L.F(pct >= 0 ? "sessions.summaryUp" : "sessions.summaryDown", Fmt.Number(Math.Abs(pct), 1), baseline.When);
        }
        else
        {
            BaselineSeries = null;
            BaselineLegend = "";
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedSession is not { } s) return;
        await Task.Run(() => _store.Delete(s.Record.Id));
        await LoadAsync();
    }
}
