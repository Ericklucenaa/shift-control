using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using ShiftControl.Core;
using Windows.UI;

namespace ShiftControl.App.Services;

/// <summary>
/// Tema claro/escuro e cor de destaque em tempo real. Os pincéis de acento são instâncias únicas
/// registradas nos recursos do app; mudar a cor altera todos os controles que as usam, sem recarregar telas.
/// </summary>
public sealed class ThemeService
{
    // Pincéis próprios do app.
    private readonly SolidColorBrush _accent = new();
    private readonly SolidColorBrush _accentSubtle = new();
    private readonly SolidColorBrush _accentFill = new();
    private readonly SolidColorBrush _onAccent = new();
    private readonly SolidColorBrush _accentTextDark = new();
    private readonly SolidColorBrush _accentTextLight = new();

    // Pincéis que os controles padrão do WinUI consultam (botão de destaque, toggle, rádio, seleção da navegação…).
    private readonly SolidColorBrush _fillDefault = new();
    private readonly SolidColorBrush _fillSecondary = new();
    private readonly SolidColorBrush _fillTertiary = new();

    // Pincéis e propriedades dinâmicas do estilo de botões.
    private readonly SolidColorBrush _quietBtnBg = new();
    private readonly SolidColorBrush _quietBtnBorder = new();
    private readonly SolidColorBrush _primaryBtnBorder = new();

    private FrameworkElement? _root;
    private readonly Dictionary<(string Theme, string Key), SolidColorBrush> _surfaces = [];
    public ButtonLook ButtonLook { get; private set; }
    public event EventHandler? AppearanceChanged;

    public void Install(ResourceDictionary resources)
    {
        _resources = resources;
        resources["AccentBrush"] = _accent;
        resources["AccentSubtleBrush"] = _accentSubtle;
        resources["AccentAreaBrush"] = _accentFill;
        resources["OnAccentBrush"] = _onAccent;
        resources["QuietButtonBackgroundBrush"] = _quietBtnBg;
        resources["QuietButtonBorderBrush"] = _quietBtnBorder;
        resources["PrimaryButtonBorderBrush"] = _primaryBtnBorder;

        foreach (var (theme, accentText) in new[] { ("Dark", _accentTextDark), ("Light", _accentTextLight) })
        {
            if (!resources.ThemeDictionaries.TryGetValue(theme, out var obj) || obj is not ResourceDictionary dict)
            {
                dict = new ResourceDictionary();
                resources.ThemeDictionaries[theme] = dict;
            }
            dict["AccentTextBrush"] = accentText;
            dict["AccentFillColorDefaultBrush"] = _fillDefault;
            dict["AccentFillColorSecondaryBrush"] = _fillSecondary;
            dict["AccentFillColorTertiaryBrush"] = _fillTertiary;
            dict["AccentTextFillColorPrimaryBrush"] = accentText;
            dict["AccentTextFillColorSecondaryBrush"] = accentText;
            dict["AccentTextFillColorTertiaryBrush"] = accentText;
            dict["TextOnAccentFillColorPrimaryBrush"] = _onAccent;
            dict["TextOnAccentFillColorSecondaryBrush"] = _onAccent;
            dict["NavigationViewSelectionIndicatorForeground"] = _accent;
            foreach (var pair in Appearance.Colors(VisualTheme.Graphite, theme == "Dark"))
            {
                // Reutiliza os pincéis já referenciados pelos estilos XAML carregados.
                var source = resources.MergedDictionaries.Select(r =>
                    r.ThemeDictionaries.TryGetValue(theme, out var themed) ? themed as ResourceDictionary : null)
                    .FirstOrDefault(r => r?.ContainsKey(pair.Key) == true);
                var brush = source?[pair.Key] as SolidColorBrush ?? new SolidColorBrush(Parse(pair.Value));
                _surfaces[(theme, pair.Key)] = brush;
                dict[pair.Key] = brush;
            }
            foreach (var key in new[] { "NavigationViewDefaultPaneBackground", "NavigationViewExpandedPaneBackground" })
                dict[key] = _surfaces[(theme, "SidebarBackgroundBrush")];
            dict["NavigationViewContentBackground"] = _surfaces[(theme, "AppBackgroundBrush")];
            dict["NavigationViewContentGridBorderBrush"] = _surfaces[(theme, "CardBorderBrush")];
        }
    }

    public void AttachRoot(FrameworkElement root) => _root = root;

    public ElementTheme ActualTheme(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private string? _appliedAccent;

    public void Apply(AppSettings settings)
    {
        if (!string.Equals(_appliedAccent, settings.Accent, StringComparison.OrdinalIgnoreCase)) SetAccent(settings.Accent);
        _appliedAccent = settings.Accent;
        ButtonLook = settings.ButtonLook;
        foreach (var theme in new[] { "Dark", "Light" })
            foreach (var pair in Appearance.Colors(settings.VisualTheme, theme == "Dark"))
                if (_surfaces.TryGetValue((theme, pair.Key), out var brush)) brush.Color = Parse(pair.Value);
        if (_root is not null) _root.RequestedTheme = ActualTheme(settings.Theme);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public SolidColorBrush Surface(ElementTheme theme, string key) => _surfaces[(theme == ElementTheme.Light ? "Light" : "Dark", key)];
    public SolidColorBrush Accent => _accent;
    public SolidColorBrush OnAccent => _onAccent;
    public SolidColorBrush AccentText(ElementTheme theme) => theme == ElementTheme.Light ? _accentTextLight : _accentTextDark;

    private ResourceDictionary? _resources;

    /// <summary>
    /// Os templates do WinUI derivam o acento das cores SystemAccentColor*. Elas são lidas quando cada
    /// controle aplica o template; para mudança em tempo real, o tema é reaplicado (ver <see cref="Apply"/>).
    /// Tema escuro usa Light1–3; tema claro usa Dark1–3 (mais escuras, para contraste em fundo claro).
    /// </summary>
    private void SetSystemAccent(Color c)
    {
        if (_resources is null) return;
        _resources["SystemAccentColor"] = c;
        _resources["SystemAccentColorLight1"] = Mix(c, Colors.White, 0.12);
        _resources["SystemAccentColorLight2"] = c;
        _resources["SystemAccentColorLight3"] = Mix(c, Colors.White, 0.30);
        _resources["SystemAccentColorDark1"] = Mix(c, Colors.Black, 0.28);
        _resources["SystemAccentColorDark2"] = Mix(c, Colors.Black, 0.40);
        _resources["SystemAccentColorDark3"] = Mix(c, Colors.Black, 0.52);
    }

    public void SetAccent(string hex)
    {
        var c = Parse(hex);
        SetSystemAccent(c);
        _accent.Color = c;
        _accentSubtle.Color = WithAlpha(c, 0x2E);
        _accentFill.Color = WithAlpha(c, 0x1F);
        _fillDefault.Color = c;
        _fillSecondary.Color = WithAlpha(c, 0xE6);
        _fillTertiary.Color = WithAlpha(c, 0xCC);
        _onAccent.Color = Luminance(c) > 0.179 ? Colors.Black : Colors.White;
        _accentTextDark.Color = Mix(c, Colors.White, 0.18);
        _accentTextLight.Color = Mix(c, Colors.Black, 0.32);
    }

    public static Color Parse(string hex)
    {
        if (!AccentPalette.IsValid(hex)) hex = AccentPalette.Default;
        return Color.FromArgb(255,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Luminância relativa (WCAG) para escolher texto claro ou escuro sobre o acento.</summary>
    private static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }
}
