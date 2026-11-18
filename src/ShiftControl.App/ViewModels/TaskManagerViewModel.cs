using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ShiftControl.App.Services;
using ShiftControl.Maintenance;

namespace ShiftControl.App.ViewModels;

public sealed partial class TaskProcessVm : ObservableObject
{
    public TaskProcessVm(string key, TaskProcess snapshot, IReadOnlyList<TaskProcess> members)
    {
        Key = key;
        Members = members;
        Snapshot = snapshot;
    }
    /// <summary>Linha de um processo ou de um grupo (todos os processos com o mesmo nome).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(PidText), nameof(CpuText), nameof(MemoryText), nameof(PrivateText), nameof(ThreadsText), nameof(StateText), nameof(CanEnd))]
    public partial TaskProcess Snapshot { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Count), nameof(IsGroup), nameof(DisplayName), nameof(PidText), nameof(CanEnd))]
    public partial IReadOnlyList<TaskProcess> Members { get; set; }
    public string Key { get; }
    public int Count => Members.Count;
    public bool IsGroup => Members.Count > 1;
    public string Name => Snapshot.Name;
    public string DisplayName => IsGroup ? $"{Name} ({Count.ToString(L.Culture)})" : Name;
    public string PidText => IsGroup ? "—" : Snapshot.Pid.ToString(L.Culture);
    public string CpuText => Snapshot.CpuPercent is { } cpu ? Fmt.Number(cpu, 1) + "%" : "—";
    public string MemoryText => Snapshot.MemoryBytes is { } bytes ? Fmt.Bytes(bytes) : "—";
    public string PrivateText => Snapshot.PrivateBytes is { } bytes ? Fmt.Bytes(bytes) : "—";
    public string ThreadsText => Snapshot.Threads?.ToString(L.Culture) ?? "—";
    public string StateText => L.Get(Snapshot.NotResponding ? "tasks.state.hung" : Snapshot.HasWindow ? "tasks.state.app" : "tasks.state.background");
    public bool CanEnd => IsGroup ? Members.Any(m => m.CanEnd) : Snapshot.CanEnd;
    public void Update(TaskProcess snapshot, IReadOnlyList<TaskProcess> members)
    {
        Members = members;
        Snapshot = snapshot;
    }
    public override string ToString() => IsGroup ? DisplayName : $"{Name} · PID {PidText}";
}

public sealed partial class TaskManagerViewModel : ToolViewModel, IDisposable
{
    private readonly SnapshotHub _hub;
    private readonly ILogger<TaskManagerViewModel> _log;
    private readonly ProcessSampler _sampler = new();
    private readonly Dictionary<string, TaskProcessVm> _rows = [];
    private IReadOnlyList<TaskProcess> _all = [];
    private DispatcherQueueTimer? _timer;
    private bool _active, _refreshing, _resetNext = true;
    private int _epoch, _detailRequest;

    public TaskManagerViewModel(SnapshotHub hub, ILogger<TaskManagerViewModel> log)
    {
        _hub = hub;
        _log = log;
        SortOptions = Enum.GetValues<ProcessSort>().Select(s => new Choice<ProcessSort>(s, L.Get("tasks.sort." + s))).ToList();
        Sort = SortOptions[0];
    }
    public ObservableCollection<TaskProcessVm> Processes { get; } = [];
    public IReadOnlyList<Choice<ProcessSort>> SortOptions { get; }
    [ObservableProperty] public partial Choice<ProcessSort>? Sort { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial bool AppsOnly { get; set; }
    /// <summary>Uma linha por programa (ex.: todos os chrome.exe juntos); encerrar fecha todos eles.</summary>
    [ObservableProperty] public partial bool GroupByName { get; set; } = true;
    [ObservableProperty] public partial bool Paused { get; set; }
    [ObservableProperty] public partial bool Loaded { get; set; }
    [ObservableProperty] public partial bool Empty { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEnd))] public partial bool Ending { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanEnd), nameof(SelectionHint))] public partial TaskProcessVm? SelectedProcess { get; set; }
    [ObservableProperty] public partial string CpuTotal { get; set; } = "—";
    [ObservableProperty] public partial string MemoryTotal { get; set; } = "—";
    [ObservableProperty] public partial string ProcessCount { get; set; } = "—";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string UpdatedAt { get; set; } = "";
    [ObservableProperty] public partial string ExecutablePath { get; set; } = "";
    [ObservableProperty] public partial string Publisher { get; set; } = "";
    [ObservableProperty] public partial string StartedAt { get; set; } = "";
    public bool HasSelection => SelectedProcess is not null;
    public bool CanEnd => !Ending && SelectedProcess?.CanEnd == true;
    public string SelectionHint => L.Get(SelectedProcess is null ? "tasks.selectToEnd" : SelectedProcess.CanEnd ? "tasks.selectionHint" : "tasks.protectedHint");

    partial void OnSearchChanged(string value) => Filter();
    partial void OnAppsOnlyChanged(bool value) => Filter();
    partial void OnGroupByNameChanged(bool value) => Filter();
    partial void OnSortChanged(Choice<ProcessSort>? value) => Filter();
    partial void OnPausedChanged(bool value)
    {
        _resetNext = true;
        if (value) { _epoch++; _timer?.Stop(); }
        else if (_active) { _timer?.Start(); _ = RefreshAsync(); }
    }
    partial void OnSelectedProcessChanged(TaskProcessVm? value) => _ = LoadDetailsAsync(value?.Snapshot);

    public void Start()
    {
        _active = true;
        _epoch++;
        _resetNext = true;
        if (_timer is null)
        {
            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += async (_, _) => { if (!Paused && _hub.IsWindowVisible) await RefreshAsync(); };
        }
        if (!Paused) _timer.Start();
        if (!Loaded || !Paused) _ = RefreshAsync();
    }
    public void Stop() { _active = false; _epoch++; _timer?.Stop(); }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!_active || _refreshing) return;
        _refreshing = true;
        Busy = !Loaded;
        int epoch = _epoch;
        bool reset = _resetNext;
        _resetNext = false;
        try
        {
            var data = await Task.Run(() => { if (reset) _sampler.Reset(); return _sampler.Sample(); });
            if (!_active || epoch != _epoch) return;
            _all = data;
            var s = _hub.Latest;
            CpuTotal = s?.Cpu.Load.Value is { } cpu ? Fmt.Percent(cpu) : "—";
            MemoryTotal = s?.Memory.TotalBytes > 0 ? Fmt.Percent(s.Memory.LoadPercent) : "—";
            ProcessCount = data.Count.ToString(L.Culture);
            UpdatedAt = L.F("tasks.updated", DateTime.Now.ToString("T", L.Culture));
            Loaded = true;
            Filter();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao atualizar os processos.");
            if (_active) Error(L.Get("tasks.loadError"));
        }
        finally { _refreshing = false; Busy = false; }
    }

    private void Filter()
    {
        if (!Loaded) return;
        string? selected = SelectedProcess?.Key;
        var sort = Sort?.Value ?? ProcessSort.Cpu;
        // Cada linha: chave estável, os números mostrados e os processos que ela representa.
        var visible = GroupByName
            ? TaskProcesses.QueryGroups(_all, Search, sort, AppsOnly).Select(g => g.Count > 1
                ? (Key: g.Key, Row: g.Summary(), Members: g.Members)
                : (Key: g.Members[0].Key, Row: g.Members[0], Members: g.Members)).ToList()
            : TaskProcesses.Query(_all, Search, sort, AppsOnly).Select(p => (Key: p.Key, Row: p, Members: (IReadOnlyList<TaskProcess>)new[] { p })).ToList();
        var keep = new HashSet<string>();
        for (int i = 0; i < visible.Count; i++)
        {
            var (key, snapshot, members) = visible[i];
            keep.Add(key);
            if (_rows.TryGetValue(key, out var row)) row.Update(snapshot, members);
            else _rows[key] = row = new TaskProcessVm(key, snapshot, members);
            if (i < Processes.Count && ReferenceEquals(Processes[i], row)) continue;
            int old = Processes.IndexOf(row);
            if (old >= 0) Processes.Move(old, i); else Processes.Insert(i, row);
        }
        while (Processes.Count > visible.Count) Processes.RemoveAt(Processes.Count - 1);
        foreach (var key in _rows.Keys.Where(k => !keep.Contains(k)).ToList()) _rows.Remove(key);
        SelectedProcess = selected is null ? null : Processes.FirstOrDefault(p => p.Key == selected);
        Empty = Loaded && Processes.Count == 0;
        Summary = GroupByName
            ? L.F("tasks.summaryGroups", Processes.Count, visible.Sum(v => v.Members.Count), _all.Count)
            : L.F("tasks.summary", Processes.Count, _all.Count);
        OnPropertyChanged(nameof(CanEnd));
        OnPropertyChanged(nameof(SelectionHint));
    }

    private async Task LoadDetailsAsync(TaskProcess? item)
    {
        int request = ++_detailRequest;
        ExecutablePath = Publisher = StartedAt = "";
        if (item is null) return;
        try
        {
            var detail = await Task.Run(() => TaskProcesses.Details(item));
            if (request != _detailRequest) return;
            ExecutablePath = detail?.Path ?? L.Get("tasks.pathUnavailable");
            Publisher = string.IsNullOrWhiteSpace(detail?.Publisher) ? L.Get("tasks.unknownPublisher") : detail.Publisher;
            StartedAt = detail?.StartedAt?.LocalDateTime.ToString("g", L.Culture) ?? "—";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha nos detalhes do processo {Pid}.", item.Pid);
            if (request == _detailRequest) ExecutablePath = L.Get("tasks.pathUnavailable");
        }
    }

    public async Task EndAsync(TaskProcess item)
    {
        if (Ending) return;
        Ending = true;
        try
        {
            var result = await Task.Run(() => TaskProcesses.End(item));
            string message = L.F("tasks.end." + result, item.Name, item.Pid);
            if (result is EndProcessResult.Ended or EndProcessResult.Ending or EndProcessResult.AlreadyExited) Info(message); else Error(message);
            _log.LogInformation("Encerramento solicitado pela interface: PID {Pid}, resultado {Result}.", item.Pid, result);
        }
        catch (Exception ex) { _log.LogError(ex, "Falha ao encerrar PID {Pid}.", item.Pid); Error(L.Get("tasks.end.Failed")); }
        finally { Ending = false; }
        await RefreshAsync();
    }

    /// <summary>Encerra todos os processos com este nome. Usa uma amostra nova (não a lista da tela, que pode estar
    /// alguns segundos atrás) e faz uma segunda passada para os que ainda estavam saindo, sem tocar em processos
    /// abertos depois do clique.</summary>
    public async Task EndGroupAsync(string name)
    {
        if (Ending) return;
        Ending = true;
        try
        {
            long started = DateTime.UtcNow.ToFileTimeUtc();
            var result = await Task.Run(() =>
            {
                var results = TaskProcesses.EndAll(name, new ProcessSampler().Sample()).ToList();
                Thread.Sleep(600);
                var leftover = new ProcessSampler().Sample().Where(p => p.CreationFileTime < started);
                results.AddRange(TaskProcesses.EndAll(name, leftover));
                return TaskProcesses.Summarize(name, results);
            });
            string key = result.Total == 0 ? "tasks.endGroup.AlreadyExited"
                : result.Blocked == 0 ? "tasks.endGroup.Done"
                : result.Ended == 0 ? "tasks.endGroup.None" : "tasks.endGroup.Partial";
            string message = L.F(key, name, result.Ended, result.Blocked);
            if (result.Blocked == 0) Info(message); else Error(message);
            _log.LogInformation("Encerramento em grupo pela interface: {Name}, {Ended} encerrados, {Blocked} bloqueados.", name, result.Ended, result.Blocked);
        }
        catch (Exception ex) { _log.LogError(ex, "Falha ao encerrar o grupo {Name}.", name); Error(L.Get("tasks.end.Failed")); }
        finally { Ending = false; }
        await RefreshAsync();
    }

    [RelayCommand]
    private void Reveal()
    {
        if (!File.Exists(ExecutablePath)) { Error(L.Get("tasks.pathUnavailable")); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{ExecutablePath}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { _log.LogWarning(ex, "Falha ao abrir a localização do processo."); Error(L.Get("tasks.pathUnavailable")); }
    }
    public void Dispose() => Stop();
}
