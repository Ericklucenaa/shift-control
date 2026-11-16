using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml.Markup;

namespace ShiftControl.App.Services;

/// <summary>
/// Textos da interface. Cada idioma é um JSON embutido (Strings/pt-BR.json, en.json…);
/// chaves ausentes num idioma caem para o pt-BR, que é o idioma de referência.
/// </summary>
public static class L
{
    public const string DefaultLanguage = "pt-BR";

    private static Dictionary<string, string> _fallback = Load(DefaultLanguage) ?? [];
    private static Dictionary<string, string> _strings = _fallback;

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(DefaultLanguage);

    public static IReadOnlyCollection<string> Keys => _fallback.Keys;

    public static void SetLanguage(string language)
    {
        _strings = Load(language) ?? _fallback;
        try { Culture = CultureInfo.GetCultureInfo(language); }
        catch (CultureNotFoundException) { Culture = CultureInfo.GetCultureInfo(DefaultLanguage); }
    }

    public static string Get(string key) =>
        _strings.TryGetValue(key, out var value) || _fallback.TryGetValue(key, out value) ? value : key;

    public static string F(string key, params object?[] args) => string.Format(Culture, Get(key), args);

    private static Dictionary<string, string>? Load(string language)
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();
        string? name = names.FirstOrDefault(n => n.EndsWith($".Strings.{language}.json", StringComparison.OrdinalIgnoreCase))
                    ?? names.FirstOrDefault(n => n.Contains($".Strings.{language}", StringComparison.OrdinalIgnoreCase))
                    ?? (language.Contains('-') ? names.FirstOrDefault(n => n.Contains($".Strings.{language.Split('-')[0]}", StringComparison.OrdinalIgnoreCase)) : null);
        if (name is null) return null;
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
    }
}

/// <summary>Uso em XAML: <c>Text="{l:Tr Key=nav.overview}"</c>.</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Tr : MarkupExtension
{
    public string Key { get; set; } = "";
    public bool Upper { get; set; }

    protected override object ProvideValue()
    {
        var text = L.Get(Key);
        return Upper ? text.ToUpper(L.Culture) : text;
    }
}
