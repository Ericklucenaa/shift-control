using ShiftControl.Core;
using ShiftControl.Storage;

namespace ShiftControl.Tests;

public class AppearanceTests
{
    [Fact]
    public void Appearance_options_roundtrip_and_invalid_values_fall_back()
    {
        using var dir = new TempDir();
        using var db = new ShiftDatabase(dir.File("appearance.db"));
        var store = new SettingsStore(db);
        store.Save(new AppSettings { Theme = ThemeMode.Light, VisualTheme = VisualTheme.Ocean, ButtonLook = ButtonLook.Liquid });
        var settings = store.Load();
        Assert.Equal(ThemeMode.Light, settings.Theme);
        Assert.Equal(VisualTheme.Ocean, settings.VisualTheme);
        Assert.Equal(ButtonLook.Liquid, settings.ButtonLook);
        Assert.Equal(ButtonLook.Liquid, settings.Clone().ButtonLook);
        settings.VisualTheme = (VisualTheme)999;
        settings.ButtonLook = (ButtonLook)999;
        settings.Normalize();
        Assert.Equal(VisualTheme.Graphite, settings.VisualTheme);
        Assert.Equal(ButtonLook.Classic, settings.ButtonLook);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_palette_has_legible_text_in_both_modes(bool dark)
    {
        foreach (var theme in Appearance.Themes)
        {
            var colors = Appearance.Colors(theme, dark);
            Assert.All(colors.Values, c => Assert.True(AccentPalette.IsValid(c)));
            foreach (var surface in new[] { "AppBackgroundBrush", "CardBackgroundBrush", "RaisedBackgroundBrush", "SidebarBackgroundBrush" })
                foreach (var text in new[] { "TextPrimaryBrush", "TextSecondaryBrush" })
                {
                    double a = Luminance(colors[surface]), b = Luminance(colors[text]);
                    Assert.True((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5, $"{theme} {dark} {surface} {text}");
                }
        }
    }
    private static double Luminance(string hex)
    {
        double Channel(int start)
        {
            double s = Convert.ToByte(hex.Substring(start, 2), 16) / 255.0;
            return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(1) + .7152 * Channel(3) + .0722 * Channel(5);
    }
}
