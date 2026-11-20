using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using ShiftControl.App.Services;
using ShiftControl.App.Views;
using ShiftControl.Monitoring;
using Windows.UI;

namespace ShiftControl.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["overview"] = typeof(DashboardPage),
        ["history"] = typeof(HistoryPage),
        ["components"] = typeof(ComponentsPage),
        ["sensors"] = typeof(SensorsPage),
        ["overlay"] = typeof(OverlayPage),
        ["sessions"] = typeof(SessionsPage),
        ["formatkit"] = typeof(FormatKitPage),
        ["checks"] = typeof(ChecksPage),
        ["undo"] = typeof(UndoPage),
        ["drivers"] = typeof(DriversPage),
        ["disks"] = typeof(DisksPage),
        ["boost"] = typeof(GameBoostPage),
        ["migration"] = typeof(MigrationPage),
        ["cleanup"] = typeof(CleanupPage),
        ["startup"] = typeof(StartupPage),
        ["tasks"] = typeof(TaskManagerPage),
        ["programs"] = typeof(ProgramsPage),
        ["repair"] = typeof(RepairPage),
        ["network"] = typeof(NetworkPage),
        ["alerts"] = typeof(AlertsPage),
        ["settings"] = typeof(SettingsPage),
    };

    private readonly SettingsService _settings;
    private readonly SnapshotHub _hub;
    private readonly MonitoringEngine _engine;
    private readonly ThemeService _theme;

    public MainWindow(bool startMinimized, string? startPage = null)
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<SettingsService>();
        _hub = App.Services.GetRequiredService<SnapshotHub>();
        _engine = App.Services.GetRequiredService<MonitoringEngine>();
        _theme = App.Services.GetRequiredService<ThemeService>();

        _hub.Attach(DispatcherQueue);
        _theme.AttachRoot(Root);
        _theme.Apply(_settings.Current);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Root.ActualThemeChanged += (_, _) => ApplyCaptionColors();
        ApplyCaptionColors();

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "ShiftControl.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        SizeWindow();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Scale(760);
            presenter.PreferredMinimumHeight = Scale(520);
            if (startMinimized) presenter.Minimize();
        }

        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += (_, e) =>
        {
            // Fechar esconde na bandeja: monitoramento, alertas e Game Boost continuam. "Sair" no menu da bandeja encerra.
            if (!AllowClose && _settings.Current.CloseToTray && App.Services.GetRequiredService<TrayService>().IsAvailable)
            {
                e.Cancel = true;
                AppWindow.Hide();
                UpdateVisibility();
            }
        };
        _hub.EngineStateChanged += (_, state) => FaultBar.IsOpen = state == EngineState.Faulted;
        if (_engine.State == EngineState.Faulted) FaultBar.IsOpen = true;
        AdminTag.Visibility = Hardware.Elevation.IsElevated ? Visibility.Visible : Visibility.Collapsed;

        _settings.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateLanguage);

        Nav.IsPaneOpen = !_settings.Current.SidebarCollapsed;
        var items = Nav.MenuItems.OfType<NavigationViewItem>().ToList();
        Nav.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Tag as string, startPage, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        UpdateVisibility();
    }

    public void Show() => Activate();

    /// <summary>Verdadeiro quando o encerramento foi pedido pelo menu da bandeja (ignora "fechar para a bandeja").</summary>
    public bool AllowClose { get; set; }

    private int Scale(int dip)
    {
        uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (int)Math.Round(dip * (dpi == 0 ? 96 : dpi) / 96.0);
    }

    private void SizeWindow()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = Math.Min(Scale(1360), (int)(area.Width * 0.92));
        int height = Math.Min(Scale(880), (int)(area.Height * 0.92));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    private void ApplyCaptionColors()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        var tb = AppWindow.TitleBar;
        var fg = dark ? Color.FromArgb(255, 0xEC, 0xED, 0xEE) : Color.FromArgb(255, 0x15, 0x17, 0x1A);
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = fg;
        tb.ButtonInactiveForegroundColor = dark ? Color.FromArgb(255, 0x6F, 0x74, 0x7B) : Color.FromArgb(255, 0x80, 0x86, 0x8D);
        tb.ButtonHoverBackgroundColor = dark ? Color.FromArgb(255, 0x25, 0x27, 0x2A) : Color.FromArgb(255, 0xE2, 0xE4, 0xE7);
        tb.ButtonHoverForegroundColor = fg;
        tb.ButtonPressedBackgroundColor = dark ? Color.FromArgb(255, 0x2E, 0x30, 0x34) : Color.FromArgb(255, 0xD8, 0xDB, 0xDF);
        tb.ButtonPressedForegroundColor = fg;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange || args.DidVisibilityChange || args.DidSizeChange) UpdateVisibility();
    }

    /// <summary>Janela minimizada/oculta: coleta desacelera e a interface deixa de ser redesenhada.</summary>
    private void UpdateVisibility()
    {
        bool minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        bool visible = AppWindow.IsVisible && !minimized;
        bool wasHidden = !_hub.IsWindowVisible;
        _hub.IsWindowVisible = visible;
        _engine.Background = !visible;
        if (visible && wasHidden && ContentFrame.Content is IRefreshOnShow page) page.RefreshNow();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Pages.TryGetValue(tag, out var type)
            && ContentFrame.CurrentSourcePageType != type)
        {
            ContentFrame.Navigate(type, null, new EntranceNavigationTransitionInfo());
        }
    }

    private void OnPaneToggled(NavigationView sender, object args)
    {
        // Só persiste quando o painel está em modo expandido (em janelas estreitas ele fecha sozinho).
        if (sender.DisplayMode == NavigationViewDisplayMode.Expanded && _settings.Current.SidebarCollapsed == sender.IsPaneOpen)
            _settings.Update(s => s.SidebarCollapsed = !sender.IsPaneOpen);
    }

    private void OnAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        int index = sender.Key - Windows.System.VirtualKey.Number1;
        var items = Nav.MenuItems.OfType<NavigationViewItem>().ToList();
        if (index >= 0 && index < items.Count) Nav.SelectedItem = items[index];
        args.Handled = true;
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => SystemIntegration.OpenFolder(AppPaths.LogDirectory);

    private static readonly Dictionary<string, string> NavKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["overview"] = "nav.overview",
        ["history"] = "nav.history",
        ["sensors"] = "nav.sensors",
        ["drivers"] = "nav.drivers",
        ["disks"] = "nav.disks",
        ["overlay"] = "nav.overlay",
        ["sessions"] = "nav.sessions",
        ["boost"] = "nav.boost",
        ["formatkit"] = "nav.formatKit",
        ["migration"] = "nav.migration",
        ["checks"] = "nav.checks",
        ["components"] = "nav.components",
        ["cleanup"] = "nav.cleanup",
        ["startup"] = "nav.startup",
        ["repair"] = "nav.repair",
        ["network"] = "nav.network",
        ["alerts"] = "nav.alerts",
        ["undo"] = "nav.undo",
        ["settings"] = "nav.settings",
    };

    public void UpdateLanguage()
    {
        foreach (var item in Nav.MenuItems)
        {
            if (item is NavigationViewItem nvi && nvi.Tag is string tag && NavKeyMap.TryGetValue(tag, out var key))
            {
                nvi.Content = L.Get(key);
            }
            else if (item is NavigationViewItemHeader header && header.Tag is string htag)
            {
                header.Content = L.Get("nav.group." + htag);
            }
        }
        if (ContentFrame.Content is IRefreshOnShow refreshable)
        {
            refreshable.RefreshNow();
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}

/// <summary>Telas que precisam redesenhar ao voltar a ficar visíveis (os despachos foram pausados).</summary>
public interface IRefreshOnShow
{
    void RefreshNow();
}
