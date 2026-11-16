using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class DisksPage : Page
{
    public DisksViewModel Vm { get; }

    public DisksPage()
    {
        Vm = App.Services.GetRequiredService<DisksViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (!Vm.Loaded) await Vm.LoadAsync();
        };
    }
}
