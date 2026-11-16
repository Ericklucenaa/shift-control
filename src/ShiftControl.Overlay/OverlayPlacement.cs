namespace ShiftControl.Overlay;

/// <summary>Retângulo da tela (em qualquer unidade: pixels físicos na janela real, DIPs na prévia).</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height);

/// <summary>
/// Onde o overlay fica na tela. A mesma conta é usada pela janela real e pela miniatura da prévia,
/// por isso a prévia mostra exatamente a posição final.
/// </summary>
public static class OverlayPlacement
{
    /// <summary>Canto superior esquerdo do overlay. Âncoras centrais ignoram o deslocamento no eixo centralizado.</summary>
    public static (double X, double Y) Compute(ScreenRect screen, double width, double height, OverlayAnchor anchor, double offsetX, double offsetY)
    {
        double left = screen.X + offsetX;
        double right = screen.X + screen.Width - width - offsetX;
        double centerX = screen.X + (screen.Width - width) / 2;
        double top = screen.Y + offsetY;
        double bottom = screen.Y + screen.Height - height - offsetY;
        double centerY = screen.Y + (screen.Height - height) / 2;

        var (x, y) = anchor switch
        {
            OverlayAnchor.TopRight => (right, top),
            OverlayAnchor.BottomLeft => (left, bottom),
            OverlayAnchor.BottomRight => (right, bottom),
            OverlayAnchor.TopCenter => (centerX, top),
            OverlayAnchor.BottomCenter => (centerX, bottom),
            OverlayAnchor.MiddleLeft => (left, centerY),
            OverlayAnchor.MiddleRight => (right, centerY),
            _ => (left, top), // TopLeft e Custom: deslocamento a partir do canto superior esquerdo
        };

        // Nunca fora da tela, mesmo com deslocamento grande ou overlay maior que o monitor.
        x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.X + screen.Width - width));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - height));
        return (x, y);
    }

    /// <summary>Posição livre (arrastada) → deslocamento a partir do canto superior esquerdo da tela.</summary>
    public static (double OffsetX, double OffsetY) CustomOffsets(ScreenRect screen, double x, double y, double width, double height)
    {
        double ox = Math.Clamp(x - screen.X, 0, Math.Max(0, screen.Width - width));
        double oy = Math.Clamp(y - screen.Y, 0, Math.Max(0, screen.Height - height));
        return (Math.Round(ox), Math.Round(oy));
    }

    /// <summary>As 8 âncoras que a prévia oferece para clique.</summary>
    public static IReadOnlyList<OverlayAnchor> Pickable { get; } =
    [
        OverlayAnchor.TopLeft, OverlayAnchor.TopCenter, OverlayAnchor.TopRight,
        OverlayAnchor.MiddleLeft, OverlayAnchor.MiddleRight,
        OverlayAnchor.BottomLeft, OverlayAnchor.BottomCenter, OverlayAnchor.BottomRight,
    ];

    /// <summary>Posição relativa (0–1) do ponto de âncora na tela, para desenhar os alvos de clique.</summary>
    public static (double X, double Y) AnchorPoint(OverlayAnchor anchor) => anchor switch
    {
        OverlayAnchor.TopCenter => (0.5, 0),
        OverlayAnchor.TopRight => (1, 0),
        OverlayAnchor.MiddleLeft => (0, 0.5),
        OverlayAnchor.MiddleRight => (1, 0.5),
        OverlayAnchor.BottomLeft => (0, 1),
        OverlayAnchor.BottomCenter => (0.5, 1),
        OverlayAnchor.BottomRight => (1, 1),
        _ => (0, 0),
    };
}

/// <summary>Um visual pronto: muda disposição, posição e aparência, mantendo as métricas escolhidas.</summary>
public sealed record OverlayStyle(string Key, Action<OverlayPreset> Apply);

public static class OverlayStyles
{
    public static IReadOnlyList<OverlayStyle> All { get; } =
    [
        // Cartão discreto no canto, como a maioria dos monitores de desempenho.
        new("corner", p => Set(p, OverlayOrientation.Vertical, OverlayAnchor.TopLeft, 16, 16, opacity: 0.72, radius: 6, font: 13, scale: 1, bars: true, graphs: true)),
        // Faixa fina colada no topo, centralizada: ocupa pouca altura.
        new("topbar", p => Set(p, OverlayOrientation.Horizontal, OverlayAnchor.TopCenter, 0, 0, opacity: 0.62, radius: 0, font: 12, scale: 1, bars: false, graphs: false)),
        // Só o texto, sem fundo: o mínimo de interferência visual.
        new("clean", p => Set(p, OverlayOrientation.Horizontal, OverlayAnchor.TopRight, 14, 10, opacity: 0, radius: 0, font: 14, scale: 1, bars: false, graphs: false)),
        // Coluna na lateral direita, centralizada na altura: bom para o preset Completo.
        new("sidebar", p => Set(p, OverlayOrientation.Vertical, OverlayAnchor.MiddleRight, 0, 0, opacity: 0.8, radius: 0, font: 13, scale: 1, bars: true, graphs: true)),
        // Linha no rodapé, centralizada, levemente maior: boa para gravação e transmissão.
        new("bottom", p => Set(p, OverlayOrientation.Horizontal, OverlayAnchor.BottomCenter, 0, 24, opacity: 0.72, radius: 8, font: 13, scale: 1.15, bars: true, graphs: false)),
        // Escala grande para TV, 4K ou jogar longe da tela.
        new("large", p => Set(p, OverlayOrientation.Vertical, OverlayAnchor.TopLeft, 24, 24, opacity: 0.8, radius: 8, font: 14, scale: 1.6, bars: true, graphs: true)),
    ];

    private static void Set(OverlayPreset p, OverlayOrientation orientation, OverlayAnchor anchor, double ox, double oy,
        double opacity, double radius, double font, double scale, bool bars, bool graphs)
    {
        p.Orientation = orientation;
        p.Anchor = anchor;
        p.OffsetX = ox;
        p.OffsetY = oy;
        p.BackgroundOpacity = opacity;
        p.CornerRadius = radius;
        p.FontSize = font;
        p.Scale = scale;
        p.ShowBars = bars;
        p.ShowGraphs = graphs;
    }
}
