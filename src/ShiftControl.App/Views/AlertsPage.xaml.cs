using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class AlertsPage : Page
{
    public AlertsViewModel Vm { get; }

    public AlertsPage()
    {
        Vm = App.Services.GetRequiredService<AlertsViewModel>();
        Vm.Load();
        InitializeComponent();
        Loaded += (_, _) => Vm.LoadHistory();
    }
}
