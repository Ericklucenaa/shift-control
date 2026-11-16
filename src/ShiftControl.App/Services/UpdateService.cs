using Microsoft.Extensions.Logging;
using ShiftControl.Maintenance;
using Microsoft.Win32;

namespace ShiftControl.App.Services;

public enum UpdateCheck { UpToDate, Available, NoReleases, Failed }

public sealed class UpdateService(HttpClient http, ILogger<UpdateService> log)
{
    public const string Repository = "Ericklucenaa/shift-control";
    private readonly ReleaseClient _client = new(http, Repository);
    public ReleaseUpdate? Latest { get; private set; }
    public bool Installing { get; private set; }

    public async Task<bool> InstallAsync(ReleaseUpdate update, bool automatic = false, IProgress<double>? progress = null, CancellationToken ct = default, bool exitApp = true, Action? installing = null)
    {
        if (Installing) return false;
        Installing = true;
        string? installer = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            installer = await _client.DownloadAsync(update, Path.Combine(AppPaths.DataDirectory, "updates"), progress, timeout.Token);
            installing?.Invoke();
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ShiftControl");
            bool installed = string.Equals(key?.GetValue("InstallLocation") as string, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            var start = UpdateInstallation.StartInfo(installer, AppContext.BaseDirectory, installed, visible: !exitApp);
            System.Diagnostics.Process? process;
            // Trava e reconfere o arquivo até o processo iniciar: entre o download e este ponto,
            // um processo sem privilégio poderia trocá-lo e ganhar a elevação deste app.
            using (ReleaseClient.OpenVerified(installer, update.Sha256!))
                process = System.Diagnostics.Process.Start(start);
            using var _ = process ?? throw new IOException("Installer did not start.");
            log.LogInformation("Installing update {Version} (automatic={Automatic}).", update.Version, automatic);
            installer = null; // The running installer owns its file until the next app launch.
            if (exitApp) App.ExitApp();
            return true;
        }
        finally
        {
            Installing = false;
            if (installer is not null) try { File.Delete(installer); } catch (IOException) { }
        }
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
