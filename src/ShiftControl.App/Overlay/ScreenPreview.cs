using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ShiftControl.App.Services;
using ShiftControl.Overlay;

namespace ShiftControl.App.Overlay;

/// <summary>
/// Miniatura do monitor, na proporção real, com o overlay desenhado na escala e na posição em que vai aparecer.
/// Usa o mesmo <see cref="OverlayPanel"/> e o mesmo cálculo de posição da janela real, com os dados ao vivo.
/// No modo interativo: clicar num dos pontos ancora o overlay; arrastar o overlay define uma posição livre.
/// </summary>
public sealed partial class ScreenPreview : Grid
{
    private readonly Border _screen = new()
    {
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2E, 0x34, 0x3A)),
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x45, 0x4B, 0x52)),
    };
    private readonly Canvas _canvas = new();
    private readonly OverlayPanel _panel = new();
    private readonly TextBlock _resolution = new()
    {
        FontSize = 10,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(150, 0xFF, 0xFF, 0xFF)),
        IsHitTestVisible = false,
    };
    private readonly List<(OverlayAnchor Anchor, Ellipse Dot)> _dots = [];
    // Contorno em volta do overlay: na escala real de um monitor grande ele fica pequeno; o contorno mostra onde está.
    private readonly Rectangle _outline = new() { StrokeThickness = 1.5, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false };
    private readonly ScaleTransform _scale = new();
    private ScreenRect _screenDip = new(0, 0, 1920, 1080);
    private OverlayPreset _preset = OverlayPreset.Minimal();
    private OverlayService? _service;
    private bool _dragging;
    private Windows.Foundation.Point _grab;
    private double _k = 0.2;

    public ScreenPreview()
    {
        _canvas.Children.Add(_resolution);
        _canvas.Children.Add(_outline);
        _panel.RenderTransform = _scale;
        _canvas.Children.Add(_panel);
        _screen.Child = _canvas;
        Children.Add(_screen);

        _panel.PointerPressed += OnPanelPressed;
        _panel.PointerMoved += OnPanelMoved;
        _panel.PointerReleased += OnPanelReleased;
        _panel.PointerCaptureLost += (_, _) => _dragging = false;
        _panel.SizeChanged += (_, _) => Layout();
        SizeChanged += (_, _) => Layout();

        Loaded += (_, _) =>
        {
            _service ??= App.Services.GetRequiredService<OverlayService>();
            if (Live) _service.Rendered += OnRendered;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (_service is not null) _service.Rendered -= OnRendered;
        };
    }

    /// <summary>Recebe os valores ao vivo do overlay.</summary>
    public bool Live { get; set; } = true;

    /// <summary>Permite clicar nas âncoras e arrastar o overlay.</summary>
    public bool Interactive { get; set; }

    public static readonly DependencyProperty PresetProperty =
        DependencyProperty.Register(nameof(Preset), typeof(OverlayPreset), typeof(ScreenPreview),
            new PropertyMetadata(null, (d, e) => ((ScreenPreview)d).OnPresetChanged((OverlayPreset?)e.NewValue)));

    public OverlayPreset? Preset { get => (OverlayPreset?)GetValue(PresetProperty); set => SetValue(PresetProperty, value); }

    /// <summary>Clique numa âncora ou fim de um arraste: (âncora, deslocamento X, deslocamento Y em DIPs).</summary>
    public event EventHandler<(OverlayAnchor Anchor, double? OffsetX, double? OffsetY)>? PositionChosen;

    private void OnPresetChanged(OverlayPreset? preset)
    {
        if (preset is null) return;
        _preset = preset.Clone();
        Refresh();
    }

    private void OnRendered(object? sender, OverlayFrame frame) => _panel.Update(frame);

    private void Refresh()
    {
        var (dip, _, pw, ph) = OverlayService.ScreenOf(_preset.MonitorIndex);
        _screenDip = dip;
        _resolution.Text = $"{pw}×{ph}";
        _panel.Apply(_preset);
        BuildDots();
        Layout();
    }

    private void BuildDots()
    {
        foreach (var (_, dot) in _dots) _canvas.Children.Remove(dot);
        _dots.Clear();
        if (!Interactive) return;
        foreach (var anchor in OverlayPlacement.Pickable)
        {
            bool selected = anchor == _preset.Anchor;
            var dot = new Ellipse
            {
                Width = selected ? 12 : 10,
                Height = selected ? 12 : 10,
                Fill = selected
                    ? (Brush)Application.Current.Resources["AccentBrush"]
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0xFF, 0xFF, 0xFF)),
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 0, 0, 0)),
                StrokeThickness = 1,
            };
            ToolTipService.SetToolTip(dot, L.Get($"overlay.anchor.{anchor}"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dot, L.Get($"overlay.anchor.{anchor}"));
            var a = anchor;
            dot.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                PositionChosen?.Invoke(this, (a, a is OverlayAnchor.TopCenter or OverlayAnchor.BottomCenter ? null : 16,
                    a is OverlayAnchor.MiddleLeft or OverlayAnchor.MiddleRight ? null : 16));
            };
            dot.PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
            dot.PointerExited += (_, _) => ProtectedCursor = null;
            _canvas.Children.Insert(0, dot);
            _dots.Add((anchor, dot));
        }
    }

    private void Layout()
    {
        double width = ActualWidth;
        if (width <= 0 || _screenDip.Width <= 0) return;
        double height = width * _screenDip.Height / _screenDip.Width;
        _screen.Width = width;
        _screen.Height = height;
        Height = height;
        _k = (width - 2) / _screenDip.Width;
        _scale.ScaleX = _scale.ScaleY = _k;

        if (!_dragging)
        {
            double pw = _panel.ActualWidth, ph = _panel.ActualHeight;
            var (x, y) = OverlayPlacement.Compute(_screenDip, pw, ph, _preset.Anchor, _preset.OffsetX, _preset.OffsetY);
            Canvas.SetLeft(_panel, x * _k);
            Canvas.SetTop(_panel, y * _k);
        }
        PlaceOutline();

        foreach (var (anchor, dot) in _dots)
        {
            var (rx, ry) = OverlayPlacement.AnchorPoint(anchor);
            double inset = 9;
            Canvas.SetLeft(dot, Math.Clamp(rx * (width - 2) - dot.Width / 2, inset - dot.Width / 2, width - 2 - inset - dot.Width / 2));
            Canvas.SetTop(dot, Math.Clamp(ry * (height - 2) - dot.Height / 2, inset - dot.Height / 2, height - 2 - inset - dot.Height / 2));
        }

        _resolution.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_resolution, (width - 2 - _resolution.DesiredSize.Width) / 2);
        Canvas.SetTop(_resolution, height / 2 - 8);
    }

    private void PlaceOutline()
    {
        _outline.Stroke ??= (Brush)Application.Current.Resources["AccentBrush"];
        const double pad = 3;
        _outline.Width = Math.Max(6, _panel.ActualWidth * _k + pad * 2);
        _outline.Height = Math.Max(6, _panel.ActualHeight * _k + pad * 2);
        Canvas.SetLeft(_outline, Canvas.GetLeft(_panel) - pad);
        Canvas.SetTop(_outline, Canvas.GetTop(_panel) - pad);
    }

    #region Arraste

    private void OnPanelPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!Interactive) return;
        _dragging = true;
        _grab = e.GetCurrentPoint(_panel).Position; // em DIPs do overlay (antes da escala)
        _panel.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPanelMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetCurrentPoint(_canvas).Position;
        double x = Math.Clamp(p.X / _k - _grab.X, 0, Math.Max(0, _screenDip.Width - _panel.ActualWidth));
        double y = Math.Clamp(p.Y / _k - _grab.Y, 0, Math.Max(0, _screenDip.Height - _panel.ActualHeight));
        Canvas.SetLeft(_panel, x * _k);
        Canvas.SetTop(_panel, y * _k);
        PlaceOutline();
        e.Handled = true;
    }

    private void OnPanelReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _panel.ReleasePointerCapture(e.Pointer);
        double x = Canvas.GetLeft(_panel) / _k, y = Canvas.GetTop(_panel) / _k;
        var (ox, oy) = OverlayPlacement.CustomOffsets(_screenDip, x, y, _panel.ActualWidth, _panel.ActualHeight);
        PositionChosen?.Invoke(this, (OverlayAnchor.Custom, ox, oy));
        e.Handled = true;
    }

    #endregion
}
