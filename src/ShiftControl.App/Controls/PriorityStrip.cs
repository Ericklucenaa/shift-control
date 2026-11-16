using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ShiftControl.App.Controls;

/// <summary>
/// Faixa horizontal alinhada à direita que mostra os filhos em ordem de prioridade (o primeiro é o mais importante)
/// até onde couberem. Os que não cabem ficam ocultos em vez de sobrepor o conteúdo vizinho.
/// </summary>
public sealed partial class PriorityStrip : Panel
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(PriorityStrip), new PropertyMetadata(16d, (d, _) => ((PriorityStrip)d).InvalidateMeasure()));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int _fitting;

    protected override Size MeasureOverride(Size available)
    {
        double width = 0, height = 0;
        _fitting = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, available.Height));
            double next = width + (_fitting > 0 ? Spacing : 0) + child.DesiredSize.Width;
            if (next > available.Width) break;
            width = next;
            height = Math.Max(height, child.DesiredSize.Height);
            _fitting++;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        // Os que cabem, da direita para a esquerda na ordem visual original.
        double used = 0;
        for (int i = 0; i < _fitting; i++) used += Children[i].DesiredSize.Width + (i > 0 ? Spacing : 0);
        double x = final.Width - used;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (i < _fitting)
            {
                child.Arrange(new Rect(x, (final.Height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
                x += child.DesiredSize.Width + Spacing;
            }
            else child.Arrange(new Rect(0, 0, 0, 0));
        }
        return final;
    }
}
