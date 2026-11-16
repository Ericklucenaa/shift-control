using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShiftControl.App.Services;
using ShiftControl.Core;

namespace ShiftControl.App.ViewModels;

public sealed record MetricOption(string Key, string Label, MetricUnit Unit, double? FixedMax)
{
    public override string ToString() => Label;
}

public sealed record PeriodOption(string Key, string Label, TimeSpan Span);

public sealed partial class HistoryViewModel : ObservableObject
{
    private const int MaxPoints = 720;

    private readonly IHistoryStore _history;
    private readonly SettingsService _settings;
    private readonly ILogger<HistoryViewModel> _log;
    private int _requestId;

    public HistoryViewModel(IHistoryStore history, SettingsService settings, ILogger<HistoryViewModel> log)
    {
        _history = history;
        _settings = settings;
        _log = log;
        Metrics = MetricKeys.All.Select(m => new MetricOption(m.Key, L.Get($"metric.{m.Key}"), m.Unit, m.FixedMax)).ToList();
        Periods =
        [
            new("60s", L.Get("period.60s"), TimeSpan.FromSeconds(60)),
            new("5m", L.Get("period.5m"), TimeSpan.FromMinutes(5)),
            new("30m", L.Get("period.30m"), TimeSpan.FromMinutes(30)),
            new("1h", L.Get("period.1h"), TimeSpan.FromHours(1)),
            new("24h", L.Get("period.24h"), TimeSpan.FromHours(24)),
            new("7d", L.Get("period.7d"), TimeSpan.FromDays(7)),
        ];
        SelectedMetric = Metrics[0];
        SelectedPeriod = Periods[1];
    }

    public IReadOnlyList<MetricOption> Metrics { get; }
    public IReadOnlyList<PeriodOption> Periods { get; }

    [ObservableProperty] public partial MetricOption SelectedMetric { get; set; }
    [ObservableProperty] public partial PeriodOption SelectedPeriod { get; set; }

    [ObservableProperty] public partial double[]? Avg { get; set; }
    [ObservableProperty] public partial double[]? Min { get; set; }
    [ObservableProperty] public partial double[]? Max { get; set; }
    [ObservableProperty] public partial DateTimeOffset[]? Times { get; set; }
    [ObservableProperty] public partial double ChartMaximum { get; set; } = double.NaN;
    [ObservableProperty] public partial Func<double, string>? Formatter { get; set; }
    [ObservableProperty] public partial bool BinaryScale { get; set; }

    [ObservableProperty] public partial string MinText { get; set; } = "—";
    [ObservableProperty] public partial string AvgText { get; set; } = "—";
    [ObservableProperty] public partial string MaxText { get; set; } = "—";
    [ObservableProperty] public partial string PointsText { get; set; } = "—";
    [ObservableProperty] public partial string StartText { get; set; } = "";
    [ObservableProperty] public partial string EndText { get; set; } = "";
    [ObservableProperty] public partial string CoverageText { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasError { get; set; }
    [ObservableProperty] public partial bool RecordingDisabled { get; set; }

    // ComboBox de página em cache pode devolver null ao ser descarregado: mantém a seleção anterior.
    partial void OnSelectedMetricChanged(MetricOption oldValue, MetricOption newValue)
    {
        if ((object?)newValue is null)
        {
            if ((object?)oldValue is not null) SelectedMetric = oldValue;
            return;
        }
        _ = RefreshAsync();
    }

    partial void OnSelectedPeriodChanged(PeriodOption oldValue, PeriodOption newValue)
    {
        if ((object?)newValue is null)
        {
            if ((object?)oldValue is not null) SelectedPeriod = oldValue;
            return;
        }
        _ = RefreshAsync();
    }

    /// <summary>Períodos curtos são atualizados periodicamente pela tela; longos, só ao trocar de filtro.</summary>
    public bool AutoRefresh => SelectedPeriod.Span <= TimeSpan.FromHours(1);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        // Os setters gerados chamam RefreshAsync durante a construção, antes de ambos os filtros existirem.
        if (SelectedMetric is null || SelectedPeriod is null) return;
        int request = ++_requestId;
        var metric = SelectedMetric;
        var period = SelectedPeriod;
        RecordingDisabled = !_settings.Current.RecordHistory;
        IsLoading = Avg is null;
        HasError = false;

        var to = DateTimeOffset.UtcNow;
        var from = to - period.Span;
        IReadOnlyList<HistoryPoint> points;
        try
        {
            points = await Task.Run(() => _history.Query(metric.Key, from, to, MaxPoints));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao consultar o histórico de {Metric}.", metric.Key);
            if (request != _requestId) return;
            HasError = true;
            IsLoading = false;
            return;
        }
        if (request != _requestId) return; // o usuário já trocou de filtro

        Formatter = v => Fmt.Metric(metric.Unit, v);
        BinaryScale = metric.Unit == MetricUnit.BytesPerSecond;
        ChartMaximum = metric.FixedMax ?? double.NaN;
        Times = points.Select(p => p.Time).ToArray();
        Avg = points.Select(p => p.Avg).ToArray();
        bool aggregated = period.Span > TimeSpan.FromMinutes(5);
        Min = aggregated ? points.Select(p => p.Min).ToArray() : null;
        Max = aggregated ? points.Select(p => p.Max).ToArray() : null;

        IsEmpty = points.Count == 0;
        IsLoading = false;
        if (IsEmpty)
        {
            MinText = AvgText = MaxText = "—";
            PointsText = "0";
            StartText = EndText = CoverageText = "";
            return;
        }

        MinText = Formatter(points.Min(p => p.Min));
        MaxText = Formatter(points.Max(p => p.Max));
        AvgText = Formatter(points.Average(p => p.Avg));
        PointsText = Fmt.Number(points.Count);
        string fmt = period.Span > TimeSpan.FromHours(20) ? "g" : "T";
        StartText = from.ToLocalTime().ToString(fmt, L.Culture);
        EndText = L.Get("chart.now");
        var firstPoint = points[0].Time;
        CoverageText = firstPoint - from > TimeSpan.FromTicks(period.Span.Ticks / 10)
            ? L.F("history.coverage", firstPoint.ToLocalTime().ToString("g", L.Culture))
            : "";
    }
}
