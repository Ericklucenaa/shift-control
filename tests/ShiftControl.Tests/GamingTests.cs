using ShiftControl.Overlay;
using ShiftControl.Storage;

namespace ShiftControl.Tests;

public class FrameStatisticsTests
{
    private static FrameStatistics Feed(IEnumerable<double> frametimes)
    {
        var s = new FrameStatistics();
        foreach (var ft in frametimes) s.Add(new FrameSample(ft));
        return s;
    }

    [Fact]
    public void Constant_60_fps()
    {
        var snap = Feed(Enumerable.Repeat(1000.0 / 60, 600)).Snapshot();
        Assert.Equal(600, snap.Frames);
        Assert.Equal(60, snap.AverageFps!.Value, 1);
        Assert.Equal(60, snap.InstantFps!.Value, 1);
        Assert.Equal(60, snap.MinimumFps!.Value, 1);
        Assert.Equal(60, snap.MaximumFps!.Value, 1);
        Assert.Equal(60, snap.OnePercentLowFps!.Value, 0); // resolução do histograma: 0,05 ms
        Assert.Null(snap.PointOnePercentLowFps); // < 1000 frames
        Assert.Equal(0, snap.Stutters);
        Assert.Equal(10, snap.CapturedTime.TotalSeconds, 2);
    }

    [Fact]
    public void Average_is_frames_over_time_not_mean_of_fps()
    {
        // 50 frames de 10 ms (100 FPS) + 50 de 30 ms (33 FPS): 100 frames em 2 s = 50 FPS (e não 66,7).
        var snap = Feed(Enumerable.Repeat(10.0, 50).Concat(Enumerable.Repeat(30.0, 50))).Snapshot();
        Assert.Equal(50, snap.AverageFps!.Value, 6);
    }

    [Fact]
    public void One_percent_low_reflects_the_slowest_frames()
    {
        // 990 frames de 10 ms e 10 frames de 50 ms: os piores 1% (10 frames) têm média 50 ms → 20 FPS.
        var frames = Enumerable.Repeat(10.0, 990).Concat(Enumerable.Repeat(50.0, 10)).ToList();
        var snap = Feed(frames).Snapshot();
        Assert.Equal(20, snap.OnePercentLowFps!.Value, 0);
        Assert.Equal(20, snap.PointOnePercentLowFps!.Value, 0); // 0,1% de 1000 = 1 frame de 50 ms
        Assert.True(snap.AverageFps > 90);
    }

    [Fact]
    public void Stutter_requires_a_spike_far_above_the_median()
    {
        var frames = Enumerable.Repeat(8.0, 100).Append(40.0).Concat(Enumerable.Repeat(8.0, 50)).Append(12.0).ToList();
        var snap = Feed(frames).Snapshot();
        Assert.Equal(1, snap.Stutters); // 40 ms conta; 12 ms (1,5× a mediana) não
        Assert.NotNull(snap.SinceLastStutter);
    }

    [Fact]
    public void Invalid_frames_are_ignored_and_reset_clears()
    {
        var s = Feed([0, -5, double.NaN, double.PositiveInfinity, 9000, 16.6]);
        Assert.Equal(1, s.Frames);
        s.Reset();
        Assert.Same(FrameStatsSnapshot.Empty, s.Snapshot());
    }

    [Fact]
    public void Recent_frametimes_are_copied_without_allocating_a_new_buffer()
    {
        var s = Feed(Enumerable.Range(1, 10).Select(i => (double)i));
        var buffer = new double[4];
        Assert.Equal(4, s.CopyRecent(buffer));
        Assert.Equal([7, 8, 9, 10], buffer);
    }

    [Fact]
    public async Task Concurrent_writer_and_readers_are_safe()
    {
        var s = new FrameStatistics();
        var token = TestContext.Current.CancellationToken;
        var writer = Task.Run(() => { for (int i = 0; i < 50_000; i++) s.Add(new FrameSample(5 + i % 10)); }, token);
        var readers = Enumerable.Range(0, 3).Select(r => Task.Run(() =>
        {
            var buffer = new double[240];
            while (!writer.IsCompleted) { _ = s.Snapshot(); s.CopyRecent(buffer); }
        }, token)).ToArray();
        await Task.WhenAll([writer, .. readers]);
        Assert.Equal(50_000, s.Frames);
    }
}

public class PresentMonCsvTests
{
    // Cabeçalho no formato do PresentMon 2.6 (métricas padrão).
    private const string Header =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,MsPCLatency,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime,MsFlipDelay,DisplayLatency,DisplayedTime";

    [Fact]
    public void Parses_a_frame_by_column_names()
    {
        var csv = new PresentMonCsv();
        Assert.True(csv.TryReadHeader(Header));
        string line = "game.exe,4242,0x000001F2A3B4C5D0,DXGI,1,0,0,Hardware: Independent Flip,1234.5,NA,6.944,6.950,0.12,1.2,3.4,NA,6.9,3.0,3.9,0.5,4.1,4.0,0.1,NA,NA,NA,8.1,6.95";
        Assert.True(csv.TryParse(line, out var r));
        Assert.Equal("game.exe", r.Application);
        Assert.Equal(4242, r.ProcessId);
        Assert.Equal(0x1F2A3B4C5D0UL, r.SwapChain);
        Assert.Equal("DXGI", r.Runtime);
        Assert.Equal("Hardware: Independent Flip", r.PresentMode);
        Assert.Equal(6.944, r.FrameTimeMs, 6);
        Assert.Equal(6.950, r.DisplayedIntervalMs!.Value, 6);
    }

    [Fact]
    public void Not_displayed_frames_have_no_display_interval()
    {
        var csv = new PresentMonCsv();
        csv.TryReadHeader(Header);
        string line = "game.exe,1,0x1,VULKAN,0,0,1,Composed: Flip,0,NA,4.2,NA,0.1,NA,NA,NA,4.2,2,2.2,NA,NA,NA,NA,NA,NA,NA,NA,NA";
        Assert.True(csv.TryParse(line, out var r));
        Assert.Null(r.DisplayedIntervalMs);
        Assert.Equal("VULKAN", r.Runtime);
    }

    [Fact]
    public void Falls_back_to_cpu_busy_plus_wait_when_needed()
    {
        var csv = new PresentMonCsv();
        Assert.True(csv.TryReadHeader("Application,ProcessID,SwapChainAddress,PresentRuntime,PresentMode,MsCPUBusy,MsCPUWait"));
        Assert.True(csv.TryParse("a.exe,7,0x2,DXGI,Composed: Flip,3.5,6.5", out var r));
        Assert.Equal(10, r.FrameTimeMs, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("game.exe,notanumber,0x1,DXGI,1,0,0,Composed: Flip,0,NA,6,6,0,0,0,NA,6,3,3,0,0,0,0,NA,NA,NA,0,0")]
    [InlineData("game.exe,1,0x1,DXGI,1,0,0,Composed: Flip,0,NA,NA,6,0,0,0,NA,6,NA,NA,0,0,0,0,NA,NA,NA,0,0")]
    [InlineData("game.exe,1")]
    public void Rejects_invalid_lines(string line)
    {
        var csv = new PresentMonCsv();
        csv.TryReadHeader(Header);
        Assert.False(csv.TryParse(line, out _));
    }

    [Fact]
    public void Header_without_required_columns_is_refused()
    {
        var csv = new PresentMonCsv();
        Assert.False(csv.TryReadHeader("Application,Something"));
        Assert.False(csv.TryParse("x,1", out _));
        Assert.False(csv.TryReadHeader("warning: PresentMon requires elevated privilege"));
    }
}

public class FrameRouterTests
{
    private sealed class Clock
    {
        public long Now = 10_000;
    }

    private static PresentRecord Frame(int pid, string exe = "game.exe", ulong swap = 1, double ft = 10, string mode = "Composed: Flip") =>
        new(exe, pid, swap, "DXGI", mode, ft, ft);

    [Fact]
    public void Only_target_frames_reach_the_statistics()
    {
        var clock = new Clock();
        var router = new FrameRouter(() => clock.Now);
        router.SetTarget(100);
        for (int i = 0; i < 50; i++)
        {
            router.OnPresent(Frame(100));
            router.OnPresent(Frame(200, "chrome.exe"));
        }
        Assert.Equal(50, router.Statistics.Frames);
    }

    [Fact]
    public void Measures_rate_per_process_for_detection()
    {
        var clock = new Clock();
        var router = new FrameRouter(() => clock.Now);
        for (int second = 0; second < 2; second++)
        {
            for (int i = 0; i < 144; i++) { router.OnPresent(Frame(1, "game.exe")); clock.Now += 1000 / 144; }
            clock.Now = (clock.Now / 1000 + 1) * 1000; // vira o segundo
        }
        var p = Assert.Single(router.Presenting());
        Assert.Equal("game.exe", p.Executable);
        Assert.InRange(p.Fps, 140, 145);

        clock.Now += 10_000; // processo parou de renderizar
        Assert.Empty(router.Presenting());
    }

    [Fact]
    public void Secondary_swap_chain_of_the_same_process_is_ignored()
    {
        var clock = new Clock();
        var router = new FrameRouter(() => clock.Now);
        router.SetTarget(5);
        // Primeiro segundo: cadeia 0xA domina (60 frames) e uma cadeia de overlay de terceiros (0xB) apresenta 2.
        for (int i = 0; i < 60; i++) router.OnPresent(Frame(5, swap: 0xA));
        for (int i = 0; i < 2; i++) router.OnPresent(Frame(5, swap: 0xB));
        clock.Now += 1000;
        long before = router.Statistics.Frames;
        for (int i = 0; i < 30; i++) router.OnPresent(Frame(5, swap: 0xA));
        for (int i = 0; i < 30; i++) router.OnPresent(Frame(5, swap: 0xB));
        Assert.Equal(before + 30, router.Statistics.Frames);
    }

    [Fact]
    public void Changing_target_starts_new_statistics()
    {
        var router = new FrameRouter();
        router.SetTarget(1);
        for (int i = 0; i < 10; i++) router.OnPresent(Frame(1));
        router.SetTarget(2);
        Assert.Equal(0, router.Statistics.Frames);
    }

    [Fact]
    public void Exclusive_fullscreen_is_recognized()
    {
        Assert.True(new PresentingProcess(1, "g.exe", 60, "DXGI", "Hardware: Legacy Flip").IsExclusiveFullscreen);
        Assert.False(new PresentingProcess(1, "g.exe", 60, "DXGI", "Hardware: Independent Flip").IsExclusiveFullscreen);
    }
}

public class GameTargetPolicyTests
{
    private static PresentingProcess P(int pid, string exe, double fps = 120) => new(pid, exe, fps, "DXGI", "Composed: Flip");

    private static TargetContext Ctx(int? fg, IReadOnlyList<PresentingProcess> presenting, string? manual = null, bool auto = true,
        int? current = null, bool alive = true, params string[] exclusions) =>
        new(fg, presenting, manual, auto, current, alive, exclusions);

    [Fact]
    public void Foreground_game_is_chosen()
    {
        var d = GameTargetPolicy.Decide(Ctx(10, [P(10, "Game.exe"), P(20, "chrome.exe")]));
        Assert.Equal(10, d.Target!.ProcessId);
        Assert.Equal(TargetReason.Foreground, d.Reason);
    }

    [Fact]
    public void Launchers_and_browsers_in_foreground_are_not_games()
    {
        Assert.Null(GameTargetPolicy.Decide(Ctx(20, [P(20, "steamwebhelper.exe")])).Target);
        Assert.Null(GameTargetPolicy.Decide(Ctx(21, [P(21, "chrome.exe")])).Target);
        Assert.Null(GameTargetPolicy.Decide(Ctx(22, [P(22, "MyLauncher.exe")], exclusions: "mylauncher")).Target);
    }

    [Fact]
    public void Idle_foreground_app_is_not_a_game()
    {
        Assert.Null(GameTargetPolicy.Decide(Ctx(10, [P(10, "notepad.exe", fps: 2)])).Target);
    }

    [Fact]
    public void Game_is_kept_after_alt_tab_while_it_keeps_running()
    {
        var d = GameTargetPolicy.Decide(Ctx(99, [P(10, "Game.exe")], current: 10, alive: true));
        Assert.Equal(10, d.Target!.ProcessId);
        Assert.Equal(TargetReason.Kept, d.Reason);
        Assert.Null(GameTargetPolicy.Decide(Ctx(99, [P(10, "Game.exe")], current: 10, alive: false)).Target);
    }

    [Fact]
    public void Switching_to_another_game_follows_the_foreground()
    {
        var d = GameTargetPolicy.Decide(Ctx(30, [P(10, "OldGame.exe"), P(30, "NewGame.exe")], current: 10));
        Assert.Equal(30, d.Target!.ProcessId);
    }

    [Fact]
    public void Manual_executable_overrides_detection()
    {
        var presenting = new[] { P(10, "Game.exe"), P(40, "Emulator.exe", fps: 60) };
        var d = GameTargetPolicy.Decide(Ctx(10, presenting, manual: "emulator"));
        Assert.Equal(40, d.Target!.ProcessId);
        Assert.Equal(TargetReason.Manual, d.Reason);
        Assert.Null(GameTargetPolicy.Decide(Ctx(10, [P(10, "Game.exe")], manual: "Emulator.exe")).Target);
    }

    [Fact]
    public void Auto_detect_off_keeps_only_current()
    {
        Assert.Null(GameTargetPolicy.Decide(Ctx(10, [P(10, "Game.exe")], auto: false)).Target);
        Assert.Equal(10, GameTargetPolicy.Decide(Ctx(10, [P(10, "Game.exe")], auto: false, current: 10)).Target!.ProcessId);
    }
}

public class LatencyStatsTests
{
    [Fact]
    public void Average_jitter_and_loss()
    {
        var s = new LatencyStats(10);
        foreach (var v in new double?[] { 20, 24, null, 22, 30 }) s.Add(v);
        var snap = s.Snapshot("1.1.1.1");
        Assert.Equal(30, snap.LastMs);
        Assert.Equal(24, snap.AverageMs!.Value, 6);
        Assert.Equal((4 + 2 + 8) / 3.0, snap.JitterMs!.Value, 6); // 20→24→22→30 (perdas não contam)
        Assert.Equal(20, snap.LossPercent!.Value, 6);
        Assert.Equal(5, snap.Probes);
    }

    [Fact]
    public void Last_probe_lost_has_no_current_value()
    {
        var s = new LatencyStats();
        s.Add(15);
        s.Add(null);
        Assert.Null(s.Snapshot("x").LastMs);
        Assert.Null(new LatencyStats().Snapshot("x").AverageMs);
    }
}

public class SessionTests
{
    [Fact]
    public void Short_sessions_are_not_saved()
    {
        var start = DateTimeOffset.Now;
        var agg = new SessionAggregator("g.exe", "Game", start);
        var few = new FrameStatsSnapshot(100, TimeSpan.FromSeconds(1), 60, 60, 60, 60, 60, null, 16, 16, 17, 60, 0, null);
        Assert.False(agg.IsWorthSaving(start.AddSeconds(10), few));
        var enough = few with { Frames = 5000 };
        Assert.True(agg.IsWorthSaving(start.AddMinutes(2), enough));
    }

    [Fact]
    public void Aggregates_hardware_and_frames()
    {
        var start = DateTimeOffset.Now;
        var agg = new SessionAggregator("g.exe", "Game", start);
        agg.AddSecond(start.AddSeconds(1), 100, 40, 90, 60, 70, 4000);
        agg.AddSecond(start.AddSeconds(2), 120, 50, 95, 64, 74, 5000);
        agg.AddSecond(start.AddSeconds(3), null, null, null, null, null, null); // sensor sumiu: não vira zero
        var frames = new FrameStatsSnapshot(9000, TimeSpan.FromMinutes(1), 110, 110, 90, 130, 80, 70, 9.1, 9, 14, 110, 3, null);
        var r = agg.Complete(start.AddMinutes(1), frames, "DXGI");
        Assert.Equal(45, r.CpuLoadAvg!.Value, 6);
        Assert.Equal(74, r.GpuTempMax);
        Assert.Equal(5000, r.VramMaxMb);
        Assert.Equal(3, r.Stutters);
        Assert.Equal(1000 / 110.0, r.AverageFrametimeMs!.Value, 6);
        Assert.Equal(3, agg.Points.Count);
        Assert.Null(agg.Points[2].Fps);
    }

    [Fact]
    public void Comparison_only_when_both_sessions_have_the_metric()
    {
        var a = Record(avgFps: 108, low1: 80, cpu: null);
        var b = Record(avgFps: 100, low1: null, cpu: 50);
        var c = SessionComparison.Compare(a, b).ToDictionary(x => x.Key);
        Assert.Equal(8, c["avgFps"].PercentChange!.Value, 6);
        Assert.Null(c["low1"].PercentChange);
        Assert.Null(c["cpuLoad"].PercentChange);
        Assert.False(c["p99"].HigherIsBetter);
    }

    internal static GameSessionRecord Record(double? avgFps = 100, double? low1 = 80, double? cpu = 50, string exe = "g.exe", DateTimeOffset? start = null)
    {
        var s = start ?? DateTimeOffset.UtcNow.AddHours(-1);
        return new GameSessionRecord(0, exe, "Game", s, s.AddMinutes(10), 60_000, avgFps, low1, null, avgFps is { } f ? 1000 / f : null,
            14, 2, cpu, 95, 60, 70, 65, 75, 6000, "DXGI");
    }

    [Fact]
    public void Store_round_trip_with_points_and_migration_from_v1()
    {
        using var dir = new TempDir();
        string path = dir.File("s.db");
        // Banco criado pela versão anterior (schema 1) é migrado sem perder dados.
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE settings(key TEXT PRIMARY KEY, json TEXT NOT NULL, updated_at INTEGER NOT NULL); INSERT INTO settings VALUES ('app','{\"theme\":\"Light\"}',0); PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }
        using var db = new ShiftDatabase(path);
        Assert.Equal(Core.ThemeMode.Light, new SettingsStore(db).Load().Theme);

        var store = new GameSessionStore(db);
        long id1 = store.Save(Record(start: DateTimeOffset.UtcNow.AddHours(-2)), [new SessionPoint(0, 60, 40, 90, 60, 70), new SessionPoint(1, null, 41, 91, 61, 71)]);
        long id2 = store.Save(Record(avgFps: 120, exe: "other.exe"), []);
        Assert.NotEqual(id1, id2);

        var all = store.List();
        Assert.Equal(2, all.Count);
        Assert.Equal("other.exe", all[0].Executable); // mais recente primeiro
        var mine = Assert.Single(store.List("G.EXE"));
        Assert.Equal(100, mine.AverageFps);
        Assert.Null(mine.PointOnePercentLowFps);
        var points = store.Points(id1);
        Assert.Equal(2, points.Count);
        Assert.Null(points[1].Fps);
        Assert.Equal(["other.exe", "g.exe"], store.Games());

        store.Delete(id1);
        Assert.Empty(store.Points(id1));
        Assert.Single(store.List());
    }
}
