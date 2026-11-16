using Microsoft.Extensions.Logging;
using ShiftControl.Maintenance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.Overlay;
using ShiftControl.App.ViewModels;
using Microsoft.Win32;

namespace ShiftControl.App.Services;

public enum UpdateCheck { UpToDate, Available, NoReleases, Failed }

public sealed class UpdateService(HttpClient http, ILogger<UpdateService> log) : IDisposable
{
    public const string Repository = "Ericklucenaa/shift-control";
    private readonly ReleaseClient _client = new(http, Repository);
    public ReleaseUpdate? Latest { get; private set; }
    private DispatcherQueueTimer? _timer;
    private readonly CancellationTokenSource _stop = new();
    private bool _checking;
    private bool _initialized;
    public bool Installing { get; private set; }

    public void Initialize(DispatcherQueue ui)
    {
        if (_initialized) return;
        _initialized = true;
        // Isolated development/UI test instances must never install a public release.
        if (Environment.GetCommandLineArgs().Any(a => a.StartsWith("--data-dir=", StringComparison.Ordinal))) return;
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(20);
        _timer.IsRepeating = false;
        _timer.Tick += async (_, _) =>
        {
            _timer.Stop();
            if (_checking || Installing || !App.Services.GetRequiredService<SettingsService>().Current.CheckUpdates) return;
            _checking = true;
            try
            {
                if (await CheckAsync(_stop.Token) != UpdateCheck.Available || Latest is not { } update) return;
                var settings = App.Services.GetRequiredService<SettingsService>().Current;
                if (settings.AutoInstallUpdates && update.Sha256 is not null && update.InstallerUrl is not null)
                    await InstallAsync(update, automatic: true, ct: _stop.Token);
                else App.Services.GetRequiredService<TrayService>().Notify(L.Get("update.availableTitle"), L.F("update.availableText", update.Version), false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogWarning(ex, "Startup update check failed."); }
            finally { _checking = false; }
        };
        _timer.Start();
    }

    private static bool CanApplyAutomatically()
    {
        var services = App.Services;
        var settings = services.GetRequiredService<SettingsService>().Current;
        var overlay = services.GetRequiredService<OverlayService>();
        var page = App.Window?.Content as Microsoft.UI.Xaml.FrameworkElement;
        bool dialog = page?.XamlRoot is { } root && VisualTreeHelperDialogs(root);
        bool busy = services.GetRequiredService<FormatKitViewModel>().Running
            || (services.GetRequiredService<ChecksViewModel>().Loading || services.GetRequiredService<ChecksViewModel>().CheckingSlow)
            || services.GetRequiredService<DriversViewModel>().Busy
            || services.GetRequiredService<MigrationViewModel>().Busy
            || services.GetRequiredService<CleanupViewModel>().Busy
            || services.GetRequiredService<StartupViewModel>().Busy
            || services.GetRequiredService<RepairViewModel>().Busy
            || services.GetRequiredService<NetworkViewModel>().Busy
            || (services.GetRequiredService<UndoViewModel>().CreatingPoint || services.GetRequiredService<UndoViewModel>().Changes.Any(c => c.Busy))
            || services.GetRequiredService<TaskManagerViewModel>().Ending;
        return UpdateInstallation.CanApply(settings.CheckUpdates && settings.AutoInstallUpdates,
            overlay.Target is not null || overlay.SessionStart is not null || services.GetRequiredService<GameBoostService>().IsActive, busy, dialog);
    }

    private static bool VisualTreeHelperDialogs(Microsoft.UI.Xaml.XamlRoot root) =>
        Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0;

    public async Task<bool> InstallAsync(ReleaseUpdate update, bool automatic = false, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (Installing) return false;
        Installing = true;
        string? installer = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            installer = await _client.DownloadAsync(update, Path.Combine(AppPaths.DataDirectory, "updates"), progress, timeout.Token);
            if (automatic)
            {
                while (!CanApplyAutomatically())
                {
                    if (!App.Services.GetRequiredService<SettingsService>().Current.AutoInstallUpdates || !App.Services.GetRequiredService<SettingsService>().Current.CheckUpdates) return false;
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
                App.Services.GetRequiredService<TrayService>().Notify(L.Get("update.availableTitle"), L.F("update.autoRestart", update.Version), false);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                // A game or operation might have started during the notification.
                if (!CanApplyAutomatically()) return false;
            }
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ShiftControl");
            bool installed = string.Equals(key?.GetValue("InstallLocation") as string, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            var start = UpdateInstallation.StartInfo(installer, AppContext.BaseDirectory, installed);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Installer did not start.");
            log.LogInformation("Installing update {Version} (automatic={Automatic}).", update.Version, automatic);
            installer = null; // The running installer owns its file until the next app launch.
            App.ExitApp();
            return true;
        }
        finally
        {
            Installing = false;
            if (installer is not null) try { File.Delete(installer); } catch (IOException) { }
        }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _stop.Cancel();
    }

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        Latest = null;
        try
        {
            var result = await _client.CheckAsync(AppPaths.Version, ct);
            Latest = result.Update;
            return result.Status switch
            {
                ReleaseStatus.Available => UpdateCheck.Available,
                ReleaseStatus.UpToDate => UpdateCheck.UpToDate,
                _ => UpdateCheck.NoReleases,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        {
            log.LogInformation("Falha na consulta de atualizações: {Error}", ex.Message);
            return UpdateCheck.Failed;
        }
    }

    public Task<string> DownloadAsync(IProgress<double>? progress = null, CancellationToken ct = default) =>
        _client.DownloadAsync(Latest ?? throw new InvalidOperationException("Consulte atualizações primeiro."), Path.Combine(AppPaths.DataDirectory, "updates"), progress, ct);

    public static bool IsNewer(string candidate, string current) => ReleaseClient.IsNewer(candidate, current);
}
