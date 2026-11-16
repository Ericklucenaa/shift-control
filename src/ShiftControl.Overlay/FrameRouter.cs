namespace ShiftControl.Overlay;

/// <summary>Processo que está apresentando frames agora.</summary>
public sealed record PresentingProcess(int ProcessId, string Executable, double Fps, string Runtime, string PresentMode)
{
    /// <summary>Modos "Hardware: Legacy" indicam tela cheia exclusiva: o overlay em janela não fica visível por cima.</summary>
    public bool IsExclusiveFullscreen => PresentMode.StartsWith("Hardware: Legacy", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Recebe todos os Present() do sistema (um único leitor), mede a taxa de cada processo para a detecção de jogos
/// e encaminha às estatísticas apenas os frames da cadeia de troca principal do jogo alvo.
/// Um jogo pode ter várias cadeias (launcher embutido, overlays de terceiros): vale a com mais frames no último segundo.
/// </summary>
public sealed class FrameRouter
{
    private const long BucketMs = 1000;
    private const long StaleMs = 2500;

    private sealed class Activity
    {
        public string Executable = "";
        public string Runtime = "";
        public string Mode = "";
        public long Bucket;
        public int CurrentCount;
        public int PreviousCount;
        public long LastSeen;
        public readonly Dictionary<ulong, int> SwapCounts = [];
        public ulong DominantSwap;
    }

    private readonly Dictionary<int, Activity> _activity = [];
    private readonly Lock _gate = new();
    private readonly Func<long> _clockMs;
    private int? _targetPid;

    public FrameRouter(Func<long>? clockMs = null)
    {
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    public FrameStatistics Statistics { get; } = new();

    public int? TargetPid
    {
        get { lock (_gate) return _targetPid; }
    }

    public string? TargetRuntime
    {
        get { lock (_gate) return _targetPid is { } p && _activity.TryGetValue(p, out var a) ? a.Runtime : null; }
    }

    public string? TargetPresentMode
    {
        get { lock (_gate) return _targetPid is { } p && _activity.TryGetValue(p, out var a) ? a.Mode : null; }
    }

    /// <summary>Troca o processo alvo. As estatísticas recomeçam (cada jogo tem sua própria sessão).</summary>
    public void SetTarget(int? pid)
    {
        lock (_gate)
        {
            if (_targetPid == pid) return;
            _targetPid = pid;
            Statistics.Reset();
        }
    }

    public void OnPresent(in PresentRecord r)
    {
        long now = _clockMs();
        bool forward;
        lock (_gate)
        {
            if (!_activity.TryGetValue(r.ProcessId, out var a))
            {
                a = new Activity { Bucket = now / BucketMs, DominantSwap = r.SwapChain };
                _activity[r.ProcessId] = a;
            }
            a.Executable = r.Application;
            a.Runtime = r.Runtime;
            a.Mode = r.PresentMode;
            a.LastSeen = now;

            long bucket = now / BucketMs;
            if (bucket != a.Bucket)
            {
                a.PreviousCount = bucket == a.Bucket + 1 ? a.CurrentCount : 0;
                a.CurrentCount = 0;
                a.Bucket = bucket;
                if (a.SwapCounts.Count > 0)
                {
                    a.DominantSwap = a.SwapCounts.MaxBy(kv => kv.Value).Key;
                    a.SwapCounts.Clear();
                }
            }
            a.CurrentCount++;
            a.SwapCounts[r.SwapChain] = a.SwapCounts.TryGetValue(r.SwapChain, out int c) ? c + 1 : 1;

            forward = _targetPid == r.ProcessId && r.SwapChain == a.DominantSwap;
        }
        if (forward) Statistics.Add(new FrameSample(r.FrameTimeMs, r.DisplayedIntervalMs));
    }

    /// <summary>Processos que apresentaram frames nos últimos ~2,5 s, com a taxa do último segundo completo.</summary>
    public IReadOnlyList<PresentingProcess> Presenting()
    {
        long now = _clockMs();
        var list = new List<PresentingProcess>();
        lock (_gate)
        {
            foreach (var (pid, a) in _activity.ToList())
            {
                if (now - a.LastSeen > StaleMs)
                {
                    if (now - a.LastSeen > 60_000) _activity.Remove(pid); // limpa processos encerrados
                    continue;
                }
                long bucket = now / BucketMs;
                int lastSecond = bucket == a.Bucket ? a.PreviousCount : bucket == a.Bucket + 1 ? a.CurrentCount : 0;
                list.Add(new PresentingProcess(pid, a.Executable, lastSecond, a.Runtime, a.Mode));
            }
        }
        return list;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _activity.Clear();
            _targetPid = null;
            Statistics.Reset();
        }
    }
}
