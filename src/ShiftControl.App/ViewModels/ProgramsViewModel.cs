using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShiftControl.App.Services;
using ShiftControl.Maintenance;
using ShiftControl.Storage;

namespace ShiftControl.App.ViewModels;

public sealed class ProgramRowVm(InstalledProgram program)
{
    public InstalledProgram Program { get; } = program;
    public string Key => Program.Key;
    public string Name => Program.Name;
    public string PublisherText => string.IsNullOrWhiteSpace(Program.Publisher) ? L.Get("programs.unknownPublisher") : Program.Publisher!;
    public string VersionText => Program.Version ?? "—";
    public string SizeText => Program.SizeBytes is { } b ? Fmt.Bytes(b) : "—";
    /// <summary>Explica de onde veio o tamanho quando foi medido na pasta (o programa não informou).</summary>
    public string? SizeTip => Program.SizeMeasured ? L.Get("programs.sizeMeasured") : null;
    public string DateText => Program.InstalledOn?.ToString("d", L.Culture) ?? "—";
    public string DriveText { get; } = InstalledPrograms.DriveOf(program) ?? "—";
    public bool IsProtected => InstalledPrograms.IsProtected(Program);
    public override string ToString() => Name;
}

public sealed class LeftoverVm(Leftover item) : ObservableObject
{
    public Leftover Item { get; } = item;
    public bool Selected { get; set; } = true;
    public string Title => Item.Path;
    public string KindText => Item.Kind == LeftoverKind.Folder
        ? L.F("programs.leftover.folder", Fmt.Bytes(Item.Bytes))
        : L.Get("programs.leftover.registry");
}

public sealed partial class ProgramsViewModel : ToolViewModel
{
    private readonly ChangeJournal _journal;
    private readonly ILogger<ProgramsViewModel> _log;
    private IReadOnlyList<InstalledProgram> _all = [];
    private CancellationTokenSource? _wait;
    private bool _loading;

    public ProgramsViewModel(ChangeJournal journal, ILogger<ProgramsViewModel> log)
    {
        _journal = journal;
        _log = log;
        SortOptions = Enum.GetValues<ProgramSort>().Select(s => new Choice<ProgramSort>(s, L.Get("programs.sort." + s))).ToList();
        Sort = SortOptions[0];
    }

    public ObservableCollection<ProgramRowVm> Programs { get; } = [];
    public IReadOnlyList<Choice<ProgramSort>> SortOptions { get; }
    /// <summary>"Todos os discos" e cada disco que tem programas, com a contagem. Refeita a cada leitura.</summary>
    public ObservableCollection<Choice<string?>> DriveOptions { get; } = [];
    [ObservableProperty] public partial Choice<string?>? Drive { get; set; }
    private bool _rebuildingDrives;
    private CancellationTokenSource? _measure;
    [ObservableProperty] public partial bool MeasuringSizes { get; set; }
    [ObservableProperty] public partial Choice<ProgramSort>? Sort { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool Loaded { get; set; }
    [ObservableProperty] public partial bool Empty { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUninstall))] public partial bool Uninstalling { get; set; }
    [ObservableProperty] public partial string UninstallStatus { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanUninstall), nameof(SelectionHint), nameof(LocationText), nameof(HasLocation))]
    public partial ProgramRowVm? SelectedProgram { get; set; }

    public bool HasSelection => SelectedProgram is not null;
    public bool CanUninstall => !Uninstalling && SelectedProgram is { IsProtected: false };
    public string SelectionHint => L.Get(SelectedProgram is null ? "programs.selectHint" : SelectedProgram.IsProtected ? "programs.protectedHint" : "programs.selectionHint");
    public string LocationText => SelectedProgram?.Program.InstallLocation is { Length: > 0 } loc ? loc : L.Get("programs.noLocation");
    public bool HasLocation => SelectedProgram?.Program.InstallLocation is { Length: > 0 } loc && Directory.Exists(loc);

    partial void OnSearchChanged(string value) => Filter();
    partial void OnSortChanged(Choice<ProgramSort>? value) => Filter();
    partial void OnDriveChanged(Choice<string?>? value)
    {
        if (!_rebuildingDrives) Filter();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        Busy = !Loaded;
        try
        {
            _all = await Task.Run(InstalledPrograms.List);
            Loaded = true;
            RebuildDriveOptions();
            Filter();
            _ = MeasureMissingSizesAsync(_all);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao listar os programas instalados.");
            Error(L.Get("programs.loadError"));
        }
        finally { _loading = false; Busy = false; }
    }

    private void Filter()
    {
        if (!Loaded) return;
        string? selected = SelectedProgram?.Key;
        var visible = InstalledPrograms.Query(_all, Search, Sort?.Value ?? ProgramSort.Name, Drive?.Value);
        Programs.Clear();
        foreach (var p in visible) Programs.Add(new ProgramRowVm(p));
        SelectedProgram = selected is null ? null : Programs.FirstOrDefault(p => p.Key == selected);
        Empty = Programs.Count == 0;
        long total = _all.Sum(p => p.SizeBytes ?? 0);
        Summary = L.F("programs.summary", Programs.Count, _all.Count, Fmt.Bytes(total)) + (MeasuringSizes ? " · " + L.Get("programs.measuring") : "");
    }

    /// <summary>
    /// Muitos programas (jogos da Steam, drivers, ferramentas) não informam o tamanho ao Windows e ficariam no fim da
    /// ordenação "Maior tamanho". Mede a pasta deles em segundo plano; a lista já aparece antes e é atualizada no fim.
    /// </summary>
    private async Task MeasureMissingSizesAsync(IReadOnlyList<InstalledProgram> snapshot)
    {
        _measure?.Cancel();
        var cts = _measure = new CancellationTokenSource();
        if (!snapshot.Any(p => p.SizeBytes is null)) return;
        MeasuringSizes = true;
        Filter();
        try
        {
            var sizes = await Task.Run(() => InstalledPrograms.MeasureMissingSizesAsync(snapshot, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(snapshot, _all)) return; // a lista foi relida no meio
            _all = _all.Select(p => p.SizeBytes is null && sizes.TryGetValue(p.Key, out long size) ? p with { SizeBytes = size, SizeMeasured = true } : p).ToList();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log.LogWarning(ex, "Falha ao medir o tamanho das pastas dos programas."); }
        finally
        {
            if (ReferenceEquals(_measure, cts)) MeasuringSizes = false;
        }
        Filter();
    }

    /// <summary>Opções do filtro de disco a partir dos programas lidos; mantém o disco escolhido se ele ainda tiver programas.</summary>
    private void RebuildDriveOptions()
    {
        string? keep = Drive?.Value;
        var counts = _all.GroupBy(p => InstalledPrograms.DriveOf(p) ?? InstalledPrograms.UnknownDrive, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        _rebuildingDrives = true;
        try
        {
            DriveOptions.Clear();
            DriveOptions.Add(new Choice<string?>(null, L.Get("programs.drive.all")));
            foreach (var (drive, count) in counts.Where(c => c.Key != InstalledPrograms.UnknownDrive).OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            {
                string? label = VolumeLabel(drive);
                DriveOptions.Add(new Choice<string?>(drive, label is null
                    ? L.F("programs.drive.option", drive, count)
                    : L.F("programs.drive.optionLabel", drive, label, count)));
            }
            if (counts.TryGetValue(InstalledPrograms.UnknownDrive, out int unknown))
                DriveOptions.Add(new Choice<string?>(InstalledPrograms.UnknownDrive, L.F("programs.drive.unknown", unknown)));
            Drive = DriveOptions.FirstOrDefault(o => string.Equals(o.Value, keep, StringComparison.OrdinalIgnoreCase)) ?? DriveOptions[0];
        }
        finally { _rebuildingDrives = false; }
    }

    /// <summary>Nome do volume ("Jogos", "Dados"…). Discos desconectados ou sem nome ficam só com a letra.</summary>
    private static string? VolumeLabel(string drive)
    {
        try
        {
            var info = new DriveInfo(drive + "\\");
            return info.IsReady && !string.IsNullOrWhiteSpace(info.VolumeLabel) ? info.VolumeLabel : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Roda o desinstalador oficial e espera ele terminar. Não encerra o desinstalador se a espera for cancelada.</summary>
    public async Task<UninstallOutcome> UninstallAsync(InstalledProgram program, bool quiet)
    {
        Uninstalling = true;
        UninstallStatus = L.F(quiet ? "programs.runningQuiet" : "programs.running", program.Name);
        Message = null;
        _wait = new CancellationTokenSource();
        try
        {
            var outcome = await InstalledPrograms.UninstallAsync(program, quiet, _wait.Token);
            _log.LogInformation("Desinstalação de {Name} ({Key}): {Result}, código {Exit}.", program.Name, program.Key, outcome.Result, outcome.ExitCode);
            switch (outcome.Result)
            {
                case UninstallResult.Removed:
                    Info(L.F("programs.result.Removed", program.Name));
                    try
                    {
                        _journal.Record("programs", L.F("programs.journal.uninstalled", program.Name),
                            string.Join(" · ", new[] { program.Version, program.Publisher }.Where(s => !string.IsNullOrEmpty(s))), null, null);
                    }
                    catch (Exception ex) { _log.LogError(ex, "Falha ao registrar a desinstalação na Central de desfazer."); }
                    break;
                case UninstallResult.StillRunning or UninstallResult.StillInstalled or UninstallResult.UninstallerMissing:
                    Info(L.F("programs.result." + outcome.Result, program.Name));
                    break;
                default:
                    Error(L.F("programs.result." + outcome.Result, program.Name));
                    break;
            }
            return outcome;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao desinstalar {Name}.", program.Name);
            Error(L.F("programs.result.Failed", program.Name));
            return new(UninstallResult.Failed, null);
        }
        finally
        {
            Uninstalling = false;
            _wait.Dispose();
            _wait = null;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private void StopWaiting() => _wait?.Cancel();

    public async Task<IReadOnlyList<Leftover>> ScanLeftoversAsync(InstalledProgram program, bool orphan)
    {
        try
        {
            var extra = new[] { AppContext.BaseDirectory, AppPaths.DataDirectory, InstalledPrograms.BackupRootPath };
            var still = _all;
            var found = await Task.Run(() => InstalledPrograms.ScanLeftovers(program, still, LeftoverRoots.Default(extra)));
            return orphan ? [InstalledPrograms.OrphanEntry(program), .. found] : found;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao procurar sobras de {Name}.", program.Name);
            return [];
        }
    }

    public async Task RemoveLeftoversAsync(InstalledProgram program, IReadOnlyList<Leftover> items)
    {
        if (items.Count == 0) return;
        Busy = true;
        try
        {
            string backups = Path.Combine(BackupRoot(create: true), $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
            var result = await InstalledPrograms.RemoveLeftoversAsync(items, backups);
            _log.LogInformation("Sobras de {Name}: {Removed} removidas, {Failed} falhas, {Bytes} bytes.", program.Name, result.Removed, result.Failed, result.FreedBytes);
            if (result.Removed > 0)
                _journal.Record("programs", L.F("programs.journal.leftovers", program.Name), L.F("programs.journal.leftoversDetail", result.Removed, Fmt.Bytes(result.FreedBytes)),
                    result.RegistryBackups.Count > 0 ? UndoService.RegistryRestore : null,
                    result.RegistryBackups.Count > 0 ? string.Join('\n', result.RegistryBackups) : null);
            string text = L.F("programs.leftovers.done", result.Removed, Fmt.Bytes(result.FreedBytes));
            if (result.Failed > 0) Error(text + " " + L.F("programs.leftovers.failed", result.Failed)); else Info(text);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao remover sobras de {Name}.", program.Name);
            Error(L.Get("programs.leftovers.error"));
        }
        finally { Busy = false; await RefreshAsync(); }
    }

    /// <summary>
    /// Elevado: pasta protegida (só Administradores gravam), porque a restauração roda elevada. Sem elevação: pasta do
    /// usuário; nesse modo a restauração também roda sem elevação e só grava o que o usuário já poderia gravar.
    /// </summary>
    public static string BackupRoot(bool create) => Shell.IsElevated
        ? (create ? InstalledPrograms.ProtectedBackupRoot() : InstalledPrograms.BackupRootPath)
        : Path.Combine(AppPaths.DataDirectory, "uninstall-backups");

    [RelayCommand]
    private void OpenLocation()
    {
        if (!HasLocation) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{SelectedProgram!.Program.InstallLocation}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { _log.LogWarning(ex, "Falha ao abrir a pasta do programa."); }
    }
}
