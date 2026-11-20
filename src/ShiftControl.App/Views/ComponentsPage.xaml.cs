using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.Services;
using ShiftControl.Hardware;

namespace ShiftControl.App.Views;

public sealed record ComponentRow(string Name, string? Detail = null)
{
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

public sealed record ComponentSection(string Title, IReadOnlyList<ComponentRow> Rows);

public sealed partial class ComponentsPage : Page
{
    public ComponentsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Sections.ItemsSource is null) await RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (!RefreshButton.IsEnabled) return;

        RefreshButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        ErrorBar.IsOpen = false;
        try
        {
            var inventory = await Task.Run(HardwareInventory.Read);
            Sections.ItemsSource = BuildSections(inventory);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<ComponentsPage>>()
                .LogWarning(ex, "Não foi possível ler os componentes do computador.");
            ErrorBar.IsOpen = true;
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
        }
    }

    private static ComponentSection[] BuildSections(HardwareInventory inventory)
    {
        var modules = inventory.MemoryModules;
        string memoryTitle = modules.Count > 0 && modules.All(m => m.CapacityBytes > 0)
            ? L.F("components.memoryTotal", Fmt.Bytes(modules.Sum(m => (double)m.CapacityBytes)))
            : L.Get("components.memory");

        return
        [
            Section(L.Get("components.cpu"), inventory.Processors.Select(cpu => new ComponentRow(
                Detected(cpu.Name),
                JoinDetails(cpu.Cores > 0 ? L.F("components.cores", cpu.Cores) : null,
                    cpu.Threads > 0 ? L.F("components.threads", cpu.Threads) : null)))),
            Section(L.Get("components.gpu"), inventory.GraphicsAdapters.Select(name => new ComponentRow(name))),
            Section(memoryTitle, modules.Select((module, index) => new ComponentRow(
                L.F("components.module", index + 1),
                Detected(JoinDetails(
                    module.CapacityBytes > 0 ? Fmt.Bytes(module.CapacityBytes) : null,
                    module.ConfiguredMts > 0 ? Fmt.Number(module.ConfiguredMts, 0) + " MT/s" : null,
                    string.Join(' ', new[] { module.Manufacturer, module.PartNumber }
                        .Where(value => !string.IsNullOrWhiteSpace(value)))))))),
            Section(L.Get("components.motherboard"),
                [new ComponentRow(Detected(inventory.Motherboard), L.F("components.bios", Detected(inventory.Bios)))]),
            Section(L.Get("components.storage"), inventory.Drives.Select(drive => new ComponentRow(
                Detected(drive.Model), drive.CapacityBytes > 0 ? Fmt.Bytes(drive.CapacityBytes) : null))),
        ];
    }

    private static ComponentSection Section(string title, IEnumerable<ComponentRow> rows) =>
        new(title, rows.DefaultIfEmpty(new ComponentRow(L.Get("components.unavailable"))).ToArray());

    private static string Detected(string? value) =>
        string.IsNullOrWhiteSpace(value) ? L.Get("components.unavailable") : value;

    private static string JoinDetails(params string?[] values) =>
        string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
}
