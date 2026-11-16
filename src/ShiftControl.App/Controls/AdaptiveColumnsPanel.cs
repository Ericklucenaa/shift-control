using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ShiftControl.App.Controls;

/// <summary>
/// Distribui os filhos em colunas de largura igual. O número de colunas vem da largura disponível
/// e de <see cref="MinColumnWidth"/>; cada linha assume a altura do filho mais alto (cartões alinhados).
/// </summary>
public sealed partial class AdaptiveColumnsPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty =
        DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(AdaptiveColumnsPanel), new PropertyMetadata(280.0, Invalidate));
    public static readonly DependencyProperty MaxColumnsProperty =
        DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(AdaptiveColumnsPanel), new PropertyMetadata(4, Invalidate));
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(AdaptiveColumnsPanel), new PropertyMetadata(16.0, Invalidate));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private static void Invalidate(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((Panel)d).InvalidateMeasure();

    private int _columns = 1;
    private double _columnWidth;
    private readonly List<double> _rowHeights = [];

    protected override Size MeasureOverride(Size available)
    {
        var visible = Children.Where(c => c.Visibility == Visibility.Visible).ToList();
        double width = double.IsInfinity(available.Width) ? MinColumnWidth * Math.Max(1, MaxColumns) : available.Width;
        _columns = Core.Presentation.Columns(width, MinColumnWidth, Spacing, MaxColumns, visible.Count);
        _columnWidth = Math.Max(0, (width - Spacing * (_columns - 1)) / _columns);

        _rowHeights.Clear();
        for (int i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(_columnWidth, double.PositiveInfinity));
            int row = i / _columns;
            if (row == _rowHeights.Count) _rowHeights.Add(0);
            _rowHeights[row] = Math.Max(_rowHeights[row], visible[i].DesiredSize.Height);
        }
        foreach (var hidden in Children.Where(c => c.Visibility != Visibility.Visible)) hidden.Measure(new Size(0, 0));

        double height = _rowHeights.Sum() + Spacing * Math.Max(0, _rowHeights.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var visible = Children.Where(c => c.Visibility == Visibility.Visible).ToList();
        double y = 0;
        for (int i = 0; i < visible.Count; i++)
        {
            int row = i / _columns, col = i % _columns;
            if (col == 0 && row > 0) y += _rowHeights[row - 1] + Spacing;
            double rowHeight = row < _rowHeights.Count ? _rowHeights[row] : visible[i].DesiredSize.Height;
            visible[i].Arrange(new Rect(col * (_columnWidth + Spacing), y, _columnWidth, rowHeight));
        }
        return final;
    }
}
