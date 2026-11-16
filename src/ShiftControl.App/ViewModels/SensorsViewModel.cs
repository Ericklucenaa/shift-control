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
            Groups.Add(new SensorGroup("favorites", L.Get("sensors.favorites"), "", favorites, [], expanded: true, expandable: false, OnGroupToggled));

        // Componentes abrem recolhidos, só com os principais; a busca abre os grupos para mostrar o que bateu.
        bool searching = q.Length > 0;
        foreach (var g in visible.GroupBy(r => r.HardwareId).OrderBy(g => g.First().Category).ThenBy(g => g.First().Hardware))
        {
            var first = g.First();
            // Dentro do tipo, mantém a ordem em que o hardware lista os sensores (Core #1, #2… #10).
            var rows = g.OrderBy(r => r.Kind).ThenBy(r => r.Order).ToList();
            var all = _rows.Values.Where(r => r.HardwareId == first.HardwareId).OrderBy(r => r.Order).ToList();
            var highlights = SensorHighlights.Pick(first.Category, all, r => r.Kind, r => r.Name);
            Groups.Add(new SensorGroup(first.HardwareId, first.Hardware, L.Get($"category.{first.Category}"), rows, highlights,
                expanded: searching || _expanded.Contains(first.HardwareId), expandable: !searching, OnGroupToggled));
        }
        IsEmpty = !IsLoading && Groups.Count == 0;
        OnPropertyChanged(nameof(ExpandAllText));
    }

    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    private void OnGroupToggled(SensorGroup group)
    {
        if (group.IsExpanded) _expanded.Add(group.Key);
        else _expanded.Remove(group.Key);
        OnPropertyChanged(nameof(ExpandAllText));
    }

    private bool AnyCollapsed => Groups.Any(g => g.Expandable && !g.IsExpanded);

    public string ExpandAllText => L.Get(AnyCollapsed ? "sensors.expandAll" : "sensors.collapseAll");

    [RelayCommand]
    private void ToggleAll()
    {
        bool expand = AnyCollapsed;
        foreach (var g in Groups.Where(g => g.Expandable)) g.SetExpanded(expand);
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

/// <summary>
/// Um componente da lista. Recolhido, a coleção fica vazia e o cabeçalho mostra só os sensores principais
/// (<see cref="Highlights"/>, ao vivo); expandido, contém todas as linhas.
/// </summary>
public sealed partial class SensorGroup : ObservableCollection<SensorRow>
{
    private readonly IReadOnlyList<SensorRow> _rows;
    private readonly Action<SensorGroup> _toggled;

    public SensorGroup(string key, string title, string category, IReadOnlyList<SensorRow> rows, IReadOnlyList<SensorRow> highlights,
        bool expanded, bool expandable, Action<SensorGroup> toggled) : base(expanded ? rows : [])
    {
        Key = key;
        Title = title;
        Category = category;
        _rows = rows;
        Highlights = highlights;
        IsExpanded = expanded;
        Expandable = expandable;
        _toggled = toggled;
    }

    public string Key { get; }
    public string Title { get; }
    public string Category { get; }
    public bool Expandable { get; }
    public bool IsExpanded { get; private set; }
    public IReadOnlyList<SensorRow> Highlights { get; }
    public string CountText => _rows.Count.ToString(L.Culture);
    public string ChevronGlyph => IsExpanded ? "" : "";
    public string ToggleTip => L.Get(IsExpanded ? "sensors.collapse" : "sensors.expand");
    public bool ShowHighlights => !IsExpanded && Highlights.Count > 0;

    public void SetExpanded(bool expanded)
    {
        if (!Expandable || expanded == IsExpanded) return;
        IsExpanded = expanded;
        if (expanded) foreach (var r in _rows) Add(r);
        else Clear();
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ChevronGlyph)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ToggleTip)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ShowHighlights)));
        _toggled(this);
    }

    [RelayCommand]
    private void Toggle() => SetExpanded(!IsExpanded);
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
        // Rótulo da linha recolhida: "Taxa · Taxa" ou "Dados" não dizem nada; leitura/gravação/download/upload/usada dizem.
        ShortLabel = s.Name switch
        {
            "Read Rate" => L.Get("sensors.short.read"),
            "Write Rate" => L.Get("sensors.short.write"),
            "Download Speed" => L.Get("sensors.short.download"),
            "Upload Speed" => L.Get("sensors.short.upload"),
            "Memory Used" or "Used Memory" or "GPU Memory Used" => L.Get("sensors.short.used"),
            _ => TypeText,
        };
    }

    public string ShortLabel { get; }

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
