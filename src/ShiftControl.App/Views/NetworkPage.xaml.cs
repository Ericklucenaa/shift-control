using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class NetworkPage : Page
{
    public NetworkViewModel Vm { get; }

    public NetworkPage()
    {
        Vm = App.Services.GetRequiredService<NetworkViewModel>();
        InitializeComponent();
        Loaded += (_, _) => Vm.LoadHistory();
    }
}
