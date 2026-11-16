using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.Services;

namespace ShiftControl.App.Controls;

/// <summary>Aviso discreto de que algumas ações da tela exigem administrador, com o botão para reabrir elevado.</summary>
public sealed partial class AdminNotice : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(AdminNotice), new PropertyMetadata("", (d, e) => ((AdminNotice)d)._bar.Message = (string)e.NewValue));

    private readonly InfoBar _bar;

    public AdminNotice()
    {
        var button = new Button { Content = L.Get("common.restartAdmin") };
        button.Click += (_, _) =>
        {
            if (SystemIntegration.RestartElevated()) App.ExitApp();
        };
        _bar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            IsClosable = false,
            IsOpen = !Hardware.Elevation.IsElevated,
            ActionButton = button,
        };
        Content = _bar;
        Visibility = Hardware.Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
