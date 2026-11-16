using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ShiftControl.Core;
using ShiftControl.Diagnostics;

namespace ShiftControl.App.Views;

/// <summary>Funções para x:Bind. Pincéis de status respeitam o tema efetivo do elemento.</summary>
public static class ViewHelpers
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;
    public static bool HasText(string? text) => !string.IsNullOrEmpty(text);

    public static Brush ThemeBrush(FrameworkElement scope, string key)
    {
        string theme = scope.ActualTheme == ElementTheme.Light ? "Light" : "Dark";
        var resources = Application.Current.Resources;
        foreach (var dict in resources.MergedDictionaries.Prepend(resources))
        {
            if (dict.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary themed && themed.TryGetValue(key, out var b) && b is Brush brush)
                return brush;
            if (dict.TryGetValue(key, out var plain) && plain is Brush plainBrush)
                return plainBrush;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    /// <summary>Pincel do tema atual da janela principal, para uso em templates (x:Bind de função estática).</summary>
    public static Brush Themed(string key) =>
        App.Window?.Content is FrameworkElement root ? ThemeBrush(root, key) : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    public static Brush ToneBrush(ViewModels.Tone tone) => Themed(tone switch
    {
        ViewModels.Tone.Good => "GoodBrush",
        ViewModels.Tone.Attention => "AttentionBrush",
        ViewModels.Tone.Critical => "CriticalBrush",
        ViewModels.Tone.Accent => "AccentBrush",
        ViewModels.Tone.Neutral => "TextSecondaryBrush",
        _ => "TextTertiaryBrush",
    });

    public static bool HasFraction(double f) => f >= 0;
    public static string Tr(string key, int n) => Services.L.F(key, n);
    public static string RunButton(bool running) => Services.L.Get(running ? "common.stop" : "network.run");
    public static double Dim(bool enabled) => enabled ? 1 : 0.45;
    public static string OrDefault(string? text, string key) => string.IsNullOrEmpty(text) ? Services.L.Get(key) : text;
    public static Brush ActiveBorder(bool active) => Themed(active ? "AccentBrush" : "CardBorderBrush");
    public static string BoostButton(bool active) => Services.L.Get(active ? "boost.deactivate" : "boost.activate");
    public static Visibility FractionVisible(double f) => f >= 0 ? Visibility.Visible : Visibility.Collapsed;
    public static GridLength Star(double f) => new(Math.Clamp(f, 0, 1) * 1000 + 0.001, GridUnitType.Star);
    public static GridLength StarRest(double f) => new((1 - Math.Clamp(f, 0, 1)) * 1000 + 0.001, GridUnitType.Star);

    public static Brush CheckBrush(CheckStatus status) => Themed(status switch
    {
        CheckStatus.Ok => "GoodBrush",
        CheckStatus.Attention => "AttentionBrush",
        CheckStatus.Info => "TextSecondaryBrush",
        _ => "TextTertiaryBrush",
    });

    public static Brush JobBrush(Setup.JobState state) => Themed(state switch
    {
        Setup.JobState.Done => "GoodBrush",
        Setup.JobState.Failed => "CriticalBrush",
        Setup.JobState.Running => "AccentBrush",
        Setup.JobState.Cancelled => "AttentionBrush",
        _ => "TextTertiaryBrush",
    });

    public static Brush PackageBrush(bool? installed, bool hasUpdate) => Themed(
        hasUpdate ? "AttentionBrush" : installed == true ? "GoodBrush" : "TextTertiaryBrush");

    public static Brush ChangeBrush(Storage.ChangeState state) => Themed(state switch
    {
        Storage.ChangeState.Applied => "AccentBrush",
        Storage.ChangeState.Reverted => "GoodBrush",
        Storage.ChangeState.RevertFailed => "CriticalBrush",
        _ => "TextTertiaryBrush",
    });

    public static string HealthKey(HealthLevel level) => level switch
    {
        HealthLevel.Good => "GoodBrush",
        HealthLevel.Attention => "AttentionBrush",
        HealthLevel.Critical => "CriticalBrush",
        _ => "TextTertiaryBrush",
    };

    public static string SeverityKey(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => "CriticalBrush",
        FindingSeverity.Attention => "AttentionBrush",
        _ => "TextTertiaryBrush",
    };

    public static string SeverityGlyph(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => "",
        FindingSeverity.Attention => "",
        _ => "",
    };
}
