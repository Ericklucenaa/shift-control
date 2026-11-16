using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ShiftControl.App.Services;
using ShiftControl.Core;

namespace ShiftControl.App.Controls;

/// <summary>Estilo e brilho líquido animados em camadas separadas: o conteúdo do botão nunca desaparece.</summary>
public static class ButtonEffects
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ButtonEffects),
        new PropertyMetadata(false, (d, e) => { if (d is Button b && (bool)e.NewValue) _ = new Effect(b); }));
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);
    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached("Role", typeof(string), typeof(ButtonEffects), new PropertyMetadata("Quiet"));
    public static string GetRole(DependencyObject o) => (string)o.GetValue(RoleProperty);
    public static void SetRole(DependencyObject o, string value) => o.SetValue(RoleProperty, value);

    private sealed class Effect
    {
        private readonly Button _button;
        private ThemeService? _theme;
        private FrameworkElement? _liquid, _sheen;
        public Effect(Button button)
        {
            _button = button;
            button.Loaded += Loaded;
            button.Unloaded += (_, _) => { if (_theme is not null) _theme.AppearanceChanged -= Changed; };
            button.ActualThemeChanged += (_, _) => Update();
            button.PointerEntered += Entered;
        }
        private void Loaded(object sender, RoutedEventArgs e)
        {
            _theme = App.Services.GetRequiredService<ThemeService>();
            _theme.AppearanceChanged -= Changed;
            _theme.AppearanceChanged += Changed;
            _button.ApplyTemplate();
            _liquid = Find(_button, "LiquidSurface");
            _sheen = Find(_button, "LiquidSheen");
            Update();
        }
        private void Changed(object? sender, EventArgs e) => Update();
        private void Update()
        {
            if (_theme is null) return;
            var look = _theme.ButtonLook;
            bool primary = GetRole(_button) == "Primary", icon = GetRole(_button) == "Icon";
            _button.CornerRadius = new CornerRadius(icon ? 7 : look switch { ButtonLook.Soft => 12, ButtonLook.Liquid => 18, _ => 5 });
            _button.Background = icon || look == ButtonLook.Outline ? new SolidColorBrush(Colors.Transparent)
                : primary ? _theme.Accent : _theme.Surface(_button.ActualTheme, "RaisedBackgroundBrush");
            _button.Foreground = primary ? look == ButtonLook.Outline ? _theme.AccentText(_button.ActualTheme) : _theme.OnAccent
                : _theme.Surface(_button.ActualTheme, "TextPrimaryBrush");
            _button.BorderBrush = primary || look == ButtonLook.Outline ? _theme.Accent : _theme.Surface(_button.ActualTheme, "CardBorderBrush");
            _button.BorderThickness = new Thickness(icon ? 0 : 1);
            if (_liquid is not null) _liquid.Visibility = look == ButtonLook.Liquid && !icon ? Visibility.Visible : Visibility.Collapsed;
            if (Find(_button, "HoverLayer") is Border hover)
                hover.Background = new SolidColorBrush(_button.ActualTheme == ElementTheme.Light ? Colors.Black : Colors.White);
        }
        private void Entered(object sender, PointerRoutedEventArgs args)
        {
            if (_theme?.ButtonLook != ButtonLook.Liquid || _sheen is null || !_button.IsEnabled || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
            ElementCompositionPreview.SetIsTranslationEnabled(_sheen, true);
            var visual = ElementCompositionPreview.GetElementVisual(_sheen);
            var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            float distance = (float)Math.Max(80, _button.ActualWidth);
            animation.InsertKeyFrame(0, new Vector3(-distance, 0, 0));
            animation.InsertKeyFrame(1, new Vector3(distance, 0, 0));
            animation.Duration = TimeSpan.FromMilliseconds(650);
            visual.StartAnimation("Translation", animation);
        }
        private static FrameworkElement? Find(DependencyObject root, string name)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement f && f.Name == name) return f;
                if (Find(child, name) is { } match) return match;
            }
            return null;
        }
    }
}
