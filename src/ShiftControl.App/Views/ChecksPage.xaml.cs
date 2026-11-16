using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class ChecksPage : Page
{
    private bool _ran;

    public ChecksViewModel Vm { get; }

    public ChecksPage()
    {
        Vm = App.Services.GetRequiredService<ChecksViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_ran) return;
            _ran = true;
            await Vm.RunAsync();
        };
    }

    private void OnFix(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CheckItemVm item }) Vm.OpenFixCommand.Execute(item);
    }
}
