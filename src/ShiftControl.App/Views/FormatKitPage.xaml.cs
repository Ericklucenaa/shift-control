using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class FormatKitPage : Page
{
    private readonly List<RadioButton> _profileButtons = [];

    public FormatKitViewModel Vm { get; }

    public FormatKitPage()
    {
        Vm = App.Services.GetRequiredService<FormatKitViewModel>();
        InitializeComponent();
        Vm.Attach(DispatcherQueue);

        foreach (var profile in Vm.Profiles)
        {
            var rb = new RadioButton
            {
                Content = profile.Label,
                GroupName = "kitProfile",
                Style = (Style)Application.Current.Resources["SegmentRadio"],
                Tag = profile,
            };
            rb.Checked += (_, _) => Vm.Profile = (Choice<string>)rb.Tag;
            _profileButtons.Add(rb);
            ProfileHost.Children.Add(rb);
        }
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FormatKitViewModel.Profile)) SyncProfileButtons();
            if (e.PropertyName == nameof(FormatKitViewModel.Running))
                foreach (var rb in _profileButtons) rb.IsEnabled = !Vm.Running;
        };

        Loaded += async (_, _) =>
        {
            SyncProfileButtons();
            await Vm.LoadAsync();
        };
        ActualThemeChanged += (_, _) => Bindings.Update();
    }

    private void SyncProfileButtons()
    {
        foreach (var rb in _profileButtons) rb.IsChecked = ReferenceEquals(rb.Tag, Vm.Profile);
    }

    public static bool CanStart(bool running, int count, bool loading) => !running && !loading && count > 0;

    public static bool CanEdit(bool running, bool loading) => !running && !loading;

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        Vm.ClearSearchCommand.Execute(null);
        PackageSearch.Focus(FocusState.Programmatic);
    }

    // Um InfoBar fechado continua no StackPanel com altura zero e ainda recebe o Spacing; recolhê-lo evita o vão.
    private void OnInfoBarClosed(InfoBar sender, InfoBarClosedEventArgs args) => sender.Visibility = Visibility.Collapsed;

    public static Visibility ShowRetry(bool hasFailures, bool running) => hasFailures && !running ? Visibility.Visible : Visibility.Collapsed;

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker(App.Window!.AppWindow.Id)
            {
                SuggestedFileName = $"shift-control-kit-{DateTime.Now:yyyyMMdd}",
                DefaultFileExtension = ".json",
            };
            picker.FileTypeChoices.Add(L.Get("formatKit.kitFileType"), [".json"]);
            var result = await picker.PickSaveFileAsync();
            if (result is null) return;
            await File.WriteAllTextAsync(result.Path, Vm.ExportKit());
            Show(InfoBarSeverity.Success, L.F("formatKit.exported", Path.GetFileName(result.Path)));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Falha ao exportar o kit.");
            Show(InfoBarSeverity.Error, L.Get("formatKit.exportError"));
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(App.Window!.AppWindow.Id);
            picker.FileTypeFilter.Add(".json");
            var result = await picker.PickSingleFileAsync();
            if (result is null) return;
            var (matched, unknown) = Vm.ImportKit(await File.ReadAllTextAsync(result.Path));
            Show(unknown.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                unknown.Count == 0 ? L.F("formatKit.imported", matched)
                    : L.F("formatKit.importedPartial", matched, unknown.Count, string.Join(", ", unknown.Take(8))));
        }
        catch (InvalidDataException ex)
        {
            Show(InfoBarSeverity.Error, L.F("formatKit.importInvalid", ex.Message));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Falha ao importar o kit.");
            Show(InfoBarSeverity.Error, L.Get("formatKit.importError"));
        }
    }

    private void Show(InfoBarSeverity severity, string message)
    {
        KitBar.Severity = severity;
        KitBar.Message = message;
        KitBar.IsOpen = true;
        KitBar.Visibility = Visibility.Visible;
    }
}
