using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class CleanupPage : Page
{
    public CleanupViewModel Vm { get; }

    public CleanupPage()
    {
        Vm = App.Services.GetRequiredService<CleanupViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (!Vm.Analyzed && !Vm.Busy) await Vm.AnalyzeAsync();
        };
    }

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        string message = L.F("cleanup.confirm", Vm.SelectedSize) + (Vm.RecycleBinSelected ? "\n\n" + L.Get("cleanup.confirmRecycle") : "");
        if (await UiHelpers.ConfirmAsync(this, L.Get("cleanup.confirmTitle"), message, L.Get("cleanup.clean"), destructive: true))
            await Vm.CleanAsync();
    }

    private async void OnScan(object sender, RoutedEventArgs e) => await Vm.ScanAsync();

    private async void OnPick(object sender, RoutedEventArgs e)
    {
        if (await UiHelpers.PickFolderAsync() is { } folder) await Vm.ScanAsync(folder);
    }

    private async void OnPathKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await Vm.ScanAsync(((TextBox)sender).Text);
    }
}
