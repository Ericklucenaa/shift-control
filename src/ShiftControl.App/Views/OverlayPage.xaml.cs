using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using ShiftControl.App.Overlay;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace ShiftControl.App.Views;

public sealed partial class OverlayPage : Page
{
    private readonly OverlayService _service;
    private readonly OverlayPanel _preview = new();
    private readonly OverlayPanel _floatingPreview = new();
    private bool _capturingHotkey;

    public OverlayPageViewModel Vm { get; }

    public OverlayPage()
    {
        _service = App.Services.GetRequiredService<OverlayService>();
        Vm = App.Services.GetRequiredService<OverlayPageViewModel>();
        InitializeComponent();
        PreviewHost.Content = _preview;
        FloatingPreviewHost.Content = _floatingPreview;
        ScreenMini.PositionChosen += (_, pos) => Vm.SetPosition(pos.Anchor, pos.OffsetX, pos.OffsetY);

        Loaded += (_, _) =>
        {
            Vm.Reload();
            Vm.RefreshStatus();
            _preview.Apply(_service.ActivePreset);
            _floatingPreview.Apply(_service.ActivePreset);
            _service.Rendered += OnRendered;
            _service.Changed += OnServiceChanged;
            _service.AttachPreview();
        };
        Unloaded += (_, _) =>
        {
            _service.Rendered -= OnRendered;
            _service.Changed -= OnServiceChanged;
            _service.DetachPreview();
        };
        ActualThemeChanged += (_, _) => Bindings.Update();
    }

    public Brush CaptureBrush(bool ok) => ViewHelpers.ThemeBrush(this, ok ? "GoodBrush" : "AttentionBrush");

    private void OnPageScrolled(object? sender, ScrollViewerViewChangedEventArgs e) =>
        FloatingPreview.Visibility = PageScroll.VerticalOffset > 350 ? Visibility.Visible : Visibility.Collapsed;

    private void OnRendered(object? sender, OverlayFrame frame)
    {
        _preview.Update(frame);
        _floatingPreview.Update(frame);
    }

    /// <summary>A prévia usa exatamente o preset em uso pelo overlay.</summary>
    private void OnServiceChanged(object? sender, EventArgs e)
    {
        var preset = _service.ActivePreset;
        if (!PresetEquals(_preview.Preset, preset))
        {
            _preview.Apply(preset);
            _floatingPreview.Apply(preset);
        }
    }

    private static bool PresetEquals(ShiftControl.Overlay.OverlayPreset a, ShiftControl.Overlay.OverlayPreset b) =>
        System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);

    private void OnMetricsReordered(ListViewBase sender, DragItemsCompletedEventArgs args) => Vm.CommitItems();

    private void OnLabelLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverlayItemVm item }) item.CommitLabel();
    }

    private void OnPingLostFocus(object sender, RoutedEventArgs e) => Vm.CommitPingHost();
    private void OnExclusionsLostFocus(object sender, RoutedEventArgs e) => Vm.CommitExclusions();
    private void OnManualLostFocus(object sender, RoutedEventArgs e) => Vm.CommitManualExecutable();

    private void OnUseProcess(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PresentingVm p }) Vm.UseProcessCommand.Execute(p);
    }

    private void OnIgnoreProcess(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PresentingVm p }) Vm.ExcludeProcessCommand.Execute(p);
    }

    private void OnApplyStyle(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: StyleVm style }) Vm.ApplyStyleCommand.Execute(style);
    }

    private void OnRemoveGamePreset(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GamePresetVm g }) Vm.RemoveGamePresetCommand.Execute(g);
    }

    private async void OnBrowseExe(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(App.Window!.AppWindow.Id);
            picker.FileTypeFilter.Add(".exe");
            var result = await picker.PickSingleFileAsync();
            if (result is null) return;
            Vm.ManualExecutable = Path.GetFileName(result.Path);
            Vm.CommitManualExecutable();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Falha ao escolher o executável.");
        }
    }

    private async void OnNewPreset(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = L.Get("overlay.presetNamePlaceholder"), MaxLength = 32 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.Get("overlay.presetNewTitle"),
            Content = box,
            PrimaryButtonText = L.Get("overlay.presetCreate"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) Vm.DuplicatePreset(box.Text);
    }

    private async void OnGrant(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.Get("overlay.grant.title"),
            Content = new TextBlock { Text = L.Get("overlay.grant.message"), TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            PrimaryButtonText = L.Get("overlay.grant.confirm"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        bool ok = await Vm.GrantPermissionAsync();
        var done = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.Get(ok ? "overlay.grant.doneTitle" : "overlay.grant.failTitle"),
            Content = new TextBlock { Text = L.Get(ok ? "overlay.grant.done" : "overlay.grant.fail"), TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            CloseButtonText = L.Get("common.close"),
        };
        await done.ShowAsync();
    }

    #region Gravação do atalho

    private void OnHotkeyChange(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyBox.Text = "";
        HotkeyBox.PlaceholderText = L.Get("overlay.hotkeyPress");
        HotkeyBox.Focus(FocusState.Programmatic);
    }

    private void OnHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;
        var key = e.Key;
        if (key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows
            or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.LeftMenu or VirtualKey.RightMenu)
            return; // espera a tecla principal
        if (key == VirtualKey.Escape)
        {
            _capturingHotkey = false;
            HotkeyBox.Text = Vm.Hotkey;
            return;
        }

        bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);
        var parts = new List<string>();
        if (Down(VirtualKey.Control)) parts.Add("Ctrl");
        if (Down(VirtualKey.Shift)) parts.Add("Shift");
        if (Down(VirtualKey.Menu)) parts.Add("Alt");
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) parts.Add("Win");
        string name = key switch
        {
            >= VirtualKey.A and <= VirtualKey.Z => ((char)key).ToString(),
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)key).ToString(),
            >= VirtualKey.F1 and <= VirtualKey.F12 => "F" + (key - VirtualKey.F1 + 1),
            VirtualKey.Insert => "Insert",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Pause => "Pause",
            VirtualKey.Scroll => "ScrollLock",
            _ => "",
        };
        if (name.Length == 0) return;
        parts.Add(name);

        if (Vm.SetHotkey(string.Join('+', parts)))
        {
            _capturingHotkey = false;
            HotkeyBox.Text = Vm.Hotkey;
        }
        else
        {
            HotkeyBox.Text = "";
            HotkeyBox.PlaceholderText = L.Get("overlay.hotkeyInvalid");
        }
    }

    #endregion
}
