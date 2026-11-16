using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ShiftControl.App.Controls;
using ShiftControl.App.Services;
using ShiftControl.Overlay;

namespace ShiftControl.App.Overlay;

/// <summary>
/// O desenho do overlay. Construído em código (sem templates nem virtualização) para ser leve:
/// a cada atualização só os textos e as barras mudam. É o mesmo componente na janela do jogo e na prévia.
/// </summary>
public sealed partial class OverlayPanel : UserControl
{
    private sealed class Row
    {
        public required OverlayMetric Metric;
        public required FrameworkElement Root;
        public TextBlock? Value;
        public Meter? Bar;
        public LineChart? Graph;
        public BarStrip? Strip;
    }

    private readonly List<Row> _rows = [];
    private readonly Panel _host;
    private OverlayPreset _preset = OverlayPreset.Minimal();
    private SolidColorBrush _text = new(), _label = new(), _accent = new(), _track = new(), _dim = new();
    private bool _editMode;
    private readonly TextBlock _editHint = new() { FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };

    private readonly Border _frame = new() { CornerRadius = new CornerRadius(6) };

    public OverlayPanel()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        IsTabStop = false;
        _host = new StackPanel();
        _frame.Child = _host;
        Content = _frame;
    }

    public OverlayPreset Preset => _preset;

    /// <summary>Modo de edição: contorno tracejado e dica de arrastar.</summary>
    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            Build();
        }
    }

    public void Apply(OverlayPreset preset)
    {
        _preset = preset.Clone();
        Build();
    }

    private static Windows.UI.Color Color(string hex, byte alpha = 255)
    {
        var c = ThemeService.Parse(hex);
        return Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    private void Build()
    {
        var p = _preset;
        double scale = p.Scale;
        double font = p.FontSize * scale;

        _text = new SolidColorBrush(Color(p.TextColor));
        _label = new SolidColorBrush(Color(p.LabelColor));
        _accent = new SolidColorBrush(Color(p.AccentColor));
        _track = new SolidColorBrush(Color(p.TextColor, 0x26));
        _dim = new SolidColorBrush(Color(p.TextColor, 0x70));
        _frame.Background = new SolidColorBrush(Color(p.BackgroundColor, (byte)Math.Round(p.BackgroundOpacity * 255)));
        _frame.CornerRadius = new CornerRadius(p.CornerRadius * scale);
        _frame.Padding = new Thickness(10 * scale, 8 * scale, 10 * scale, 8 * scale);
        _frame.BorderThickness = new Thickness(_editMode ? 1 : 0);
        _frame.BorderBrush = _editMode ? _accent : null;

        _rows.Clear();
        var items = p.VisibleItems.ToList();
        bool horizontal = p.Orientation == OverlayOrientation.Horizontal;
        Panel container = horizontal
            ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 * scale }
            : new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 * scale };

        foreach (var item in items)
        {
            var row = BuildRow(item, font, scale, horizontal);
            _rows.Add(row);
            container.Children.Add(row.Root);
        }
        if (items.Count == 0)
            container.Children.Add(new TextBlock { Text = L.Get("overlay.empty"), Foreground = _dim, FontSize = font });

        var outer = new StackPanel();
        outer.Children.Add(container);
        if (_editMode)
        {
            _editHint.Text = L.Get("overlay.editHint");
            _editHint.Foreground = _accent;
            _editHint.FontSize = Math.Max(10, 11 * scale);
            outer.Children.Add(_editHint);
        }
        _frame.Child = outer;
    }

    private Row BuildRow(OverlayItem item, double font, double scale, bool horizontal)
    {
        string label = string.IsNullOrWhiteSpace(item.Label) ? OverlayComposer.DefaultLabel(item.Metric) : item.Label!;
        bool emphasis = item.Metric == OverlayMetric.Fps;
        var align = _preset.Alignment == OverlayAlignment.Right ? TextAlignment.Right : TextAlignment.Left;

        var labelBlock = new TextBlock
        {
            Text = horizontal ? label : label.ToUpper(L.Culture),
            FontSize = Math.Max(8, font * (horizontal ? 0.78 : 0.74)),
            FontWeight = FontWeights.SemiBold,
            CharacterSpacing = horizontal ? 0 : 60,
            Foreground = _label,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = align,
        };

        // Gráfico de frametime
        if (item.Metric == OverlayMetric.FrametimeGraph)
        {
            var chart = new LineChart
            {
                Height = 36 * scale,
                Width = horizontal ? 120 * scale : double.NaN,
                MinWidth = 140 * scale,
                Minimum = 0,
                Stroke = _accent,
                AreaFill = new SolidColorBrush(Color(_preset.AccentColor, 0x2A)),
                GridStroke = _track,
                LabelForeground = _dim,
                Formatter = v => Fmt.Number(v, 0) + " ms",
                IsHitTestVisible = false,
            };
            var stack = new StackPanel { Spacing = 2 * scale };
            if (!horizontal) stack.Children.Add(labelBlock);
            stack.Children.Add(chart);
            return new Row { Metric = item.Metric, Root = stack, Graph = chart };
        }

        // Carga por thread da CPU
        if (item.Metric == OverlayMetric.CpuCores)
        {
            var strip = new BarStrip { Height = 18 * scale, MinWidth = 120 * scale, BarBrush = _accent, TrackBrush = _track, ColumnSpacing = 2 };
            var stack = new StackPanel { Spacing = 2 * scale };
            stack.Children.Add(labelBlock);
            stack.Children.Add(strip);
            return new Row { Metric = item.Metric, Root = stack, Strip = strip };
        }

        var value = new TextBlock
        {
            FontSize = emphasis ? font * 1.55 : font,
            FontWeight = emphasis ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = emphasis ? _accent : _text,
            TextAlignment = horizontal ? TextAlignment.Left : TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = horizontal ? 0 : 64 * scale,
        };

        Meter? bar = null;
        if (_preset.ShowBars && HasBar(item.Metric))
            bar = new Meter { Height = Math.Max(2, 3 * scale), FillBrush = _accent, TrackBrush = _track, Margin = new Thickness(0, 2 * scale, 0, 0) };

        FrameworkElement root;
        if (horizontal)
        {
            var stack = new StackPanel { Spacing = 0, MinWidth = 44 * scale };
            stack.Children.Add(labelBlock);
            stack.Children.Add(value);
            if (bar is not null) stack.Children.Add(bar);
            root = stack;
        }
        else
        {
            var grid = new Grid { ColumnSpacing = 14 * scale, MinWidth = 160 * scale };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (_preset.Alignment == OverlayAlignment.Right)
            {
                Grid.SetColumn(labelBlock, 0);
                labelBlock.HorizontalAlignment = HorizontalAlignment.Right;
                value.TextAlignment = TextAlignment.Right;
            }
            Grid.SetColumn(value, 1);
            grid.Children.Add(labelBlock);
            grid.Children.Add(value);
            if (bar is not null)
            {
                Grid.SetRow(bar, 1);
                Grid.SetColumnSpan(bar, 2);
                grid.Children.Add(bar);
            }
            root = grid;
        }
        return new Row { Metric = item.Metric, Root = root, Value = value, Bar = bar };
    }

    private static bool HasBar(OverlayMetric m) =>
        m is OverlayMetric.CpuLoad or OverlayMetric.GpuLoad or OverlayMetric.Vram or OverlayMetric.Ram;

    /// <summary>Atualiza só os valores (sem recriar elementos).</summary>
    public void Update(OverlayFrame frame)
    {
        foreach (var row in _rows)
        {
            int i = (int)row.Metric;
            bool ok = frame.Available[i];
            if (row.Value is not null)
            {
                if (!string.Equals(row.Value.Text, frame.Text[i], StringComparison.Ordinal)) row.Value.Text = frame.Text[i];
                row.Value.Opacity = ok ? 1 : 0.5;
            }
            if (row.Bar is not null) row.Bar.Value = frame.Bar[i];
            if (row.Graph is not null)
            {
                row.Graph.Values = ok && frame.FrametimeCount > 1 ? frame.FrametimeGraph.AsSpan(0, frame.FrametimeCount).ToArray() : null;
                row.Graph.Slots = frame.FrametimeGraph.Length;
            }
            if (row.Strip is not null) row.Strip.Values = ok ? frame.ThreadLoads : [];
        }
    }
}
