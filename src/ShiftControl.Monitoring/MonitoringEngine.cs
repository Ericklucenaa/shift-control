using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShiftControl.Core;

namespace ShiftControl.Monitoring;

public enum EngineState { Stopped, Starting, Running, Faulted }

/// <summary>
/// Coleta em uma thread própria, de prioridade abaixo do normal, desacoplada da interface.
/// A UI recebe <see cref="SnapshotReady"/> (na thread de coleta) e decide quando desenhar.
/// Falha numa amostra é registrada e a coleta continua na próxima.
/// </summary>
public sealed class MonitoringEngine : IDisposable
{
    public static readonly TimeSpan BackgroundMinimumInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RetentionEvery = TimeSpan.FromMinutes(10);

    private readonly IHardwareSource _source;
    private readonly IHistoryStore? _history;
    private readonly ILogger _log;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeProvider _time;
    private Thread? _thread;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private bool _background;
    private int _consecutiveFailures;

    public MonitoringEngine(IHardwareSource source, IHistoryStore? history = null, ILogger<MonitoringEngine>? log = null,
        int liveCapacity = 300, TimeProvider? time = null)
    {
        _source = source;
        _history = history;
        _log = log ?? NullLogger<MonitoringEngine>.Instance;
        _time = time ?? TimeProvider.System;
        Live = new LiveHistory(liveCapacity);
    }

    /// <summary>Disparado na thread de coleta. Assinantes não devem bloquear.</summary>
    public event EventHandler<SystemSnapshot>? SnapshotReady;

    public event EventHandler<EngineState>? StateChanged;

    public EngineState State { get; private set; } = EngineState.Stopped;
    public SystemInfo? Info { get; private set; }
    public SystemSnapshot? Latest { get; private set; }
    public LiveHistory Live { get; }
    public SensorStatsTracker Stats { get; } = new();
    public Exception? FaultReason { get; private set; }
    public bool RecordHistory { get; set; } = true;
    public long SampleCount { get; private set; }

    /// <summary>Intervalo de coleta pedido pelo usuário.</summary>
    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            _interval = value < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : value;
            _wake.Set();
        }
    }

    /// <summary>Com a janela oculta/minimizada, a coleta desacelera para no mínimo 2 s.</summary>
    public bool Background
    {
        get => _background;
        set
        {
            _background = value;
            _wake.Set();
        }
    }

    /// <summary>Algo visível fora da janela principal (o overlay) precisa dos dados no ritmo normal.</summary>
    public bool FullRateRequested
    {
        get => _fullRate;
        set
        {
            _fullRate = value;
            _wake.Set();
        }
    }

    private bool _fullRate;

    public TimeSpan EffectiveInterval =>
        _background && !_fullRate && _interval < BackgroundMinimumInterval ? BackgroundMinimumInterval : _interval;

    public void Start()
    {
        if (_thread is not null) throw new InvalidOperationException("O monitoramento já foi iniciado.");
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ShiftControl.Collector",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    private void SetState(EngineState state)
    {
        State = state;
        try { StateChanged?.Invoke(this, state); }
        catch (Exception ex) { _log.LogError(ex, "Assinante de StateChanged falhou."); }
    }

    private void Run()
    {
        var token = _cts.Token;
        SetState(EngineState.Starting);
        try
        {
            Info = _source.Initialize();
        }
        catch (Exception ex)
        {
            FaultReason = ex;
            _log.LogError(ex, "Não foi possível inicializar a coleta de hardware.");
            SetState(EngineState.Faulted);
            return;
        }
        SetState(EngineState.Running);

        var lastFlush = _time.GetTimestamp();
        var lastRetention = long.MinValue;
        long sequence = 0;

        while (!token.IsCancellationRequested)
        {
            long started = _time.GetTimestamp();
            Tick(++sequence);

            if (_history is not null)
            {
                try
                {
                    if (_time.GetElapsedTime(lastFlush) >= FlushEvery)
                    {
                        _history.Flush();
                        lastFlush = _time.GetTimestamp();
                    }
                    if (lastRetention == long.MinValue || _time.GetElapsedTime(lastRetention) >= RetentionEvery)
                    {
                        _history.ApplyRetention(_time.GetUtcNow());
                        lastRetention = _time.GetTimestamp();
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Falha na manutenção do histórico.");
                }
            }

            var remaining = EffectiveInterval - _time.GetElapsedTime(started);
            _wake.Reset();
            if (remaining > TimeSpan.Zero)
            {
                try { _wake.Wait(remaining, token); }
                catch (OperationCanceledException) { break; }
            }
        }

        try { _history?.Flush(); }
        catch (Exception ex) { _log.LogError(ex, "Falha ao gravar o histórico ao encerrar."); }
        SetState(EngineState.Stopped);
    }

    /// <summary>Uma rodada de coleta. Exposta para testes.</summary>
    internal void Tick(long sequence)
    {
        SystemSnapshot snapshot;
        try
        {
            snapshot = _source.Sample(sequence);
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            // Registra a primeira falha e depois uma a cada 60, para não inundar o log.
            if (_consecutiveFailures == 1 || _consecutiveFailures % 60 == 0)
                _log.LogError(ex, "Falha na coleta ({Count} seguidas).", _consecutiveFailures);
            return;
        }

        if (_consecutiveFailures > 0)
        {
            _log.LogInformation("Coleta normalizada após {Count} falhas.", _consecutiveFailures);
            _consecutiveFailures = 0;
        }

        Latest = snapshot;
        SampleCount++;
        Stats.Update(snapshot.Sensors);
        Live.Push(snapshot);
        if (RecordHistory) _history?.Append(snapshot.Time, MetricExtractor.Extract(snapshot));

        var handlers = SnapshotReady;
        if (handlers is null) return;
        foreach (var d in handlers.GetInvocationList())
        {
            try { ((EventHandler<SystemSnapshot>)d)(this, snapshot); }
            catch (Exception ex) { _log.LogError(ex, "Assinante de SnapshotReady falhou."); }
        }
    }

    public int ConsecutiveFailures => _consecutiveFailures;

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        _wake.Set();
        if (_thread is { IsAlive: true } t && !t.Join(TimeSpan.FromSeconds(5)))
            _log.LogWarning("A thread de coleta não terminou em 5 s.");
        try { _source.Dispose(); }
        catch (Exception ex) { _log.LogError(ex, "Falha ao liberar a fonte de hardware."); }
        _wake.Dispose();
        _cts.Dispose();
    }
}
