using Microsoft.Data.Sqlite;
using ShiftControl.Core;
using ShiftControl.Storage;

namespace ShiftControl.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Defaults_when_empty_and_round_trip()
    {
        using var dir = new TempDir();
        using (var db = new ShiftDatabase(dir.File("a.db")))
        {
            var store = new SettingsStore(db);
            var s = store.Load();
            Assert.Equal(ThemeMode.Dark, s.Theme);
            s.Theme = ThemeMode.Light;
            s.FavoriteSensors.Add("/gpu/0/temperature/0");
            s.LastAnalysisLevel = HealthLevel.Attention;
            store.Save(s);
        }
        // Reabre o arquivo: as preferências persistem entre sessões.
        using (var db = new ShiftDatabase(dir.File("a.db")))
        {
            var s = new SettingsStore(db).Load();
            Assert.Equal(ThemeMode.Light, s.Theme);
            Assert.Equal(["/gpu/0/temperature/0"], s.FavoriteSensors);
            Assert.Equal(HealthLevel.Attention, s.LastAnalysisLevel);
        }
    }

    [Fact]
    public void Corrupted_json_falls_back_to_defaults()
    {
        using var dir = new TempDir();
        using var db = new ShiftDatabase(dir.File("b.db"));
        using (var cmd = new SqliteCommand("INSERT INTO settings(key, json, updated_at) VALUES ('app', '{not json', 0)", GetConnection(db)))
            cmd.ExecuteNonQuery();
        var s = new SettingsStore(db).Load();
        Assert.Equal(1000, s.SampleIntervalMs);
    }

    [Fact]
    public void Saved_values_are_normalized()
    {
        using var dir = new TempDir();
        using var db = new ShiftDatabase(dir.File("c.db"));
        var store = new SettingsStore(db);
        store.Save(new AppSettings { SampleIntervalMs = 3 });
        Assert.Contains(store.Load().SampleIntervalMs, AppSettings.AllowedIntervalsMs);
    }

    [Fact]
    public void Newer_schema_is_refused_instead_of_corrupted()
    {
        using var dir = new TempDir();
        string path = dir.File("d.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = new SqliteCommand("PRAGMA user_version = 99", c);
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new ShiftDatabase(path));
    }

    private static SqliteConnection GetConnection(ShiftDatabase db) =>
        (SqliteConnection)typeof(ShiftDatabase).GetProperty("Connection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(db)!;
}

public class HistoryStoreTests
{
    private static KeyValuePair<string, double> P(string k, double v) => new(k, v);

    [Fact]
    public void Raw_query_returns_recent_samples()
    {
        using var dir = new TempDir();
        using var db = new ShiftDatabase(dir.File("h.db"));
        using var h = new HistoryStore(db);
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 60; i++) h.Append(now.AddSeconds(-60 + i), [P("cpu.load", i)]);

        var points = h.Query("cpu.load", now.AddSeconds(-61), now, 1000);
        Assert.Equal(60, points.Count);
        Assert.Equal(0, points[0].Avg);
        Assert.Equal(59, points[^1].Avg);
    }

    [Fact]
    public void Buckets_aggregate_avg_min_max()
    {
        using var dir = new TempDir();
        using var db = new ShiftDatabase(dir.File("h.db"));
        using var h = new HistoryStore(db);
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 100; i++) h.Append(now.AddSeconds(-100 + i), [P("m", i)]);

        var points = h.Query("m", now.AddSeconds(-100), now, 10);
        Assert.InRange(points.Count, 10, 11);
        Assert.All(points, p => Assert.True(p.Min <= p.Avg && p.Avg <= p.Max));
        Assert.Equal(0, points.Min(p => p.Min));
        Assert.Equal(99, points.Max(p => p.Max));
    }

    [Fact]
    public void Long_periods_use_minute_aggregates_and_merge_batches()
    {
        using var dir = new TempDir();
        using var db = new ShiftDatabase(dir.File("h.db"));
        using var h = new HistoryStore(db) { RawRetention = TimeSpan.FromHours(1) };
        var minute = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60_000 * 60_000).AddHours(-3);

        // Dois lotes no mesmo minuto: o agregado deve combinar média ponderada, mínimo e máximo.
        h.Append(minute.AddSeconds(1), [P("t", 10)]);
        h.Append(minute.AddSeconds(2), [P("t", 20)]);
        h.Flush();
        h.Append(minute.AddSeconds(3), [P("t", 60)]);
        h.Flush();

        var points = h.Query("t", minute.AddHours(-1), DateTimeOffset.UtcNow, 100);
        var p = Assert.Single(points);
        Assert.Equal(30, p.Avg, 6);
        Assert.Equal(10, p.Min);
        Assert.Equal(60, p.Max);
    }

    [Fact]
    public void Retention_removes_old_rows()
    {
        using var dir = new TempDir();
        using var db = new ShiftDatabase(dir.File("h.db"));
        using var h = new HistoryStore(db) { RawRetention = TimeSpan.FromHours(1), AggregatedRetention = TimeSpan.FromDays(1) };
        var now = DateTimeOffset.UtcNow;
        h.Append(now.AddDays(-3), [P("x", 1)]);
        h.Append(now.AddHours(-2), [P("x", 2)]);
        h.Append(now, [P("x", 3)]);
        h.Flush();

        int removed = h.ApplyRetention(now);
        Assert.Equal(3, removed); // 2 brutas antigas + 1 agregado de 3 dias atrás
        Assert.Single(h.Query("x", now.AddMinutes(-5), now.AddSeconds(1), 100));
        // Agregados: sobram o minuto de 2 h atrás e o atual (o de 3 dias saiu).
        Assert.Equal(2, h.Query("x", now.AddDays(-7), now.AddSeconds(1), 100).Count);
    }

    [Fact]
    public void Non_finite_values_are_not_stored_and_unknown_metric_is_empty()
    {
        using var dir = new TempDir();
        using var db = new ShiftDatabase(dir.File("h.db"));
        using var h = new HistoryStore(db);
        var now = DateTimeOffset.UtcNow;
        h.Append(now, [P("y", double.NaN), P("y", double.PositiveInfinity)]);
        Assert.Equal(0, h.PendingCount);
        Assert.Empty(h.Query("nunca-gravada", now.AddMinutes(-1), now, 10));
        Assert.Empty(h.Query("y", now, now.AddSeconds(-1), 10)); // intervalo invertido
    }

    [Fact]
    public void Pending_queue_is_bounded()
    {
        using var dir = new TempDir();
        using var db = new ShiftDatabase(dir.File("h.db"));
        using var h = new HistoryStore(db) { MaxPending = 100 };
        for (int i = 0; i < 500; i++) h.Append(DateTimeOffset.UtcNow, [P("z", i)]);
        Assert.Equal(100, h.PendingCount);
    }

    [Fact]
    public void Clear_empties_history_and_data_survives_reopen()
    {
        using var dir = new TempDir();
        string path = dir.File("h.db");
        var now = DateTimeOffset.UtcNow;
        using (var db = new ShiftDatabase(path))
        using (var h = new HistoryStore(db))
            h.Append(now, [P("k", 1)]); // Dispose grava o lote pendente

        using (var db = new ShiftDatabase(path))
        using (var h = new HistoryStore(db))
        {
            Assert.Single(h.Query("k", now.AddMinutes(-1), now.AddSeconds(1), 10));
            Assert.True(h.DatabaseSizeBytes > 0);
            h.Clear();
            Assert.Empty(h.Query("k", now.AddMinutes(-1), now.AddSeconds(1), 10));
        }
    }
}
