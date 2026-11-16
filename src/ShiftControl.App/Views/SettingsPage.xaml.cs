using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel Vm { get; }

    public SettingsPage()
    {
        Vm = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        Loaded += (_, _) => Vm.RefreshFacts();
    }

    public static InfoBarSeverity StatusSeverity(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (await UiHelpers.ConfirmAsync(this, L.Get("update.install"), L.Get("update.installConfirm"), L.Get("update.install")))
            await Vm.InstallUpdateAsync();
    }

    private async void OnClearHistory(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.Get("settings.clearHistoryTitle"),
            Content = L.Get("settings.clearHistoryMessage"),
            PrimaryButtonText = L.Get("settings.clearHistoryConfirm"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await Vm.ClearHistoryAsync();
    }
}
