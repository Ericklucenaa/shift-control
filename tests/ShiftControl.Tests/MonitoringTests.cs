using System.Diagnostics;
using ShiftControl.Core;
using ShiftControl.Monitoring;
using ShiftControl.Storage;

namespace ShiftControl.Tests;

public class MonitoringEngineTests
{
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condição não atingida.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Publishes_snapshots_on_a_background_thread()
    {
        var source = new FakeHardwareSource();
        using var engine = new MonitoringEngine(source) { Interval = TimeSpan.FromMilliseconds(250) };
        int callerThread = Environment.CurrentManagedThreadId;
        int? eventThread = null;
        engine.SnapshotReady += (_, _) => eventThread ??= Environment.CurrentManagedThreadId;

        engine.Start();
        await WaitUntil(() => engine.SampleCount >= 2);

        Assert.Equal(EngineState.Running, engine.State);
        Assert.NotNull(engine.Latest);
        Assert.NotNull(engine.Info);
        Assert.NotEqual(callerThread, eventThread);
        Assert.True(engine.Live.Count >= 2);
    }

    [Fact]
    public void Failed_sample_is_skipped_and_collection_recovers()
    {
        int calls = 0;
        var source = new FakeHardwareSource(seq => ++calls == 2 ? throw new IOException("sensor sumiu") : Snapshots.Basic(seq));
        using var engine = new MonitoringEngine(source);
        engine.Tick(1);
        engine.Tick(2);
        Assert.Equal(1, engine.ConsecutiveFailures);
        engine.Tick(3);
        Assert.Equal(0, engine.ConsecutiveFailures);
        Assert.Equal(2, engine.SampleCount);
        Assert.Equal(3, engine.Latest!.Sequence);
    }

    [Fact]
    public void Throwing_subscriber_does_not_break_other_subscribers()
    {
        using var engine = new MonitoringEngine(new FakeHardwareSource());
        int received = 0;
        engine.SnapshotReady += (_, _) => throw new InvalidOperationException("bug na tela");
        engine.SnapshotReady += (_, _) => received++;
        engine.Tick(1);
        Assert.Equal(1, received);
    }

    [Fact]
    public async Task Initialization_failure_is_reported_not_thrown()
    {
        var source = new FakeHardwareSource { InitializeError = new UnauthorizedAccessException("sem acesso") };
        using var engine = new MonitoringEngine(source);
        engine.Start();
        await WaitUntil(() => engine.State == EngineState.Faulted);
        Assert.IsType<UnauthorizedAccessException>(engine.FaultReason);
        Assert.Equal(0, source.Samples);
    }

    [Fact]
    public async Task Dispose_interrupts_a_long_wait_and_releases_the_source()
    {
        var source = new FakeHardwareSource();
        var engine = new MonitoringEngine(source) { Interval = TimeSpan.FromMinutes(10) };
        engine.Start();
        await WaitUntil(() => engine.SampleCount >= 1);

        var sw = Stopwatch.StartNew();
        engine.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Dispose levou {sw.ElapsedMilliseconds} ms");
        Assert.True(source.Disposed);
        engine.Dispose(); // idempotente
    }

    [Fact]
    public void Background_mode_slows_collection_to_at_least_two_seconds()
    {
        using var engine = new MonitoringEngine(new FakeHardwareSource()) { Interval = TimeSpan.FromMilliseconds(500) };
        Assert.Equal(TimeSpan.FromMilliseconds(500), engine.EffectiveInterval);
        engine.Background = true;
        Assert.Equal(MonitoringEngine.BackgroundMinimumInterval, engine.EffectiveInterval);
        engine.Interval = TimeSpan.FromSeconds(5);
        Assert.Equal(TimeSpan.FromSeconds(5), engine.EffectiveInterval);
    }

    [Fact]
    public void History_receives_samples_only_when_recording()
    {
        using var dir = new TempDir();
        using var db = new ShiftDatabase(dir.File("m.db"));
        using var history = new HistoryStore(db);
        using var engine = new MonitoringEngine(new FakeHardwareSource(), history);

        engine.Tick(1);
        Assert.True(history.PendingCount > 0);
        int before = history.PendingCount;
        engine.RecordHistory = false;
        engine.Tick(2);
        Assert.Equal(before, history.PendingCount);
    }

    [Fact]
    public async Task Readers_on_other_threads_see_consistent_live_history()
    {
        var source = new FakeHardwareSource(seq => Snapshots.Basic(seq, cpuLoad: seq % 100));
        using var engine = new MonitoringEngine(source, liveCapacity: 50) { Interval = TimeSpan.FromMilliseconds(250) };
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int samples = 0;
        engine.SnapshotReady += (_, _) => { if (Interlocked.Increment(ref samples) >= 2) sampled.TrySetResult(); };
        engine.Start();
        using var cts = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(async () =>
        {
            var buffer = new double[50];
            while (!cts.IsCancellationRequested)
            {
                int n = engine.Live.Copy(MetricKeys.CpuLoad, buffer);
                Assert.InRange(n, 0, 50);
                _ = engine.Live.RecentAverage(MetricKeys.CpuLoad, 10);
                // Allow the below-normal collector to run on busy/single-core CI hosts.
                await Task.Delay(1);
            }
        })).ToArray();
        try { await sampled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        finally { cts.Cancel(); await Task.WhenAll(readers); }
        Assert.True(engine.SampleCount >= 2);
    }

    /// <summary>Custo do próprio motor (sem hardware): referência para não regredir.</summary>
    [Fact]
    public void Engine_overhead_per_tick_is_small()
    {
        var snapshot = Snapshots.Basic();
        using var engine = new MonitoringEngine(new FakeHardwareSource(_ => snapshot));
        for (int i = 0; i < 100; i++) engine.Tick(i); // aquecimento
        var sw = Stopwatch.StartNew();
        const int ticks = 5000;
        for (int i = 0; i < ticks; i++) engine.Tick(i);
        double perTickUs = sw.Elapsed.TotalMilliseconds * 1000 / ticks;
        Assert.True(perTickUs < 200, $"{perTickUs:F1} µs por rodada");
    }
}

public class LiveHistoryTests
{
    [Fact]
    public void Missing_metrics_become_NaN_and_are_ignored_by_averages()
    {
        var live = new LiveHistory(10);
        live.Push(Snapshots.Basic(cpuLoad: 10));
        live.Push(Snapshots.Basic(cpuLoad: 30));
        var temps = live.Snapshot(MetricKeys.CpuTemp);
        Assert.All(temps, t => Assert.True(double.IsNaN(t)));
        Assert.Null(live.RecentAverage(MetricKeys.CpuTemp, 10));
        Assert.Equal(20, live.RecentAverage(MetricKeys.CpuLoad, 10));
        Assert.Equal(30, live.RecentAverage(MetricKeys.CpuLoad, 1));
    }

    [Fact]
    public void Unknown_series_is_empty()
    {
        var live = new LiveHistory(5);
        Assert.Empty(live.Snapshot("nao.existe"));
        Assert.Null(live.RecentAverage("nao.existe", 5));
    }
}

public class SensorExportTests
{
    private static SensorExportRow Row(string name, double? v = 1.5) =>
        new("Hw, \"A\"", "Cpu", name, "Load", "%", v, v is null ? "RequiresAdmin" : null, 1, 2, 1.5, 3);

    [Fact]
    public void Csv_escapes_commas_quotes_and_formulas()
    {
        var csv = SensorExport.ToCsv([Row("=cmd|' /C calc'!A0"), Row("Normal", null)]);
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("\"Hw, \"\"A\"\"\",Cpu,'=cmd", lines[1]);
        Assert.Contains(",RequiresAdmin,", lines[2]);
        Assert.Contains("1.5", lines[1]); // ponto decimal invariante
    }

    [Fact]
    public void Json_contains_all_rows()
    {
        var json = SensorExport.ToJson([Row("a"), Row("b")], DateTimeOffset.UnixEpoch);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("sensors").GetArrayLength());
    }

    [Fact]
    public void Rows_include_session_statistics()
    {
        var stats = new SensorStatsTracker();
        var snap = Snapshots.Basic(cpuLoad: 40);
        stats.Update(snap.Sensors);
        var rows = SensorExport.Rows(snap.Sensors, stats);
        var load = rows.Single(r => r.Sensor == "CPU Total");
        Assert.Equal(40, load.Average);
        var temp = rows.Single(r => r.Sensor == "Core");
        Assert.Null(temp.Value);
        Assert.Equal("RequiresAdmin", temp.Unavailable);
    }
}
