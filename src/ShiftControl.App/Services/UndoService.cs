using Microsoft.Extensions.Logging;
using ShiftControl.Setup;
using ShiftControl.Storage;

namespace ShiftControl.App.Services;

/// <summary>
/// Executa a reversão de uma alteração registrada na Central de desfazer, conforme o tipo.
/// Cada módulo que altera o sistema registra o tipo que sabe desfazer.
/// </summary>
public sealed class UndoService
{
    public const string WinGetUninstall = "winget.uninstall";
    public const string StartupToggle = "startup.toggle";

    private readonly ChangeJournal _journal;
    private readonly WinGetClient _winget;
    private readonly ILogger<UndoService> _log;
    private readonly IServiceProvider _services;

    public UndoService(ChangeJournal journal, WinGetClient winget, ILogger<UndoService> log, IServiceProvider services)
    {
        _services = services;
        _journal = journal;
        _winget = winget;
        _log = log;
    }

    public ChangeJournal Journal => _journal;

    public async Task<(bool Ok, string Message)> RevertAsync(ChangeEntry entry, CancellationToken ct = default)
    {
        if (!entry.CanRevert) return (false, L.Get("undo.notRevertible"));
        try
        {
            (bool ok, string message) = entry.UndoKind switch
            {
                WinGetUninstall => await UninstallAsync(entry.UndoData!, ct),
                GameBoostService.UndoKind => ((GameBoostService)_services.GetService(typeof(GameBoostService))!).RevertEntry(entry),
                StartupToggle => RevertStartup(entry.UndoData!),
                _ => (false, L.F("undo.unknownKind", entry.UndoKind)),
            };
            _journal.SetState(entry.Id, ok ? ChangeState.Reverted : ChangeState.RevertFailed, message);
            _log.LogInformation("Reversão de {Id} ({Kind}): {Result}.", entry.Id, entry.UndoKind, message);
            return (ok, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Falha ao reverter {Id}.", entry.Id);
            _journal.SetState(entry.Id, ChangeState.RevertFailed, ex.Message);
            return (false, ex.Message);
        }
    }

    /// <summary>Dados: chave do item + "|" + estado anterior (1 habilitado, 0 desabilitado).</summary>
    private static (bool, string) RevertStartup(string data)
    {
        int sep = data.LastIndexOf('|');
        string key = data[..sep];
        bool wasEnabled = data[(sep + 1)..] == "1";
        var item = Maintenance.Startup.Find(key);
        if (item is null) return (true, L.Get("undo.startupGone"));
        return Maintenance.Startup.SetEnabled(item, wasEnabled)
            ? (true, L.Get(wasEnabled ? "undo.startupEnabled" : "undo.startupDisabled"))
            : (false, L.Get("startup.needsAdmin"));
    }

    private async Task<(bool, string)> UninstallAsync(string id, CancellationToken ct)
    {
        var (outcome, result) = await _winget.UninstallAsync(id, null, ct);
        return outcome switch
        {
            WinGetOutcome.Success => (true, L.Get("undo.uninstalled")),
            WinGetOutcome.NotFound => (true, L.Get("undo.alreadyRemoved")),
            WinGetOutcome.RebootRequired => (true, L.Get("undo.uninstalledReboot")),
            _ => (false, L.F("formatKit.outcome." + outcome) + " (" + WinGetCodes.Hex(result.ExitCode) + ")"),
        };
    }
}
