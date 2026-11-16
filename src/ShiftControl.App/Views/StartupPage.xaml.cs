using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class StartupPage : Page
{
    public StartupViewModel Vm { get; }

    public StartupPage()
    {
        Vm = App.Services.GetRequiredService<StartupViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await Vm.LoadAsync();
    }
}
