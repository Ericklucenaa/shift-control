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
    /// <summary>Dados: caminhos dos backups .reg (um por linha) gravados ao remover sobras de um programa.</summary>
    public const string RegistryRestore = "registry.restore";

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
                RegistryRestore => await RestoreRegistryAsync(entry.UndoData!, ct),
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

    /// <summary>Reimporta só backups da pasta protegida e criados pelo app elevado (ver InstalledPrograms.RestoreRegistryAsync).</summary>
    private static async Task<(bool, string)> RestoreRegistryAsync(string data, CancellationToken ct)
    {
        var files = data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Elevado: só a pasta protegida e arquivos de Administradores. Sem elevação: a pasta do usuário (não há ganho de privilégio).
        bool elevated = Maintenance.Shell.IsElevated;
        var (restored, refused) = await Maintenance.InstalledPrograms.RestoreRegistryAsync(files, ViewModels.ProgramsViewModel.BackupRoot(create: false),
            elevated ? null : _ => true, ct);
        return refused == 0 ? (true, L.F("programs.undo.restored", restored)) : (false, L.F("programs.undo.refused", restored, refused));
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
