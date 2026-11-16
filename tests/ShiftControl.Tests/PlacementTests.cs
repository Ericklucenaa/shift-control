using ShiftControl.Overlay;

namespace ShiftControl.Tests;

public class PlacementTests
{
    private static readonly ScreenRect Screen = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(OverlayAnchor.TopLeft, 16, 16)]
    [InlineData(OverlayAnchor.TopRight, 1920 - 200 - 16, 16)]
    [InlineData(OverlayAnchor.BottomLeft, 16, 1080 - 100 - 16)]
    [InlineData(OverlayAnchor.BottomRight, 1920 - 200 - 16, 1080 - 100 - 16)]
    [InlineData(OverlayAnchor.TopCenter, 860, 16)]
    [InlineData(OverlayAnchor.BottomCenter, 860, 1080 - 100 - 16)]
    [InlineData(OverlayAnchor.MiddleLeft, 16, 490)]
    [InlineData(OverlayAnchor.MiddleRight, 1920 - 200 - 16, 490)]
    [InlineData(OverlayAnchor.Custom, 16, 16)]
    public void Anchors(OverlayAnchor anchor, double x, double y)
    {
        var (px, py) = OverlayPlacement.Compute(Screen, 200, 100, anchor, 16, 16);
        Assert.Equal(x, px, 6);
        Assert.Equal(y, py, 6);
    }

    [Fact]
    public void Second_monitor_uses_its_own_origin()
    {
        var right = new ScreenRect(1920, -200, 2560, 1440);
        Assert.Equal((2560 + 1920 - 200 - 10, -200 + 10), OverlayPlacement.Compute(right, 200, 100, OverlayAnchor.TopRight, 10, 10));
    }

    [Fact]
    public void Never_leaves_the_screen()
    {
        var (x, y) = OverlayPlacement.Compute(Screen, 200, 100, OverlayAnchor.TopLeft, 5000, 5000);
        Assert.Equal((1720, 980), (x, y));
        var (bx, by) = OverlayPlacement.Compute(Screen, 3000, 2000, OverlayAnchor.BottomRight, 0, 0); // maior que a tela
        Assert.Equal((0, 0), (bx, by));
    }

    [Fact]
    public void Dragged_position_round_trips_as_custom_offsets()
    {
        var (ox, oy) = OverlayPlacement.CustomOffsets(Screen, 700.4, 300.6, 200, 100);
        Assert.Equal((700, 301), (ox, oy));
        Assert.Equal((700, 301), OverlayPlacement.Compute(Screen, 200, 100, OverlayAnchor.Custom, ox, oy));
        Assert.Equal((0, 980), OverlayPlacement.CustomOffsets(Screen, -50, 5000, 200, 100));
    }

    [Fact]
    public void Pickable_anchor_points_are_on_the_screen_edges()
    {
        Assert.Equal(8, OverlayPlacement.Pickable.Count);
        foreach (var a in OverlayPlacement.Pickable)
        {
            var (rx, ry) = OverlayPlacement.AnchorPoint(a);
            Assert.True(rx is 0 or 0.5 or 1 && ry is 0 or 0.5 or 1);
            Assert.False(rx == 0.5 && ry == 0.5); // o centro da tela não é oferecido (atrapalharia o jogo)
        }
    }

    [Fact]
    public void Styles_change_look_and_position_but_keep_metrics()
    {
        var keys = OverlayStyles.All.Select(s => s.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        foreach (var style in OverlayStyles.All)
        {
            var p = OverlayPreset.Competitive();
            var metricsBefore = p.VisibleItems.Select(i => i.Metric).ToList();
            style.Apply(p);
            p.Normalize();
            Assert.Equal(metricsBefore, p.VisibleItems.Select(i => i.Metric).ToList());
            Assert.InRange(p.Scale, 0.5, 3);
        }
        var clean = OverlayPreset.Minimal();
        OverlayStyles.All.Single(s => s.Key == "clean").Apply(clean);
        Assert.Equal(0, clean.BackgroundOpacity);
        Assert.Equal(OverlayAnchor.TopRight, clean.Anchor);
    }

    [Fact]
    public void Corner_radius_is_clamped()
    {
        Assert.Equal(16, new OverlayPreset { CornerRadius = 99 }.Normalize().CornerRadius);
        Assert.Equal(0, new OverlayPreset { CornerRadius = -3 }.Normalize().CornerRadius);
    }
}
