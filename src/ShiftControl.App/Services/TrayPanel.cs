using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ShiftControl.App.Overlay;
using ShiftControl.App.Views;
using ShiftControl.Maintenance;
using ShiftControl.Monitoring;

namespace ShiftControl.App.Services;

/// <summary>
/// Mini-painel da bandeja: leitura rápida (CPU, GPU, memória), estado do overlay e do Game Boost, e atalhos.
/// Janela pequena, sem barra de título, perto do ícone; fecha ao perder o foco.
/// </summary>
public sealed class TrayPanel
{
    private readonly MonitoringEngine _engine;
    private readonly OverlayService _overlay;
    private readonly GameBoostService _boost;
    private Window? _window;
    private DispatcherTimer? _timer;
    private TextBlock? _cpu, _gpu, _mem, _boostState;
    private ToggleSwitch? _overlaySwitch, _boostSwitch;
    private bool _updating;

    public TrayPanel(MonitoringEngine engine, OverlayService overlay, GameBoostService boost)
    {
        _engine = engine;
        _overlay = overlay;
        _boost = boost;
    }

    // Clicar no ícone tira o foco do painel antes de o clique chegar: o painel já fechou por "Deactivated"
    // e o clique, sozinho, o reabriria. Um clique logo após esse fechamento é o usuário fechando.
    private const long ReopenGuardMs = 500;
    private long _closedByFocusLoss = long.MinValue / 2;

    public void Toggle(int cursorX, int cursorY)
    {
        if (_window is not null)
        {
            Close();
            return;
        }
        if (Environment.TickCount64 - _closedByFocusLoss < ReopenGuardMs) return;
        Open(cursorX, cursorY);
    }

    private void Open(int x, int y)
    {
        var root = Build();
        _window = new Window { Content = root, Title = "Shift Control" };
        if (App.Window?.Content is FrameworkElement main) root.RequestedTheme = main.ActualTheme;
        var aw = _window.AppWindow;
        if (aw.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(true, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        aw.IsShownInSwitchers = false;

        var area = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(x, y), DisplayAreaFallback.Primary).WorkArea;
        double scale = (double)Win32Dpi(area) / 96;
        int w = (int)(300 * scale), h = (int)(318 * scale);
        int left = Math.Clamp(x - w / 2, area.X + 8, area.X + area.Width - w - 8);
        int top = y > area.Y + area.Height / 2 ? area.Y + area.Height - h - 8 : area.Y + 8;
        aw.MoveAndResize(new Windows.Graphics.RectInt32(left, top, w, h));

        _window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated || _window is null) return;
            _closedByFocusLoss = Environment.TickCount64;
            Close();
        };
        _window.Closed += (_, _) =>
        {
            _timer?.Stop();
            _window = null;
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
        _window.Activate();
    }

    private static uint Win32Dpi(Windows.Graphics.RectInt32 area) => App.Window is null ? 96 : GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(App.Window));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private void Close()
    {
        var w = _window;
        _window = null;
        _timer?.Stop();
        w?.Close();
    }

    private FrameworkElement Build()
    {
        Brush B(string key) => ViewHelpers.Themed(key);
        TextBlock Label(string text) => new() { Text = text, FontSize = 12, Foreground = B("TextSecondaryBrush") };
        TextBlock Value() => new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, Foreground = B("TextPrimaryBrush") };

        _cpu = Value();
        _gpu = Value();
        _mem = Value();
        _boostState = new TextBlock { FontSize = 12, Foreground = B("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap };

        var grid = new Grid { RowSpacing = 8, ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(int r, string label, TextBlock value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Label(label);
            Grid.SetRow(l, r);
            Grid.SetRow(value, r);
            Grid.SetColumn(value, 1);
            grid.Children.Add(l);
            grid.Children.Add(value);
        }
        Row(0, "CPU", _cpu);
        Row(1, "GPU", _gpu);
        Row(2, L.Get("tray.memory"), _mem);

        _overlaySwitch = new ToggleSwitch { Header = L.Get("tray.overlay"), OnContent = "", OffContent = "", MinWidth = 0 };
        _overlaySwitch.Toggled += (_, _) =>
        {
            if (!_updating && _overlaySwitch.IsOn != _overlay.Settings.Enabled) _overlay.UpdateSettings(s => s.Enabled = _overlaySwitch.IsOn);
        };
        _boostSwitch = new ToggleSwitch { Header = L.Get("tray.boost"), OnContent = "", OffContent = "", MinWidth = 0 };
        _boostSwitch.Toggled += (_, _) =>
        {
            if (_updating) return;
            if (_boostSwitch.IsOn && !_boost.IsActive) _boost.Activate(_boost.Settings.DefaultProfile, null, null);
            else if (!_boostSwitch.IsOn && _boost.IsActive) _boost.Deactivate(L.Get("boost.restoredManual"));
            Refresh();
        };
        var toggles = new Grid { ColumnSpacing = 12 };
        toggles.ColumnDefinitions.Add(new ColumnDefinition());
        toggles.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_boostSwitch, 1);
        toggles.Children.Add(_overlaySwitch);
        toggles.Children.Add(_boostSwitch);

        var open = new Button { Content = L.Get("tray.open"), HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["PrimaryButton"] };
        open.Click += (_, _) =>
        {
            Close();
            App.ShowMainWindow();
        };

        var title = new TextBlock { Text = "Shift Control", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = B("TextPrimaryBrush") };
        var stack = new StackPanel { Spacing = 14, Padding = new Thickness(18, 16, 18, 16) };
        stack.Children.Add(title);
        stack.Children.Add(grid);
        stack.Children.Add(new Border { Height = 1, Background = B("CardBorderBrush") });
        stack.Children.Add(toggles);
        stack.Children.Add(_boostState);
        stack.Children.Add(open);
        return new Grid { Background = B("CardBackgroundBrush"), Children = { stack } };
    }

    private void Refresh()
    {
        if (_window is null) return;
        _updating = true;
        var s = _engine.Latest;
        string T(double? v) => v is { } x ? Fmt.Temperature(x) : "—";
        string P(double? v) => v is { } x ? Fmt.Percent(x) : "—";
        _cpu!.Text = s is null ? "—" : $"{P(s.Cpu.Load.Value)} · {T(s.Cpu.Temperature.Value)}";
        var gpu = s?.PrimaryGpu;
        _gpu!.Text = gpu is null ? "—" : $"{P(gpu.Load.Value)} · {T(gpu.Temperature.Value)}";
        _mem!.Text = s is null || s.Memory.TotalBytes == 0 ? "—" : $"{Fmt.Percent(s.Memory.LoadPercent)} · {Fmt.Bytes(s.Memory.UsedBytes)}";
        _overlaySwitch!.IsOn = _overlay.Settings.Enabled;
        _boostSwitch!.IsOn = _boost.IsActive;
        _boostState!.Text = _boost.Active is { } a
            ? L.F("tray.boostActive", L.Get("boost.profile." + a.Profile), a.Game ?? L.Get("boost.manual"))
            : _boost.Settings.AutoApply ? L.Get("tray.boostAuto") : L.Get("tray.boostOff");
        _updating = false;
    }
}
