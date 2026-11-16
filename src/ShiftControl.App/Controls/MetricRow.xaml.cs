using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Controls;

/// <summary>Linha "rótulo ........ valor". Valores indisponíveis ficam esmaecidos, com ícone e explicação no tooltip.</summary>
public sealed partial class MetricRow : UserControl
{
    private static readonly MetricText Empty = new();

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(MetricRow), new PropertyMetadata(""));
    public static readonly DependencyProperty MetricProperty =
        DependencyProperty.Register(nameof(Metric), typeof(MetricText), typeof(MetricRow), new PropertyMetadata(Empty, OnMetricChanged));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public MetricText Metric { get => (MetricText)GetValue(MetricProperty); set => SetValue(MetricProperty, value); }

    public MetricRow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyAvailability();
        ActualThemeChanged += (_, _) => ApplyAvailability();
    }

    public Visibility HintVisibility(bool available) => available ? Visibility.Collapsed : Visibility.Visible;

    private static void OnMetricChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (MetricRow)d;
        if (e.OldValue is MetricText old) old.PropertyChanged -= row.OnMetricPropertyChanged;
        if (e.NewValue is MetricText now) now.PropertyChanged += row.OnMetricPropertyChanged;
        row.ApplyAvailability();
    }

    private void OnMetricPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MetricText.Available)) ApplyAvailability();
    }

    private void ApplyAvailability()
    {
        if (ValueText is null) return;
        // Opacidade em vez de outro pincel: funciona igual nos temas claro e escuro.
        ValueText.Opacity = Metric.Available ? 1 : 0.55;
        ValueText.FontWeight = Metric.Available ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    }
}
