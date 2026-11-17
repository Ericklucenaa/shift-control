using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;
using ShiftControl.Maintenance;

namespace ShiftControl.App.Views;

public sealed partial class ProgramsPage : Page
{
    public ProgramsViewModel Vm { get; }
    private bool _flowRunning;

    public ProgramsPage()
    {
        Vm = App.Services.GetRequiredService<ProgramsViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => { if (!Vm.Uninstalling) await Vm.RefreshAsync(); };
    }

    private async void OnUninstall(object sender, RoutedEventArgs e) => await RunFlowAsync(Vm.SelectedProgram?.Program);

    private async void OnDeleteKey(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunFlowAsync(Vm.SelectedProgram?.Program);
    }

    /// <summary>Confirmar → desinstalador oficial → procurar sobras → o usuário escolhe o que remover.</summary>
    private async Task RunFlowAsync(InstalledProgram? program)
    {
        if (program is null || _flowRunning || Vm.Uninstalling || InstalledPrograms.IsProtected(program)) return;
        _flowRunning = true;
        try
        {
            var (go, quiet) = await ConfirmAsync(program);
            if (!go) return;
            var outcome = await Vm.UninstallAsync(program, quiet);
            bool orphan = outcome.Result == UninstallResult.UninstallerMissing;
            if (outcome.Result != UninstallResult.Removed && !orphan) return;
            var leftovers = await Vm.ScanLeftoversAsync(program, orphan);
            if (leftovers.Count == 0) return;
            var chosen = await ChooseLeftoversAsync(program, leftovers, orphan);
            if (chosen.Count > 0) await Vm.RemoveLeftoversAsync(program, chosen);
        }
        finally { _flowRunning = false; }
    }

    private async Task<(bool Go, bool Quiet)> ConfirmAsync(InstalledProgram program)
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 480 };
        body.Children.Add(new TextBlock { Text = L.F("programs.confirm.body", program.Name), TextWrapping = TextWrapping.Wrap });
        if (InstalledPrograms.IsSharedRuntime(program))
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = L.Get("programs.confirm.shared") });
        if (Command(program) is null)
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = L.Get("programs.confirm.orphan") });
        CheckBox? quiet = null;
        if (program.HasQuietUninstall)
        {
            quiet = new CheckBox { Content = L.Get("programs.confirm.quiet"), IsChecked = false };
            ToolTipService.SetToolTip(quiet, L.Get("programs.confirm.quietTip"));
            body.Children.Add(quiet);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.F("programs.confirm.title", program.Name),
            Content = body,
            PrimaryButtonText = L.Get("programs.uninstall"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        bool go = await dialog.ShowAsync() == ContentDialogResult.Primary;
        return (go, quiet?.IsChecked == true);
    }

    private static UninstallCommand? Command(InstalledProgram p) => InstalledPrograms.Command(p, quiet: false);

    private async Task<IReadOnlyList<Leftover>> ChooseLeftoversAsync(InstalledProgram program, IReadOnlyList<Leftover> leftovers, bool orphan)
    {
        var rows = leftovers.Select(l => new LeftoverVm(l)).ToList();
        var list = new StackPanel { Spacing = 6 };
        foreach (var row in rows)
        {
            var box = new CheckBox { IsChecked = true, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = row.Title, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false });
            text.Children.Add(new TextBlock { Text = row.KindText, Style = (Style)Application.Current.Resources["CaptionText"] });
            box.Content = text;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, row.Title);
            box.Checked += (_, _) => row.Selected = true;
            box.Unchecked += (_, _) => row.Selected = false;
            list.Children.Add(box);
        }
        var body = new StackPanel { Spacing = 12, MaxWidth = 520 };
        body.Children.Add(new TextBlock
        {
            Text = L.F(orphan ? "programs.leftovers.orphanBody" : "programs.leftovers.body", program.Name),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new TextBlock
        {
            Text = L.Get("programs.leftovers.safety"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionText"],
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L.Get("programs.leftovers.title"),
            Content = body,
            PrimaryButtonText = L.Get("programs.leftovers.remove"),
            CloseButtonText = L.Get("programs.leftovers.keep"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return [];
        return rows.Where(r => r.Selected).Select(r => r.Item).ToList();
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        ProgramRowVm? row = null;
        var current = args.OriginalSource as DependencyObject;
        while (current is not null && current != ProgramList)
        {
            if (current is FrameworkElement { DataContext: ProgramRowVm found }) { row = found; break; }
            current = VisualTreeHelper.GetParent(current);
        }
        row ??= Vm.SelectedProgram;
        if (row is null) return;
        Vm.SelectedProgram = row;
        var target = row.Program;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = target.Name, IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        var uninstall = new MenuFlyoutItem { Text = L.Get("programs.uninstall"), IsEnabled = !row.IsProtected && !Vm.Uninstalling };
        uninstall.Click += async (_, _) => await RunFlowAsync(target);
        menu.Items.Add(uninstall);
        var open = new MenuFlyoutItem { Text = L.Get("programs.openLocation"), IsEnabled = Vm.HasLocation };
        open.Click += (_, _) => Vm.OpenLocationCommand.Execute(null);
        menu.Items.Add(open);
        if (args.TryGetPosition(ProgramList, out var position)) menu.ShowAt(ProgramList, new FlyoutShowOptions { Position = position });
        else menu.ShowAt(ProgramList.ContainerFromItem(row) as FrameworkElement ?? ProgramList);
        args.Handled = true;
    }
}
