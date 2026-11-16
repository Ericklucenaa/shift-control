using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;
using ShiftControl.Maintenance;

namespace ShiftControl.App.Views;

public sealed partial class TaskManagerPage : Page
{
    public TaskManagerViewModel Vm { get; }
    private readonly ILogger<TaskManagerPage> _log;
    private bool _confirmingEnd;
    public TaskManagerPage()
    {
        Vm = App.Services.GetRequiredService<TaskManagerViewModel>();
        _log = App.Services.GetRequiredService<ILogger<TaskManagerPage>>();
        InitializeComponent();
        Loaded += (_, _) => Vm.Start();
        Unloaded += (_, _) => Vm.Stop();
    }
    private async void OnEndProcess(object sender, RoutedEventArgs e)
    {
        await ConfirmEndAsync(Vm.SelectedProcess?.Snapshot);
    }

    private async void OnEndKeyInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ConfirmEndAsync(Vm.SelectedProcess?.Snapshot);
    }

    private async Task ConfirmEndAsync(TaskProcess? target)
    {
        if (target?.CanEnd != true || Vm.Ending || _confirmingEnd) return;
        _confirmingEnd = true;
        try
        {
            if (await UiHelpers.ConfirmAsync(this, L.Get("tasks.endTitle"), L.F("tasks.endConfirm", target.Name, target.Pid), L.Get("tasks.end"), destructive: true))
                await Vm.EndAsync(target);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao solicitar encerramento de PID {Pid}.", target.Pid);
            Vm.MessageIsError = true;
            Vm.Message = L.Get("tasks.end.Failed");
        }
        finally { _confirmingEnd = false; }
    }

    private void OnProcessContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        TaskProcessVm? row = null;
        var current = args.OriginalSource as DependencyObject;
        while (current is not null && current != ProcessList)
        {
            if (current is FrameworkElement { DataContext: TaskProcessVm found }) { row = found; break; }
            current = VisualTreeHelper.GetParent(current);
        }
        if (row is null && !args.TryGetPosition(ProcessList, out _)) row = Vm.SelectedProcess;
        if (row is null) return;
        Vm.SelectedProcess = row;
        var target = row.Snapshot; // Captura a linha clicada antes de qualquer reordenação.
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = $"{target.Name} · PID {target.Pid}", IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        var end = new MenuFlyoutItem { Text = L.Get("tasks.end"), IsEnabled = row.CanEnd };
        end.Click += async (_, _) => { menu.Hide(); await ConfirmEndAsync(target); };
        menu.Items.Add(end);
        if (args.TryGetPosition(ProcessList, out var position)) menu.ShowAt(ProcessList, new FlyoutShowOptions { Position = position });
        else menu.ShowAt(ProcessList.ContainerFromItem(row) as FrameworkElement ?? ProcessList);
        args.Handled = true;
    }
}
