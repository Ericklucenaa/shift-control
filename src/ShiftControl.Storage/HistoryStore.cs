using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShiftControl.Core;

namespace ShiftControl.Storage;

/// <summary>
/// Histórico em dois níveis:
/// <list type="bullet">
/// <item><c>samples_raw</c>: cada amostra, mantida por poucas horas (padrão 2 h).</item>
/// <item><c>samples_minute</c>: média/mín/máx por minuto, mantida por dias (padrão 30).</item>
/// </list>
/// As amostras ficam em memória e são gravadas em lote (<see cref="Flush"/>), uma transação por lote.
/// </summary>
public sealed class HistoryStore : IHistoryStore
{
    private const long MinuteMs = 60_000;

    private readonly ShiftDatabase _db;
    private readonly ILogger _log;
    private readonly Dictionary<string, long> _metricIds = new(StringComparer.Ordinal);
    private readonly Lock _pendingGate = new();
    private List<(string Key, long Ts, double V)> _pending = new(1024);

    public HistoryStore(ShiftDatabase db, ILogger<HistoryStore>? log = null)
    {
        _db = db;
        _log = log ?? NullLogger<HistoryStore>.Instance;
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT id, key FROM metrics";
            using var r = cmd.ExecuteReader();
            while (r.Read()) _metricIds[r.GetString(1)] = r.GetInt64(0);
        }
    }

    public TimeSpan RawRetention { get; set; } = TimeSpan.FromHours(2);
    public TimeSpan AggregatedRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Limite de segurança: se o disco travar, a fila em memória não cresce sem fim.</summary>
    public int MaxPending { get; init; } = 50_000;

    public int PendingCount
    {
        get { lock (_pendingGate) return _pending.Count; }
    }

    public void Append(DateTimeOffset time, IEnumerable<KeyValuePair<string, double>> values)
    {
        long ts = time.ToUnixTimeMilliseconds();
        lock (_pendingGate)
        {
            foreach (var (key, v) in values)
            {
                if (!double.IsFinite(v)) continue;
                if (_pending.Count >= MaxPending) _pending.RemoveAt(0);
                _pending.Add((key, ts, v));
            }
        }
    }

    public void Flush()
    {
        List<(string Key, long Ts, double V)> batch;
        lock (_pendingGate)
        {
            if (_pending.Count == 0) return;
            batch = _pending;
            _pending = new List<(string, long, double)>(Math.Max(1024, batch.Count));
        }

        // Agregados por minuto calculados em memória, depois mesclados com o que já existe no banco.
        var minutes = new Dictionary<(long Id, long Minute), (double Sum, double Min, double Max, long N)>();

        lock (_db.Gate)
        {
            try
            {
                using var tx = _db.Connection.BeginTransaction();
                using var insert = _db.Connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO samples_raw(metric_id, ts, v) VALUES ($m, $t, $v)";
                var pm = insert.Parameters.Add("$m", SqliteType.Integer);
                var pt = insert.Parameters.Add("$t", SqliteType.Integer);
                var pv = insert.Parameters.Add("$v", SqliteType.Real);
                insert.Prepare();

                foreach (var (key, ts, v) in batch)
                {
                    long id = MetricId(key, tx);
                    pm.Value = id;
                    pt.Value = ts;
                    pv.Value = v;
                    insert.ExecuteNonQuery();

                    var bucket = (id, ts - ts % MinuteMs);
                    minutes[bucket] = minutes.TryGetValue(bucket, out var a)
                        ? (a.Sum + v, Math.Min(a.Min, v), Math.Max(a.Max, v), a.N + 1)
                        : (v, v, v, 1);
                }

                using var upsert = _db.Connection.CreateCommand();
                upsert.Transaction = tx;
                upsert.CommandText = """
                    INSERT INTO samples_minute(metric_id, ts, avg, min, max, n) VALUES ($m, $t, $a, $lo, $hi, $n)
                    ON CONFLICT(metric_id, ts) DO UPDATE SET
                        avg = (avg * n + excluded.avg * excluded.n) / (n + excluded.n),
                        min = MIN(min, excluded.min),
                        max = MAX(max, excluded.max),
                        n   = n + excluded.n
                    """;
                var um = upsert.Parameters.Add("$m", SqliteType.Integer);
                var ut = upsert.Parameters.Add("$t", SqliteType.Integer);
                var ua = upsert.Parameters.Add("$a", SqliteType.Real);
                var ulo = upsert.Parameters.Add("$lo", SqliteType.Real);
                var uhi = upsert.Parameters.Add("$hi", SqliteType.Real);
                var un = upsert.Parameters.Add("$n", SqliteType.Integer);
                upsert.Prepare();
                foreach (var ((id, minute), agg) in minutes)
                {
                    um.Value = id;
                    ut.Value = minute;
                    ua.Value = agg.Sum / agg.N;
                    ulo.Value = agg.Min;
                    uhi.Value = agg.Max;
                    un.Value = agg.N;
                    upsert.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch (SqliteException ex)
            {
                _log.LogError(ex, "Falha ao gravar {Count} amostras de histórico; o lote foi descartado.", batch.Count);
            }
        }
    }

    private long MetricId(string key, SqliteTransaction tx)
    {
        if (_metricIds.TryGetValue(key, out var id)) return id;
        using var cmd = _db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO metrics(key) VALUES ($k) ON CONFLICT(key) DO UPDATE SET key = key RETURNING id";
        cmd.Parameters.AddWithValue("$k", key);
        id = (long)cmd.ExecuteScalar()!;
        _metricIds[key] = id;
        return id;
    }

    public IReadOnlyList<HistoryPoint> Query(string metric, DateTimeOffset from, DateTimeOffset to, int maxPoints)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 1);
        if (to <= from) return [];
        Flush();

        long fromMs = from.ToUnixTimeMilliseconds();
        long toMs = to.ToUnixTimeMilliseconds();
        long span = toMs - fromMs;

        lock (_db.Gate)
        {
            if (!_metricIds.TryGetValue(metric, out var id)) return [];

            // Usa as amostras brutas quando o período cabe na retenção delas; senão, os agregados por minuto.
            bool useRaw = span <= RawRetention.TotalMilliseconds
                          && fromMs >= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)RawRetention.TotalMilliseconds - MinuteMs;

            using var cmd = _db.Connection.CreateCommand();
            long bucket;
            if (useRaw)
            {
                bucket = Math.Max(1, span / maxPoints);
                cmd.CommandText = """
                    SELECT (ts / $b) * $b AS b, AVG(v), MIN(v), MAX(v)
                    FROM samples_raw WHERE metric_id = $m AND ts >= $f AND ts <= $to
                    GROUP BY b ORDER BY b
                    """;
            }
            else
            {
                bucket = Math.Max(MinuteMs, (span / maxPoints + MinuteMs - 1) / MinuteMs * MinuteMs);
                cmd.CommandText = """
                    SELECT (ts / $b) * $b AS b, SUM(avg * n) / SUM(n), MIN(min), MAX(max)
                    FROM samples_minute WHERE metric_id = $m AND ts >= $f AND ts <= $to
                    GROUP BY b ORDER BY b
                    """;
            }
            cmd.Parameters.AddWithValue("$b", bucket);
            cmd.Parameters.AddWithValue("$m", id);
            cmd.Parameters.AddWithValue("$f", fromMs);
            cmd.Parameters.AddWithValue("$to", toMs);

            var points = new List<HistoryPoint>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                points.Add(new HistoryPoint(
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)),
                    r.GetDouble(1), r.GetDouble(2), r.GetDouble(3)));
            }
            return points;
        }
    }

    public int ApplyRetention(DateTimeOffset now)
    {
        long nowMs = now.ToUnixTimeMilliseconds();
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM samples_raw WHERE ts < $r; DELETE FROM samples_minute WHERE ts < $a;";
            cmd.Parameters.AddWithValue("$r", nowMs - (long)RawRetention.TotalMilliseconds);
            cmd.Parameters.AddWithValue("$a", nowMs - (long)AggregatedRetention.TotalMilliseconds);
            int removed = cmd.ExecuteNonQuery();
            if (removed > 0) _log.LogDebug("Retenção do histórico removeu {Rows} linhas.", removed);
            return removed;
        }
    }

    public long DatabaseSizeBytes
    {
        get
        {
            lock (_db.Gate)
                return _db.Scalar<long>("PRAGMA page_count;") * _db.Scalar<long>("PRAGMA page_size;");
        }
    }

    public void Clear()
    {
        lock (_pendingGate) _pending.Clear();
        lock (_db.Gate)
        {
            _db.Exec("DELETE FROM samples_raw; DELETE FROM samples_minute;");
            _db.Exec("VACUUM;");
        }
    }

    public void Dispose() => Flush();
}
