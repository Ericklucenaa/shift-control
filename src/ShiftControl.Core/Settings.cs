using System.Text.RegularExpressions;

namespace ShiftControl.Core;

public enum ThemeMode { Dark, Light, System }
public enum VisualTheme { Graphite, Ocean, Forest, Plum, Sand }
public enum ButtonLook { Classic, Soft, Outline, Liquid }

public enum TemperatureUnit { Celsius, Fahrenheit }

public enum HealthLevel { Unknown, Good, Attention, Critical }

public sealed record AccentOption(string Key, string Hex);

public static partial class AccentPalette
{
    public const string Default = "#2BB3A3";

    public static IReadOnlyList<AccentOption> Options { get; } =
    [
        new("turquoise", "#2BB3A3"),
        new("amber", "#D9A441"),
        new("coral", "#E2725B"),
        new("lime", "#8DBF4A"),
        new("steel", "#5B8DEF"),
        new("rose", "#D0568F"),
    ];

    public static bool IsValid(string? hex) => hex is not null && HexColor().IsMatch(hex);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();
}

/// <summary>Preferências persistidas. <see cref="Normalize"/> corrige valores fora do permitido.</summary>
public sealed class AppSettings
{
    public static readonly int[] AllowedIntervalsMs = [500, 1000, 2000, 5000];

    public int Version { get; set; } = 1;
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public VisualTheme VisualTheme { get; set; } = VisualTheme.Graphite;
    public ButtonLook ButtonLook { get; set; } = ButtonLook.Classic;
    public string Accent { get; set; } = AccentPalette.Default;
    public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;
    public string Language { get; set; } = "pt-BR";

    public int SampleIntervalMs { get; set; } = 1000;
    public bool RecordHistory { get; set; } = true;
    public int RawRetentionHours { get; set; } = 2;
    public int AggregatedRetentionDays { get; set; } = 30;

    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool SidebarCollapsed { get; set; }
    /// <summary>Fechar a janela mantém o app na bandeja (monitoramento, alertas e Game Boost continuam).</summary>
    public bool CloseToTray { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; } = true;

    public List<string> FavoriteSensors { get; set; } = [];

    public DateTimeOffset? LastAnalysisAt { get; set; }
    public HealthLevel LastAnalysisLevel { get; set; } = HealthLevel.Unknown;

    public AppSettings Normalize()
    {
        if (!AllowedIntervalsMs.Contains(SampleIntervalMs))
            SampleIntervalMs = AllowedIntervalsMs.OrderBy(v => Math.Abs(v - SampleIntervalMs)).First();
        RawRetentionHours = Math.Clamp(RawRetentionHours, 1, 48);
        AggregatedRetentionDays = Math.Clamp(AggregatedRetentionDays, 1, 365);
        if (!AccentPalette.IsValid(Accent)) Accent = AccentPalette.Default;
        if (!Enum.IsDefined(Theme)) Theme = ThemeMode.Dark;
        if (!Enum.IsDefined(VisualTheme)) VisualTheme = VisualTheme.Graphite;
        if (!Enum.IsDefined(ButtonLook)) ButtonLook = ButtonLook.Classic;
        if (!Enum.IsDefined(TemperatureUnit)) TemperatureUnit = TemperatureUnit.Celsius;
        if (!Enum.IsDefined(LastAnalysisLevel)) LastAnalysisLevel = HealthLevel.Unknown;
        if (string.IsNullOrWhiteSpace(Language)) Language = "pt-BR";
        FavoriteSensors = (FavoriteSensors ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.Ordinal).ToList();
        return this;
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.FavoriteSensors = [.. FavoriteSensors];
        return copy;
    }
}
