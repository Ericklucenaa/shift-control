using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.Services;
using Microsoft.Windows.Storage.Pickers;

namespace ShiftControl.App.Views;

/// <summary>Diálogos de confirmação e seletores de pasta/arquivo (suporta execução como Administrador).</summary>
public static class UiHelpers
{
    public static async Task<bool> ConfirmAsync(FrameworkElement scope, string title, string message, string primary, bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = scope.XamlRoot,
            RequestedTheme = scope.ActualTheme,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
            PrimaryButtonText = primary,
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task<string?> PickFolderAsync()
    {
        var window = App.Window ?? throw new InvalidOperationException(L.Get("common.folderPickerUnavailable"));
        // Windows App SDK: owned asynchronous dialog, supported for elevated desktop apps too.
        // Only an actual cancellation returns null; failures propagate to the page's error message.
        var picker = new FolderPicker(window.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            CommitButtonText = L.Get("common.selectFolder"),
        };
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
