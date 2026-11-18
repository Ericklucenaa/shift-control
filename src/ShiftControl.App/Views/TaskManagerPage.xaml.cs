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
    private async void OnEndProcess(object sender, RoutedEventArgs e) => await ConfirmEndRowAsync(Vm.SelectedProcess);

    private async void OnEndKeyInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ConfirmEndRowAsync(Vm.SelectedProcess);
    }

    /// <summary>Linha agrupada: encerra todos os processos do nome; linha simples: só aquele processo.</summary>
    private Task ConfirmEndRowAsync(TaskProcessVm? row) =>
        row?.IsGroup == true ? ConfirmEndGroupAsync(row.Name, row.Count) : ConfirmEndAsync(row?.Snapshot);

    private async Task ConfirmEndGroupAsync(string name, int count)
    {
        if (Vm.Ending || _confirmingEnd) return;
        _confirmingEnd = true;
        try
        {
            if (await UiHelpers.ConfirmAsync(this, L.Get("tasks.endGroupTitle"), L.F("tasks.endGroupConfirm", name, count), L.F("tasks.endAll", name), destructive: true))
                await Vm.EndGroupAsync(name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao solicitar encerramento do grupo {Name}.", name);
            Vm.MessageIsError = true;
            Vm.Message = L.Get("tasks.end.Failed");
        }
        finally { _confirmingEnd = false; }
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
        string name = row.Name;
        int count = row.Count;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = row.IsGroup ? row.DisplayName : $"{target.Name} · PID {target.Pid}", IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        if (row.IsGroup)
        {
            var endGroup = new MenuFlyoutItem { Text = L.F("tasks.endAll", name), IsEnabled = row.CanEnd };
            endGroup.Click += async (_, _) => { menu.Hide(); await ConfirmEndGroupAsync(name, count); };
            menu.Items.Add(endGroup);
        }
        else
        {
            var end = new MenuFlyoutItem { Text = L.Get("tasks.end"), IsEnabled = row.CanEnd };
            end.Click += async (_, _) => { menu.Hide(); await ConfirmEndAsync(target); };
            menu.Items.Add(end);
            // Com a lista separada, o mesmo programa pode ter outros processos (ex.: chrome.exe): fechar todos juntos.
            int same = Vm.Processes.Count(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (same > 1)
            {
                var endGroup = new MenuFlyoutItem { Text = L.F("tasks.endAll", name), IsEnabled = row.CanEnd };
                endGroup.Click += async (_, _) => { menu.Hide(); await ConfirmEndGroupAsync(name, same); };
                menu.Items.Add(endGroup);
            }
        }
        if (args.TryGetPosition(ProcessList, out var position)) menu.ShowAt(ProcessList, new FlyoutShowOptions { Position = position });
        else menu.ShowAt(ProcessList.ContainerFromItem(row) as FrameworkElement ?? ProcessList);
        args.Handled = true;
    }
}
