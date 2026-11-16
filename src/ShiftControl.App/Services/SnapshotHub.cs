using Microsoft.UI.Dispatching;
using ShiftControl.Core;
using ShiftControl.Monitoring;

namespace ShiftControl.App.Services;

/// <summary>
/// Ponte entre a thread de coleta e a interface. Guarda só o snapshot mais recente e agenda
/// no máximo um despacho por vez: se a UI estiver ocupada, amostras intermediárias são puladas
/// em vez de enfileiradas. Com a janela oculta, nada é despachado.
/// </summary>
public sealed class SnapshotHub
{
    private readonly MonitoringEngine _engine;
    private DispatcherQueue? _dispatcher;
    private SystemSnapshot? _pending;
    private int _scheduled;

    public SnapshotHub(MonitoringEngine engine)
    {
        _engine = engine;
        engine.SnapshotReady += OnSnapshot;
        engine.StateChanged += (_, state) => _dispatcher?.TryEnqueue(() => EngineStateChanged?.Invoke(this, state));
    }

    /// <summary>Disparado na thread da interface.</summary>
    public event EventHandler<SystemSnapshot>? Updated;

    public event EventHandler<EngineState>? EngineStateChanged;

    public bool IsWindowVisible { get; set; } = true;

    public SystemSnapshot? Latest => _engine.Latest;

    public void Attach(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    private void OnSnapshot(object? sender, SystemSnapshot snapshot)
    {
        Volatile.Write(ref _pending, snapshot);
        if (!IsWindowVisible || _dispatcher is null) return;
        if (Interlocked.Exchange(ref _scheduled, 1) == 1) return;
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, Deliver))
            Interlocked.Exchange(ref _scheduled, 0);
    }

    private void Deliver()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        var snapshot = Interlocked.Exchange(ref _pending, null);
        if (snapshot is not null) Updated?.Invoke(this, snapshot);
    }
}
