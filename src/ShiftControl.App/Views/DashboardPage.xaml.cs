using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;
using ShiftControl.Core;

namespace ShiftControl.App.Views;

public sealed partial class DashboardPage : Page, IRefreshOnShow
{
    private readonly SnapshotHub _hub;

    public DashboardViewModel Vm { get; }

    public DashboardPage()
    {
        Vm = App.Services.GetRequiredService<DashboardViewModel>();
        _hub = App.Services.GetRequiredService<SnapshotHub>();
        InitializeComponent();

        // Só recebe atualizações enquanto está na tela: gráficos fora de vista não custam nada.
        Loaded += (_, _) =>
        {
            _hub.Updated += OnUpdated;
            RefreshNow();
        };
        Unloaded += (_, _) => _hub.Updated -= OnUpdated;
        ActualThemeChanged += (_, _) => Bindings.Update();
    }

    public Brush HealthBrush(HealthLevel level) => ViewHelpers.ThemeBrush(this, ViewHelpers.HealthKey(level));

    private void OnUpdated(object? sender, SystemSnapshot snapshot) => Vm.Update(snapshot);

    public void RefreshNow()
    {
        if (_hub.Latest is { } s) Vm.Update(s);
    }
}
