using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class MigrationPage : Page
{
    public MigrationViewModel Vm { get; }
    private bool _choosingFolder;
    private readonly ILogger<MigrationPage> _log;

    public MigrationPage()
    {
        Vm = App.Services.GetRequiredService<MigrationViewModel>();
        _log = App.Services.GetRequiredService<ILogger<MigrationPage>>();
        InitializeComponent();
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (Vm.Busy || _choosingFolder) return;
        _choosingFolder = true;
        try
        {
            Vm.Message = null;
            if (!Vm.IncludePrograms && !Vm.IncludeDrivers && !Vm.IncludeWifi && !Vm.IncludeSettings)
            {
                Vm.MessageIsError = true;
                Vm.Message = L.Get("migration.chooseOptions");
                return;
            }
            if (await UiHelpers.PickFolderAsync() is not { } folder) return;
            if (Vm.IncludeWifi && !await UiHelpers.ConfirmAsync(this, L.Get("migration.wifiConfirmTitle"), L.Get("migration.wifiConfirm"), L.Get("migration.create")))
                return;
            await Vm.CreateAsync(folder);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao escolher a pasta ou criar o pacote de migração.");
            Vm.MessageIsError = true;
            Vm.Message = L.F("migration.createError", ex.Message);
        }
        finally { _choosingFolder = false; }
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Vm.Busy || _choosingFolder) return;
        _choosingFolder = true;
        try
        {
            if (await UiHelpers.PickFolderAsync() is { } folder) Vm.OpenPackage(folder);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao abrir o pacote de migração.");
            Vm.MessageIsError = true;
            Vm.Message = ex.Message;
        }
        finally { _choosingFolder = false; }
    }

    private async void OnRestoreAll(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Vm.Busy) return;
            if (await UiHelpers.ConfirmAsync(this, L.Get("migration.restoreAll"), L.Get("migration.restoreAllConfirm"), L.Get("migration.restoreAll")))
                await Vm.RestoreAllAsync();
        }
        catch (Exception ex)
        {
            Vm.Message = ex.Message;
            Vm.MessageIsError = true;
        }
    }
}
