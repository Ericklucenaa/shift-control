using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ShiftControl.App.Controls;

/// <summary>
/// Gráfico de linha leve para séries curtas (até alguns milhares de pontos), desenhado com formas XAML.
/// Sem dependências externas: cada atualização reaproveita as mesmas formas, só trocando os pontos.
/// Valores NaN interrompem a linha (métrica indisponível naquele instante) em vez de virar zero.
/// </summary>
public sealed partial class LineChart : Grid
{
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly List<Polyline> _primary = [];
    private readonly List<Polygon> _areas = [];
    private readonly List<Polyline> _secondary = [];
    private readonly List<Polygon> _bands = [];
    private readonly Line[] _grid = [new(), new(), new()];
    private readonly Line _hoverLine = new() { StrokeThickness = 1, Visibility = Visibility.Collapsed };
    private readonly Ellipse _hoverDot = new() { Width = 7, Height = 7, StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly Ellipse _hoverDot2 = new() { Width = 7, Height = 7, StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly TextBlock _maxLabel = new() { FontSize = 11, IsHitTestVisible = false };
    private readonly Border _tip = new()
    {
        Padding = new Thickness(8, 4, 8, 5),
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly TextBlock _tipText = new() { FontSize = 12 };

    private double _min, _max;
    private int _slots;

    public LineChart()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // recebe o ponteiro na área vazia
        foreach (var g in _grid)
        {
            g.StrokeThickness = 1;
            g.StrokeDashArray = [2, 3];
            _canvas.Children.Add(g);
        }
        _canvas.Children.Add(_hoverLine);
        Children.Add(_canvas);
        Children.Add(_maxLabel);
        _tip.Child = _tipText;
        Children.Add(_tip);
        _canvas.Children.Add(_hoverDot);
        _canvas.Children.Add(_hoverDot2);

        SizeChanged += (_, _) => Render();
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => HideHover();
        PointerCanceled += (_, _) => HideHover();
        ActualThemeChanged += (_, _) => ApplyBrushes();
        Loaded += (_, _) => { ApplyBrushes(); Render(); };
    }

    #region Propriedades

    public static readonly DependencyProperty ValuesProperty = Dp<double[]?>(nameof(Values), null);
    public static readonly DependencyProperty SecondaryValuesProperty = Dp<double[]?>(nameof(SecondaryValues), null);
    public static readonly DependencyProperty BandMinProperty = Dp<double[]?>(nameof(BandMin), null);
    public static readonly DependencyProperty BandMaxProperty = Dp<double[]?>(nameof(BandMax), null);
    public static readonly DependencyProperty TimesProperty = Dp<DateTimeOffset[]?>(nameof(Times), null);
    public static readonly DependencyProperty SlotsProperty = Dp(nameof(Slots), 0);
    public static readonly DependencyProperty MinimumProperty = Dp(nameof(Minimum), double.NaN);
    public static readonly DependencyProperty MaximumProperty = Dp(nameof(Maximum), double.NaN);
    public static readonly DependencyProperty SecondsPerSlotProperty = Dp(nameof(SecondsPerSlot), 1.0);
    public static readonly DependencyProperty BinaryScaleProperty = Dp(nameof(BinaryScale), false);
    public bool BinaryScale { get => (bool)GetValue(BinaryScaleProperty); set => SetValue(BinaryScaleProperty, value); }
    public static readonly DependencyProperty FormatterProperty = Dp<Func<double, string>?>(nameof(Formatter), null);
    public static readonly DependencyProperty PrimaryLabelProperty = Dp<string?>(nameof(PrimaryLabel), null);
    public static readonly DependencyProperty SecondaryLabelProperty = Dp<string?>(nameof(SecondaryLabel), null);
    public static readonly DependencyProperty StrokeProperty = Dp<Brush?>(nameof(Stroke), null);
    public static readonly DependencyProperty AreaFillProperty = Dp<Brush?>(nameof(AreaFill), null);
    public static readonly DependencyProperty SecondaryStrokeProperty = Dp<Brush?>(nameof(SecondaryStroke), null);
    public static readonly DependencyProperty GridStrokeProperty = Dp<Brush?>(nameof(GridStroke), null);
    public static readonly DependencyProperty LabelForegroundProperty = Dp<Brush?>(nameof(LabelForeground), null);
    public static readonly DependencyProperty TooltipBackgroundProperty = Dp<Brush?>(nameof(TooltipBackground), null);
    public static readonly DependencyProperty TooltipBorderProperty = Dp<Brush?>(nameof(TooltipBorder), null);

    public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double[]? SecondaryValues { get => (double[]?)GetValue(SecondaryValuesProperty); set => SetValue(SecondaryValuesProperty, value); }
    public double[]? BandMin { get => (double[]?)GetValue(BandMinProperty); set => SetValue(BandMinProperty, value); }
    public double[]? BandMax { get => (double[]?)GetValue(BandMaxProperty); set => SetValue(BandMaxProperty, value); }
    public DateTimeOffset[]? Times { get => (DateTimeOffset[]?)GetValue(TimesProperty); set => SetValue(TimesProperty, value); }
    /// <summary>Quantidade de posições no eixo X; séries mais curtas são alinhadas à direita (o "agora").</summary>
    public int Slots { get => (int)GetValue(SlotsProperty); set => SetValue(SlotsProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double SecondsPerSlot { get => (double)GetValue(SecondsPerSlotProperty); set => SetValue(SecondsPerSlotProperty, value); }
    public Func<double, string>? Formatter { get => (Func<double, string>?)GetValue(FormatterProperty); set => SetValue(FormatterProperty, value); }
    public string? PrimaryLabel { get => (string?)GetValue(PrimaryLabelProperty); set => SetValue(PrimaryLabelProperty, value); }
    public string? SecondaryLabel { get => (string?)GetValue(SecondaryLabelProperty); set => SetValue(SecondaryLabelProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? AreaFill { get => (Brush?)GetValue(AreaFillProperty); set => SetValue(AreaFillProperty, value); }
    public Brush? SecondaryStroke { get => (Brush?)GetValue(SecondaryStrokeProperty); set => SetValue(SecondaryStrokeProperty, value); }
    public Brush? GridStroke { get => (Brush?)GetValue(GridStrokeProperty); set => SetValue(GridStrokeProperty, value); }
    public Brush? LabelForeground { get => (Brush?)GetValue(LabelForegroundProperty); set => SetValue(LabelForegroundProperty, value); }
    public Brush? TooltipBackground { get => (Brush?)GetValue(TooltipBackgroundProperty); set => SetValue(TooltipBackgroundProperty, value); }
    public Brush? TooltipBorder { get => (Brush?)GetValue(TooltipBorderProperty); set => SetValue(TooltipBorderProperty, value); }

    private static DependencyProperty Dp<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(LineChart), new PropertyMetadata(fallback, OnChanged));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (LineChart)d;
        if (e.Property == StrokeProperty || e.Property == AreaFillProperty || e.Property == SecondaryStrokeProperty
            || e.Property == GridStrokeProperty || e.Property == LabelForegroundProperty
            || e.Property == TooltipBackgroundProperty || e.Property == TooltipBorderProperty)
            chart.ApplyBrushes();
        else
            chart.Render();
    }

    #endregion

    private void ApplyBrushes()
    {
        foreach (var p in _primary) p.Stroke = Stroke;
        foreach (var a in _areas) a.Fill = AreaFill;
        foreach (var s in _secondary) s.Stroke = SecondaryStroke;
        foreach (var b in _bands) b.Fill = AreaFill;
        foreach (var g in _grid) g.Stroke = GridStroke;
        _hoverLine.Stroke = GridStroke is null ? LabelForeground : LabelForeground;
        _hoverLine.Opacity = 0.5;
        _hoverDot.Stroke = Stroke;
        _hoverDot.Fill = TooltipBackground;
        _hoverDot2.Stroke = SecondaryStroke;
        _hoverDot2.Fill = TooltipBackground;
        _maxLabel.Foreground = LabelForeground;
        _tip.Background = TooltipBackground;
        _tip.BorderBrush = TooltipBorder;
    }

    private void Render()
    {
        double w = ActualWidth, h = ActualHeight;
        var values = Values ?? [];
        var second = SecondaryValues ?? [];
        var bandMin = BandMin ?? [];
        var bandMax = BandMax ?? [];
        _slots = Math.Max(Slots, Math.Max(Math.Max(values.Length, second.Length), bandMax.Length));

        if (w <= 0 || h <= 0 || _slots < 2)
        {
            HideFrom(_primary, 0); HideFrom(_areas, 0); HideFrom(_secondary, 0); HideFrom(_bands, 0);
            _maxLabel.Text = "";
            return;
        }

        // Escala: mínimo 0 (todas as métricas atuais são não negativas), máximo "redondo".
        double dataMax = double.NegativeInfinity, dataMin = double.PositiveInfinity;
        foreach (var arr in new[] { values, second, bandMax, bandMin })
            foreach (var v in arr)
                if (double.IsFinite(v)) { dataMax = Math.Max(dataMax, v); dataMin = Math.Min(dataMin, v); }

        _min = double.IsNaN(Minimum) ? Math.Min(0, double.IsFinite(dataMin) ? dataMin : 0) : Minimum;
        double top0 = double.IsFinite(dataMax) ? dataMax : 1;
        _max = double.IsNaN(Maximum) ? (BinaryScale ? NiceBinaryCeiling(top0) : NiceCeiling(top0)) : Maximum;
        if (_max <= _min) _max = _min + 1;

        const double top = 18; // espaço para o rótulo do topo
        double plotH = Math.Max(1, h - top - 1);

        for (int i = 0; i < _grid.Length; i++)
        {
            double y = top + plotH * i / (_grid.Length - 1);
            var g = _grid[i];
            g.X1 = 0; g.X2 = w; g.Y1 = y; g.Y2 = y;
            g.StrokeDashArray = i == _grid.Length - 1 ? null : [2, 3];
        }
        _maxLabel.Text = Formatter?.Invoke(_max) ?? _max.ToString("0.##");

        Point Map(int slot, double v) => new(
            _slots <= 1 ? 0 : slot * w / (_slots - 1),
            top + plotH - Math.Clamp((v - _min) / (_max - _min), 0, 1) * plotH);

        // Faixa mín–máx (histórico agregado)
        int bandIndex = 0;
        if (bandMin.Length == bandMax.Length && bandMax.Length > 1)
        {
            int offset = _slots - bandMax.Length;
            foreach (var (start, end) in Runs(bandMax))
            {
                var poly = Pooled(_bands, bandIndex++, () => new Polygon { Fill = AreaFill }, 0);
                var pts = new PointCollection();
                for (int i = start; i <= end; i++) pts.Add(Map(offset + i, bandMax[i]));
                for (int i = end; i >= start; i--) pts.Add(Map(offset + i, double.IsFinite(bandMin[i]) ? bandMin[i] : bandMax[i]));
                poly.Points = pts;
            }
        }
        HideFrom(_bands, bandIndex);

        // Série principal + área
        int lineIndex = 0, areaIndex = 0;
        bool showArea = bandIndex == 0;
        {
            int offset = _slots - values.Length;
            foreach (var (start, end) in Runs(values))
            {
                var line = Pooled(_primary, lineIndex++, () => new Polyline
                {
                    Stroke = Stroke, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round,
                }, -1);
                var pts = new PointCollection();
                for (int i = start; i <= end; i++) pts.Add(Map(offset + i, values[i]));
                line.Points = pts;

                if (showArea)
                {
                    var area = Pooled(_areas, areaIndex++, () => new Polygon { Fill = AreaFill }, 0);
                    var apts = new PointCollection();
                    foreach (var p in pts) apts.Add(p);
                    apts.Add(new Point(pts[^1].X, top + plotH));
                    apts.Add(new Point(pts[0].X, top + plotH));
                    area.Points = apts;
                }
            }
        }
        HideFrom(_primary, lineIndex);
        HideFrom(_areas, areaIndex);

        int secondIndex = 0;
        {
            int offset = _slots - second.Length;
            foreach (var (start, end) in Runs(second))
            {
                var line = Pooled(_secondary, secondIndex++, () => new Polyline
                {
                    Stroke = SecondaryStroke, StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
                }, -1);
                var pts = new PointCollection();
                for (int i = start; i <= end; i++) pts.Add(Map(offset + i, second[i]));
                line.Points = pts;
            }
        }
        HideFrom(_secondary, secondIndex);

        if (_hoverLine.Visibility == Visibility.Visible && _lastPointer is { } p0) UpdateHover(p0);
    }

    /// <summary>Trechos contínuos de valores finitos (NaN separa trechos). Trechos de 1 ponto viram 2 iguais.</summary>
    private static IEnumerable<(int Start, int End)> Runs(double[] values)
    {
        int start = -1;
        for (int i = 0; i <= values.Length; i++)
        {
            bool ok = i < values.Length && double.IsFinite(values[i]);
            if (ok && start < 0) start = i;
            else if (!ok && start >= 0)
            {
                yield return (start, i - 1);
                start = -1;
            }
        }
    }

    private T Pooled<T>(List<T> pool, int index, Func<T> create, int zIndexHint) where T : Shape
    {
        if (index < pool.Count)
        {
            pool[index].Visibility = Visibility.Visible;
            return pool[index];
        }
        var shape = create();
        pool.Add(shape);
        // Áreas e faixas ficam atrás das linhas; linhas logo abaixo dos marcadores de hover.
        if (zIndexHint == 0) _canvas.Children.Insert(_grid.Length, shape);
        else _canvas.Children.Insert(_canvas.Children.IndexOf(_hoverLine), shape);
        return shape;
    }

    private static void HideFrom<T>(List<T> pool, int index) where T : UIElement
    {
        for (int i = index; i < pool.Count; i++) pool[i].Visibility = Visibility.Collapsed;
    }

    private static double NiceBinaryCeiling(double v) => Core.Presentation.NiceBinaryCeiling(v);
    private static double NiceCeiling(double v) => Core.Presentation.NiceCeiling(v);

    #region Hover

    private Point? _lastPointer;

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        _lastPointer = p;
        UpdateHover(p);
    }

    private void UpdateHover(Point p)
    {
        var values = Values ?? [];
        if (_slots < 2 || values.Length == 0 || ActualWidth <= 0) { HideHover(); return; }

        double w = ActualWidth, h = ActualHeight;
        int slot = (int)Math.Round(Math.Clamp(p.X / w, 0, 1) * (_slots - 1));
        int i = slot - (_slots - values.Length);
        if (i < 0 || i >= values.Length || !double.IsFinite(values[i])) { HideHover(); return; }

        const double top = 18;
        double plotH = Math.Max(1, h - top - 1);
        double x = slot * w / (_slots - 1);
        double Y(double v) => top + plotH - Math.Clamp((v - _min) / (_max - _min), 0, 1) * plotH;

        _hoverLine.X1 = _hoverLine.X2 = x;
        _hoverLine.Y1 = top;
        _hoverLine.Y2 = top + plotH;
        _hoverLine.Visibility = Visibility.Visible;
        Canvas.SetLeft(_hoverDot, x - 3.5);
        Canvas.SetTop(_hoverDot, Y(values[i]) - 3.5);
        _hoverDot.Visibility = Visibility.Visible;

        var fmt = Formatter ?? (v => v.ToString("0.##"));
        string text = (PrimaryLabel is { } pl ? pl + "  " : "") + fmt(values[i]);

        var second = SecondaryValues ?? [];
        int j = slot - (_slots - second.Length);
        if (j >= 0 && j < second.Length && double.IsFinite(second[j]))
        {
            Canvas.SetLeft(_hoverDot2, x - 3.5);
            Canvas.SetTop(_hoverDot2, Y(second[j]) - 3.5);
            _hoverDot2.Visibility = Visibility.Visible;
            text += "\n" + (SecondaryLabel is { } sl ? sl + "  " : "") + fmt(second[j]);
        }
        else _hoverDot2.Visibility = Visibility.Collapsed;

        var bandMin = BandMin ?? [];
        var bandMax = BandMax ?? [];
        if (i < bandMin.Length && i < bandMax.Length && double.IsFinite(bandMin[i]) && double.IsFinite(bandMax[i]))
            text += "\n" + Services.L.F("chart.range", fmt(bandMin[i]), fmt(bandMax[i]));

        var times = Times;
        if (times is not null && i < times.Length)
            text += "\n" + times[i].ToLocalTime().ToString(times.Length > 0 && (times[^1] - times[0]).TotalHours > 20 ? "g" : "T", Services.L.Culture);
        else
        {
            int ago = (int)Math.Round((_slots - 1 - slot) * SecondsPerSlot);
            text += "\n" + (ago <= 0 ? Services.L.Get("chart.now") : Services.L.F("chart.secondsAgo", ago));
        }

        _tipText.Text = text;
        _tipText.Foreground = LabelForeground;
        _tip.Visibility = Visibility.Visible;
        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tipW = _tip.DesiredSize.Width;
        double left = x + 10 + tipW > w ? x - 10 - tipW : x + 10;
        _tip.Margin = new Thickness(Math.Max(0, left), top, 0, 0);
    }

    private void HideHover()
    {
        _lastPointer = null;
        _hoverLine.Visibility = Visibility.Collapsed;
        _hoverDot.Visibility = Visibility.Collapsed;
        _hoverDot2.Visibility = Visibility.Collapsed;
        _tip.Visibility = Visibility.Collapsed;
    }

    #endregion
}
