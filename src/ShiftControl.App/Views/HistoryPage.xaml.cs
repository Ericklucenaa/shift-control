using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class HistoryPage : Page, IRefreshOnShow
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public HistoryViewModel Vm { get; }

    public HistoryPage()
    {
        Vm = App.Services.GetRequiredService<HistoryViewModel>();
        InitializeComponent();
        // Períodos curtos andam junto com o tempo; a consulta roda fora da thread da interface.
        _timer.Tick += (_, _) =>
        {
            if (Vm.AutoRefresh) _ = Vm.RefreshAsync();
        };
        Loaded += (_, _) =>
        {
            _timer.Start();
            RefreshNow();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void RefreshNow() => _ = Vm.RefreshAsync();

    private void OnPeriodLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key } rb) rb.IsChecked = key == Vm.SelectedPeriod.Key;
    }

    private void OnPeriodChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key } && Vm.Periods.FirstOrDefault(p => p.Key == key) is { } period
            && period != Vm.SelectedPeriod)
            Vm.SelectedPeriod = period;
    }
}
