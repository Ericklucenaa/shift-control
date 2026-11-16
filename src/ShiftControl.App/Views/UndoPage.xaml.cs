using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class UndoPage : Page
{
    public UndoViewModel Vm { get; }

    public UndoPage()
    {
        Vm = App.Services.GetRequiredService<UndoViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (Vm.Dirty) await Vm.LoadAsync();
        };
    }

    private async void OnRevert(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ChangeVm change }) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.F("undo.confirmTitle", change.Title),
            Content = new TextBlock { Text = L.Get("undo.confirm." + change.Entry.UndoKind), TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            PrimaryButtonText = L.Get("undo.revert"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Vm.RevertAsync(change);
    }
}
