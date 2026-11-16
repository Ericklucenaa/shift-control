namespace ShiftControl.Core;

/// <summary>Paletas independentes do modo claro/escuro e da cor de destaque.</summary>
public static class Appearance
{
    public static IReadOnlyList<VisualTheme> Themes { get; } = Enum.GetValues<VisualTheme>();
    public static IReadOnlyList<ButtonLook> Buttons { get; } = Enum.GetValues<ButtonLook>();

    public static IReadOnlyDictionary<string, string> Colors(VisualTheme theme, bool dark)
    {
        string[] surfaces = (theme, dark) switch
        {
            (VisualTheme.Ocean, true) => ["#101923", "#0B131D", "#172431", "#2C4052", "#203143", "#2B4054"],
            (VisualTheme.Ocean, false) => ["#EDF4FA", "#E1ECF5", "#FFFFFF", "#CFDFEC", "#F3F8FC", "#E0EDF7"],
            (VisualTheme.Forest, true) => ["#121B17", "#0D1511", "#1A2720", "#30463A", "#23352A", "#2E4437"],
            (VisualTheme.Forest, false) => ["#F0F5EF", "#E4EDE2", "#FCFEFB", "#D2DFCF", "#F5F9F2", "#E4EFDE"],
            (VisualTheme.Plum, true) => ["#1B1421", "#140F19", "#271E2F", "#45344F", "#33263D", "#43314E"],
            (VisualTheme.Plum, false) => ["#F6F0F8", "#EDE2F1", "#FFFCFF", "#E2D3E8", "#FAF5FC", "#EEDFF3"],
            (VisualTheme.Sand, true) => ["#211C16", "#19150F", "#2C261E", "#4A4032", "#393024", "#493D2D"],
            (VisualTheme.Sand, false) => ["#F7F3EB", "#EEE6D8", "#FFFDFA", "#E2D8C6", "#FAF5EC", "#EEE3D1"],
            (_, true) => ["#101112", "#0C0D0E", "#17181A", "#232528", "#1E2023", "#25272A"],
            _ => ["#F3F4F5", "#ECEEF0", "#FFFFFF", "#DFE2E5", "#F7F8F9", "#EEF0F2"],
        };
        return new Dictionary<string, string>
        {
            ["AppBackgroundBrush"] = surfaces[0], ["SidebarBackgroundBrush"] = surfaces[1],
            ["CardBackgroundBrush"] = surfaces[2], ["CardBorderBrush"] = surfaces[3],
            ["RaisedBackgroundBrush"] = surfaces[4], ["HoverBackgroundBrush"] = surfaces[5],
            ["TextPrimaryBrush"] = dark ? "#ECEDEE" : "#15171A",
            ["TextSecondaryBrush"] = dark ? "#B0B4BB" : "#50565E",
            ["TextTertiaryBrush"] = dark ? "#90969F" : "#656C75",
            ["MeterTrackBrush"] = surfaces[3], ["GridLineBrush"] = surfaces[3],
        };
    }
}
