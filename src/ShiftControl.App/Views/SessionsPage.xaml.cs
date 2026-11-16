using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ShiftControl.App.Overlay;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class SessionsPage : Page
{
    private static SessionsPage? _current;
    private readonly OverlayService _overlay;

    public SessionsViewModel Vm { get; }

    public SessionsPage()
    {
        Vm = App.Services.GetRequiredService<SessionsViewModel>();
        _overlay = App.Services.GetRequiredService<OverlayService>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _current = this;
            _overlay.SessionSaved += OnSessionSaved;
            await Vm.LoadAsync();
        };
        Unloaded += (_, _) => _overlay.SessionSaved -= OnSessionSaved;
    }

    private async void OnSessionSaved(object? sender, ShiftControl.Overlay.GameSessionRecord e) => await Vm.LoadAsync();

    /// <summary>Verde = melhor, âmbar = pior, neutro = diferença menor que 1% ou sem dado.</summary>
    public static Brush DeltaBrush(bool? better)
    {
        var scope = (FrameworkElement?)_current ?? App.Window?.Content as FrameworkElement;
        string key = better switch { true => "GoodBrush", false => "AttentionBrush", _ => "TextSecondaryBrush" };
        return scope is null ? new SolidColorBrush(Microsoft.UI.Colors.Gray) : ViewHelpers.ThemeBrush(scope, key);
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.Get("sessions.deleteTitle"),
            Content = L.Get("sessions.deleteMessage"),
            PrimaryButtonText = L.Get("sessions.delete"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Vm.DeleteSelectedCommand.ExecuteAsync(null);
    }
}
