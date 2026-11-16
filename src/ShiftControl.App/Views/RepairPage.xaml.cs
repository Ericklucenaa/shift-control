using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class RepairPage : Page
{
    public RepairViewModel Vm { get; }

    public RepairPage()
    {
        Vm = App.Services.GetRequiredService<RepairViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (!Vm.ErrorsLoaded) await Vm.LoadErrorsAsync();
        };
    }

    /// <summary>Acompanha o fim da saída enquanto a ferramenta roda.</summary>
    private void OnOutputChanged(object sender, TextChangedEventArgs e)
    {
        OutputBox.SelectionStart = OutputBox.Text.Length;
        if (OutputBox.FindDescendant<ScrollViewer>() is { } sv) sv.ChangeView(null, sv.ScrollableHeight, null, true);
    }
}

internal static class VisualTreeExtensions
{
    public static T? FindDescendant<T>(this Microsoft.UI.Xaml.DependencyObject root) where T : Microsoft.UI.Xaml.DependencyObject
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (child.FindDescendant<T>() is { } found) return found;
        }
        return null;
    }
}
