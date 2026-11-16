using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;
using ShiftControl.Core;

namespace ShiftControl.App.Views;

public sealed partial class SensorsPage : Page, IRefreshOnShow
{
    private readonly SnapshotHub _hub;
    private readonly ILogger<SensorsPage> _log;

    public SensorsViewModel Vm { get; }

    public SensorsPage()
    {
        Vm = App.Services.GetRequiredService<SensorsViewModel>();
        _hub = App.Services.GetRequiredService<SnapshotHub>();
        _log = App.Services.GetRequiredService<ILogger<SensorsPage>>();
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _hub.Updated += OnUpdated;
            RefreshNow();
        };
        Unloaded += (_, _) => _hub.Updated -= OnUpdated;
    }

    public static double ValueOpacity(bool available) => available ? 1 : 0.5;

    private void OnUpdated(object? sender, SystemSnapshot s) => Vm.Update(s);

    public void RefreshNow()
    {
        if (_hub.Latest is { } s) Vm.Update(s);
    }

    private void OnClearSearch(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Vm.Query = "";
        args.Handled = true;
    }

    private void OnExportCsv(object sender, RoutedEventArgs e) => _ = ExportAsync("CSV", ".csv", Vm.ExportCsv);
    private void OnExportJson(object sender, RoutedEventArgs e) => _ = ExportAsync("JSON", ".json", Vm.ExportJson);

    private async Task ExportAsync(string typeName, string extension, Func<string> build)
    {
        try
        {
            // Seletor do Windows App SDK: funciona também com o app elevado (o de Windows.Storage não).
            var picker = new FileSavePicker(App.Window!.AppWindow.Id)
            {
                SuggestedFileName = $"shift-control-sensores-{DateTime.Now:yyyyMMdd-HHmm}",
                DefaultFileExtension = extension,
            };
            picker.FileTypeChoices.Add(typeName, [extension]);
            var result = await picker.PickSaveFileAsync();
            if (result is null) return; // cancelado

            string content = build();
            await File.WriteAllTextAsync(result.Path, content);
            ShowExport(InfoBarSeverity.Success, L.F("sensors.exported", Path.GetFileName(result.Path)));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao exportar sensores.");
            ShowExport(InfoBarSeverity.Error, L.Get("sensors.exportError"));
        }
    }

    private void ShowExport(InfoBarSeverity severity, string message)
    {
        ExportBar.Severity = severity;
        ExportBar.Message = message;
        ExportBar.IsOpen = true;
    }
}
