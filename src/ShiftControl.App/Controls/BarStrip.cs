using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace ShiftControl.App.Controls;

/// <summary>Pequenas barras verticais lado a lado (0–100), usadas para a carga por thread da CPU.</summary>
public sealed partial class BarStrip : Grid
{
    private readonly List<(Rectangle Track, Rectangle Bar)> _bars = [];

    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(BarStrip), new PropertyMetadata(null, (d, _) => ((BarStrip)d).Update()));
    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(BarStrip), new PropertyMetadata(null, (d, _) => ((BarStrip)d).Restyle()));
    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(BarStrip), new PropertyMetadata(null, (d, _) => ((BarStrip)d).Restyle()));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush? BarBrush { get => (Brush?)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }
    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public BarStrip()
    {
        ColumnSpacing = 3;
        SizeChanged += (_, _) => Update();
    }

    private void Restyle()
    {
        foreach (var (track, bar) in _bars)
        {
            track.Fill = TrackBrush;
            bar.Fill = BarBrush;
        }
    }

    private void Update()
    {
        var values = Values ?? [];
        if (values.Count != _bars.Count)
        {
            Children.Clear();
            ColumnDefinitions.Clear();
            _bars.Clear();
            for (int i = 0; i < values.Count; i++)
            {
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var track = new Rectangle { Fill = TrackBrush, RadiusX = 1.5, RadiusY = 1.5 };
                var bar = new Rectangle { Fill = BarBrush, RadiusX = 1.5, RadiusY = 1.5, VerticalAlignment = VerticalAlignment.Bottom };
                SetColumn(track, i);
                SetColumn(bar, i);
                Children.Add(track);
                Children.Add(bar);
                _bars.Add((track, bar));
            }
        }

        double h = ActualHeight;
        for (int i = 0; i < values.Count; i++)
            _bars[i].Bar.Height = Math.Max(1.5, Math.Clamp(values[i], 0, 100) / 100 * h);
    }
}
