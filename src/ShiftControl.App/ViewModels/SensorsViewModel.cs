using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShiftControl.App.Services;
using ShiftControl.Core;
using ShiftControl.Monitoring;

namespace ShiftControl.App.ViewModels;

public sealed partial class SensorsViewModel : ObservableObject
{
    private readonly MonitoringEngine _engine;
    private readonly SettingsService _settings;
    private readonly Dictionary<string, SensorRow> _rows = new(StringComparer.Ordinal);
    private string _structureKey = "";
    private DateTimeOffset _statsSince = DateTimeOffset.Now;

    public SensorsViewModel(MonitoringEngine engine, SettingsService settings)
    {
        _engine = engine;
        _settings = settings;
    }

    public ObservableCollection<SensorGroup> Groups { get; } = [];

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial bool IsElevated { get; set; } = true;

    partial void OnQueryChanged(string value) => Rebuild();

    public void Update(SystemSnapshot s)
    {
        IsLoading = false;
        IsElevated = _engine.Info?.IsElevated ?? true;

        bool structureChanged = false;
        foreach (var sensor in s.Sensors)
        {
            if (!_rows.TryGetValue(sensor.Id, out var row))
            {
                row = new SensorRow(sensor, ToggleFavorite, _rows.Count) { IsFavorite = _settings.Current.FavoriteSensors.Contains(sensor.Id) };
                _rows[sensor.Id] = row;
                structureChanged = true;
            }
            row.Update(sensor, _engine.Stats.Get(sensor.Id));
        }

        // Sensores que sumiram (dispositivo removido) saem da lista.
        string key = string.Join('|', s.Sensors.Select(x => x.Id));
        if (key != _structureKey)
        {
            _structureKey = key;
            var current = s.Sensors.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _rows.Keys.Where(k => !current.Contains(k)).ToList()) _rows.Remove(gone);
            structureChanged = true;
        }

        if (structureChanged || Groups.Count == 0) Rebuild();
        Summary = L.F("sensors.summary", _rows.Count, _rows.Values.Select(r => r.HardwareId).Distinct().Count(),
            Fmt.When(_statsSince, DateTimeOffset.Now));
    }

    private void Rebuild()
    {
        var q = Query.Trim();
        var visible = _rows.Values
            .Where(r => q.Length == 0
                        || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || r.Hardware.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || r.TypeText.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Groups.Clear();
        var favorites = visible.Where(r => r.IsFavorite).ToList();
        if (favorites.Count > 0)
            Groups.Add(new SensorGroup(L.Get("sensors.favorites"), "", favorites));

        foreach (var g in visible.GroupBy(r => r.HardwareId).OrderBy(g => g.First().Category).ThenBy(g => g.First().Hardware))
        {
            var first = g.First();
            Groups.Add(new SensorGroup(first.Hardware, L.Get($"category.{first.Category}"),
                // Dentro do tipo, mantém a ordem em que o hardware lista os sensores (Core #1, #2… #10).
                g.OrderBy(r => r.Kind).ThenBy(r => r.Order)));
        }
        IsEmpty = !IsLoading && Groups.Count == 0;
    }

    private void ToggleFavorite(SensorRow row)
    {
        row.IsFavorite = !row.IsFavorite;
        _settings.Update(s =>
        {
            s.FavoriteSensors.Remove(row.Id);
            if (row.IsFavorite) s.FavoriteSensors.Add(row.Id);
        });
        Rebuild();
    }

    [RelayCommand]
    private void ResetStats()
    {
        _engine.Stats.Reset();
        _statsSince = DateTimeOffset.Now;
    }

    public string ExportCsv() => SensorExport.ToCsv(ExportRows());
    public string ExportJson() => SensorExport.ToJson(ExportRows(), DateTimeOffset.Now);

    private IReadOnlyList<SensorExportRow> ExportRows() =>
        _engine.Latest is { } s ? SensorExport.Rows(s.Sensors, _engine.Stats) : [];
}

public sealed partial class SensorGroup : ObservableCollection<SensorRow>
{
    public SensorGroup(string title, string category, IEnumerable<SensorRow> rows) : base(rows)
    {
        Title = title;
        Category = category;
    }

    public string Title { get; }
    public string Category { get; }
    public string CountText => Count.ToString(L.Culture);
}

public sealed partial class SensorRow : ObservableObject
{
    private readonly Action<SensorRow> _toggle;

    public SensorRow(SensorReading s, Action<SensorRow> toggle, int order)
    {
        _toggle = toggle;
        Order = order;
        Id = s.Id;
        Name = s.Name;
        Hardware = s.HardwareName;
        HardwareId = s.HardwareId;
        Category = s.Category;
        Kind = s.Kind;
        TypeText = L.Get($"kind.{s.Kind}");
    }

    public string Id { get; }
    public int Order { get; }
    public string Name { get; }
    public string Hardware { get; }
    public string HardwareId { get; }
    public HardwareCategory Category { get; }
    public SensorKind Kind { get; }
    public string TypeText { get; }

    [ObservableProperty] public partial bool IsFavorite { get; set; }
    public MetricText Value { get; } = new();
    [ObservableProperty] public partial string MinText { get; set; } = "—";
    [ObservableProperty] public partial string MaxText { get; set; } = "—";
    [ObservableProperty] public partial string AvgText { get; set; } = "—";

    public string FavoriteGlyph => IsFavorite ? "" : "";
    public string FavoriteTip => L.Get(IsFavorite ? "sensors.unfavorite" : "sensors.favorite");

    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(FavoriteTip));
    }

    public void Update(SensorReading s, RunningStats stats)
    {
        Value.Set(s.Value, v => Fmt.Sensor(Kind, v));
        MinText = stats.HasData ? Fmt.Sensor(Kind, stats.Min) : "—";
        MaxText = stats.HasData ? Fmt.Sensor(Kind, stats.Max) : "—";
        AvgText = stats.HasData ? Fmt.Sensor(Kind, stats.Mean) : "—";
    }

    [RelayCommand]
    private void ToggleFavorite() => _toggle(this);
}
