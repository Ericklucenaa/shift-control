using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace ShiftControl.App.Controls;

/// <summary>Barra horizontal fina de 0 a 100%.</summary>
public sealed partial class Meter : Grid
{
    private readonly Rectangle _track = new() { RadiusX = 2, RadiusY = 2 };
    private readonly Rectangle _fill = new() { RadiusX = 2, RadiusY = 2, HorizontalAlignment = HorizontalAlignment.Left };

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(Meter), new PropertyMetadata(0.0, (d, _) => ((Meter)d).Update()));
    public static readonly DependencyProperty FillBrushProperty =
        DependencyProperty.Register(nameof(FillBrush), typeof(Brush), typeof(Meter), new PropertyMetadata(null, (d, e) => ((Meter)d)._fill.Fill = (Brush?)e.NewValue));
    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(Meter), new PropertyMetadata(null, (d, e) => ((Meter)d)._track.Fill = (Brush?)e.NewValue));

    /// <summary>Percentual (0–100). NaN esconde o preenchimento.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush? FillBrush { get => (Brush?)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public Meter()
    {
        Height = 4;
        Children.Add(_track);
        Children.Add(_fill);
        SizeChanged += (_, _) => Update();
    }

    private void Update()
    {
        double v = Value;
        if (!double.IsFinite(v))
        {
            _fill.Visibility = Visibility.Collapsed;
            return;
        }
        _fill.Visibility = Visibility.Visible;
        _fill.Width = Math.Max(Height, Math.Clamp(v, 0, 100) / 100 * ActualWidth);
    }
}
