using ShiftControl.Core;

namespace ShiftControl.Overlay;

/// <summary>Métricas que o overlay sabe exibir. Novos valores devem ser adicionados ao final (são persistidos por nome).</summary>
public enum OverlayMetric
{
    GameName, SessionTime,
    Fps, FpsAverage, FpsMinimum, FpsMaximum, OnePercentLow, PointOnePercentLow, Frametime, FrametimeGraph, Stutter,
    CpuLoad, CpuCores, CpuTemperature, CpuClock, CpuPower,
    GpuLoad, GpuTemperature, GpuHotspot, GpuClock, GpuMemoryClock, GpuPower, GpuFan, Vram,
    Ram, DiskActivity,
    Ping, Jitter, PacketLoss,
}

/// <summary>De onde vem cada métrica. Fontes desligadas não são coletadas (economia de recursos).</summary>
public enum OverlaySource
{
    /// <summary>Sensores do monitoramento já existente.</summary>
    Sensors,
    /// <summary>Eventos de apresentação de frames (ETW via PresentMon).</summary>
    FramePresentation,
    /// <summary>Ping ICMP ao destino configurado.</summary>
    Network,
    /// <summary>Dados da sessão (nome do jogo, duração).</summary>
    Session,
}

public enum OverlayOrientation { Vertical, Horizontal }

/// <summary>Persistido por nome; novos valores no final.</summary>
public enum OverlayAnchor { TopLeft, TopRight, BottomLeft, BottomRight, Custom, TopCenter, BottomCenter, MiddleLeft, MiddleRight }

public enum OverlayAlignment { Left, Right }

public static class OverlayCatalog
{
    public static IReadOnlyList<OverlayMetric> All { get; } = Enum.GetValues<OverlayMetric>();

    public static OverlaySource SourceOf(OverlayMetric metric) => metric switch
    {
        OverlayMetric.Fps or OverlayMetric.FpsAverage or OverlayMetric.FpsMinimum or OverlayMetric.FpsMaximum
            or OverlayMetric.OnePercentLow or OverlayMetric.PointOnePercentLow or OverlayMetric.Frametime
            or OverlayMetric.FrametimeGraph or OverlayMetric.Stutter => OverlaySource.FramePresentation,
        OverlayMetric.Ping or OverlayMetric.Jitter or OverlayMetric.PacketLoss => OverlaySource.Network,
        OverlayMetric.GameName or OverlayMetric.SessionTime => OverlaySource.Session,
        _ => OverlaySource.Sensors,
    };

    /// <summary>Valor atual de uma métrica de sensor. Métricas de frames, rede e sessão não vêm do snapshot.</summary>
    public static Reading Value(OverlayMetric metric, SystemSnapshot s)
    {
        var gpu = s.PrimaryGpu;
        var none = Reading.Missing(Unavailable.NotSupported);
        return metric switch
        {
            OverlayMetric.CpuLoad => s.Cpu.Load,
            OverlayMetric.CpuTemperature => s.Cpu.Temperature,
            OverlayMetric.CpuClock => s.Cpu.Clock,
            OverlayMetric.CpuPower => s.Cpu.Power,
            OverlayMetric.GpuLoad => gpu?.Load ?? none,
            OverlayMetric.GpuTemperature => gpu?.Temperature ?? none,
            OverlayMetric.GpuHotspot => gpu?.Hotspot ?? none,
            OverlayMetric.GpuClock => gpu?.CoreClock ?? none,
            OverlayMetric.GpuMemoryClock => gpu?.MemoryClock ?? none,
            OverlayMetric.GpuPower => gpu?.Power ?? none,
            OverlayMetric.GpuFan => gpu?.FanRpm ?? none,
            OverlayMetric.Vram => gpu?.VramUsedMb ?? none,
            OverlayMetric.Ram => s.Memory.TotalBytes > 0 ? Reading.Of(s.Memory.UsedBytes) : Reading.Missing(Unavailable.Pending),
            OverlayMetric.DiskActivity => s.Drives.Any(d => d.ReadBps.HasValue) ? Reading.Of(s.DiskReadBps + s.DiskWriteBps) : Reading.Missing(Unavailable.Pending),
            _ => none,
        };
    }
}

/// <summary>Uma linha do overlay: qual métrica, se aparece e um nome opcional escolhido pelo usuário.</summary>
public sealed class OverlayItem
{
    public OverlayMetric Metric { get; set; }
    public bool Visible { get; set; }
    /// <summary>Rótulo personalizado; nulo ou vazio usa o nome padrão.</summary>
    public string? Label { get; set; }

    public OverlayItem Clone() => new() { Metric = Metric, Visible = Visible, Label = Label };
}

/// <summary>Configuração visual e de conteúdo do overlay.</summary>
public sealed class OverlayPreset
{
    public static readonly int[] AllowedIntervalsMs = [250, 500, 1000];

    public string Name { get; set; } = "Minimalista";
    /// <summary>Ordem = ordem de exibição. Contém todas as métricas; <see cref="OverlayItem.Visible"/> decide quais aparecem.</summary>
    public List<OverlayItem> Items { get; set; } = [];
    public OverlayOrientation Orientation { get; set; } = OverlayOrientation.Vertical;
    public OverlayAlignment Alignment { get; set; } = OverlayAlignment.Left;
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopLeft;
    /// <summary>Distância da borda do monitor (em pixels independentes de DPI).</summary>
    public double OffsetX { get; set; } = 16;
    public double OffsetY { get; set; } = 16;
    /// <summary>Índice do monitor (-1 = principal).</summary>
    public int MonitorIndex { get; set; } = -1;
    public double FontSize { get; set; } = 13;
    public double Scale { get; set; } = 1.0;
    public double BackgroundOpacity { get; set; } = 0.72;
    /// <summary>Arredondamento dos cantos do fundo (0 = reto).</summary>
    public double CornerRadius { get; set; } = 6;
    public string TextColor { get; set; } = "#ECEDEE";
    public string LabelColor { get; set; } = "#8F949A";
    public string AccentColor { get; set; } = AccentPalette.Default;
    public string BackgroundColor { get; set; } = "#0E0F10";
    public bool ShowGraphs { get; set; } = true;
    public bool ShowBars { get; set; } = true;
    public int UpdateIntervalMs { get; set; } = 500;

    public IEnumerable<OverlayItem> VisibleItems => Items.Where(i => i.Visible);

    public OverlayPreset Clone()
    {
        var copy = (OverlayPreset)MemberwiseClone();
        copy.Items = Items.Select(i => i.Clone()).ToList();
        return copy;
    }

    public OverlayPreset Normalize()
    {
        FontSize = Math.Clamp(FontSize, 9, 32);
        Scale = Math.Clamp(Scale, 0.5, 3);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 1);
        CornerRadius = Math.Clamp(CornerRadius, 0, 16);
        OffsetX = Math.Clamp(OffsetX, 0, 10_000);
        OffsetY = Math.Clamp(OffsetY, 0, 10_000);
        if (!AllowedIntervalsMs.Contains(UpdateIntervalMs)) UpdateIntervalMs = 500;
        if (!AccentPalette.IsValid(TextColor)) TextColor = "#ECEDEE";
        if (!AccentPalette.IsValid(LabelColor)) LabelColor = "#8F949A";
        if (!AccentPalette.IsValid(AccentColor)) AccentColor = AccentPalette.Default;
        if (!AccentPalette.IsValid(BackgroundColor)) BackgroundColor = "#0E0F10";
        if (!Enum.IsDefined(Orientation)) Orientation = OverlayOrientation.Vertical;
        if (!Enum.IsDefined(Alignment)) Alignment = OverlayAlignment.Left;
        if (!Enum.IsDefined(Anchor)) Anchor = OverlayAnchor.TopLeft;
        if (string.IsNullOrWhiteSpace(Name)) Name = "Personalizado";
        Name = Name.Trim();

        // Cada métrica aparece exatamente uma vez; as que faltam entram no fim, ocultas.
        var items = new List<OverlayItem>();
        var seen = new HashSet<OverlayMetric>();
        foreach (var item in Items ?? [])
        {
            if (item is null || !Enum.IsDefined(item.Metric) || !seen.Add(item.Metric)) continue;
            item.Label = string.IsNullOrWhiteSpace(item.Label) ? null : item.Label.Trim()[..Math.Min(item.Label.Trim().Length, 24)];
            items.Add(item);
        }
        foreach (var m in OverlayCatalog.All)
            if (seen.Add(m)) items.Add(new OverlayItem { Metric = m, Visible = false });
        Items = items;
        return this;
    }

    public static OverlayPreset Create(string name, OverlayOrientation orientation, params OverlayMetric[] visible)
    {
        var preset = new OverlayPreset
        {
            Name = name,
            Orientation = orientation,
            Items = visible.Select(m => new OverlayItem { Metric = m, Visible = true }).ToList(),
        };
        return preset.Normalize();
    }

    public static OverlayPreset Minimal() => Create("Minimalista", OverlayOrientation.Horizontal,
        OverlayMetric.Fps, OverlayMetric.OnePercentLow, OverlayMetric.CpuLoad, OverlayMetric.CpuTemperature,
        OverlayMetric.GpuLoad, OverlayMetric.GpuTemperature);

    public static OverlayPreset Complete() => Create("Completo", OverlayOrientation.Vertical,
        OverlayMetric.GameName, OverlayMetric.SessionTime, OverlayMetric.Fps, OverlayMetric.FpsAverage,
        OverlayMetric.OnePercentLow, OverlayMetric.PointOnePercentLow, OverlayMetric.Frametime, OverlayMetric.FrametimeGraph,
        OverlayMetric.CpuLoad, OverlayMetric.CpuCores, OverlayMetric.CpuTemperature, OverlayMetric.CpuClock, OverlayMetric.CpuPower,
        OverlayMetric.GpuLoad, OverlayMetric.GpuTemperature, OverlayMetric.GpuHotspot, OverlayMetric.GpuClock,
        OverlayMetric.GpuMemoryClock, OverlayMetric.GpuPower, OverlayMetric.GpuFan, OverlayMetric.Vram,
        OverlayMetric.Ram, OverlayMetric.DiskActivity, OverlayMetric.Ping);

    public static OverlayPreset Competitive()
    {
        var p = Create("Competitivo", OverlayOrientation.Vertical,
            OverlayMetric.Fps, OverlayMetric.Frametime, OverlayMetric.FrametimeGraph, OverlayMetric.OnePercentLow,
            OverlayMetric.Stutter, OverlayMetric.Ping, OverlayMetric.Jitter, OverlayMetric.PacketLoss,
            OverlayMetric.CpuLoad, OverlayMetric.GpuLoad);
        p.FontSize = 12;
        p.UpdateIntervalMs = 250;
        p.ShowBars = false;
        return p;
    }

    public static IReadOnlyList<OverlayPreset> BuiltIn() => [Minimal(), Complete(), Competitive()];
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; }
    public string Hotkey { get; set; } = "Ctrl+Shift+O";
    public string ActivePreset { get; set; } = "Minimalista";
    public List<OverlayPreset> Presets { get; set; } = [.. OverlayPreset.BuiltIn()];
    /// <summary>Executável do jogo → nome do preset a usar nele.</summary>
    public Dictionary<string, string> GamePresets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AutoDetect { get; set; } = true;
    /// <summary>Mostra o overlay só quando há um jogo detectado (ou no modo de edição).</summary>
    public bool OnlyInGame { get; set; } = true;
    public string? ManualExecutable { get; set; }
    public List<string> Exclusions { get; set; } = [];
    public string PingHost { get; set; } = "1.1.1.1";
    public bool RecordSessions { get; set; } = true;

    public OverlayPreset Active =>
        Presets.FirstOrDefault(p => p.Name == ActivePreset) ?? Presets.FirstOrDefault() ?? OverlayPreset.Minimal();

    /// <summary>Preset associado ao executável do jogo, se houver; senão, o ativo.</summary>
    public OverlayPreset ForGame(string? executable)
    {
        if (executable is not null
            && GamePresets.TryGetValue(GameTargetPolicy.Normalize(executable), out var name)
            && Presets.FirstOrDefault(p => p.Name == name) is { } preset)
            return preset;
        return Active;
    }

    public OverlaySettings Normalize()
    {
        Presets = (Presets ?? []).Where(p => p is not null).Select(p => p.Normalize())
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (Presets.Count == 0) Presets.AddRange(OverlayPreset.BuiltIn());
        if (!Hotkeys.TryParse(Hotkey, out _)) Hotkey = "Ctrl+Shift+O";
        if (Presets.All(p => p.Name != ActivePreset)) ActivePreset = Presets[0].Name;
        GamePresets = new Dictionary<string, string>(
            (GamePresets ?? []).Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && Presets.Any(p => p.Name == kv.Value))
                .ToDictionary(kv => GameTargetPolicy.Normalize(kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        ManualExecutable = string.IsNullOrWhiteSpace(ManualExecutable) ? null : GameTargetPolicy.Normalize(ManualExecutable);
        Exclusions = (Exclusions ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Select(GameTargetPolicy.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        PingHost = string.IsNullOrWhiteSpace(PingHost) ? "1.1.1.1" : PingHost.Trim();
        return this;
    }

    public OverlaySettings Clone()
    {
        var copy = (OverlaySettings)MemberwiseClone();
        copy.Presets = Presets.Select(p => p.Clone()).ToList();
        copy.GamePresets = new Dictionary<string, string>(GamePresets, StringComparer.OrdinalIgnoreCase);
        copy.Exclusions = [.. Exclusions];
        return copy;
    }
}
