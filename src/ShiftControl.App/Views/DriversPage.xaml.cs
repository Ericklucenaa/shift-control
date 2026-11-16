using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class DriversPage : Page
{
    public DriversViewModel Vm { get; }

    public DriversPage()
    {
        Vm = App.Services.GetRequiredService<DriversViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (!Vm.Loaded && !Vm.Busy) await Vm.LoadAsync();
        };
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (Vm.SelectedUpdates == 0) return;
        if (await UiHelpers.ConfirmAsync(this, L.Get("drivers.wuConfirmTitle"), L.F("drivers.wuConfirm", Vm.SelectedUpdates), L.Get("drivers.wuConfirmAction")))
            await Vm.InstallSelectedAsync();
    }

    private async void OnBackup(object sender, RoutedEventArgs e)
    {
        if (await UiHelpers.PickFolderAsync() is { } folder) await Vm.BackupAsync(folder);
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (await UiHelpers.PickFolderAsync() is not { } folder) return;
        if (await UiHelpers.ConfirmAsync(this, L.Get("drivers.restoreConfirmTitle"), L.F("drivers.restoreConfirm", folder), L.Get("drivers.backupRestore")))
            await Vm.RestoreAsync(folder);
    }
}
