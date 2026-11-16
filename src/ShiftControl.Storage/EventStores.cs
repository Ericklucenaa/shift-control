namespace ShiftControl.Storage;

public sealed record AlertRecord(long Id, DateTimeOffset At, string Rule, string Level, double? Value, string? Subject);

/// <summary>Histórico de alertas disparados (temperatura, disco, memória, SMART).</summary>
public sealed class AlertStore
{
    private readonly ShiftDatabase _db;

    public AlertStore(ShiftDatabase db) => _db = db;

    public event EventHandler<AlertRecord>? Added;

    public AlertRecord Add(string rule, string level, double? value, string? subject)
    {
        var at = DateTimeOffset.Now;
        long id;
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "INSERT INTO alerts(at_ms, rule, level, value, subject) VALUES ($at, $r, $l, $v, $s) RETURNING id";
            cmd.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$r", rule);
            cmd.Parameters.AddWithValue("$l", level);
            cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", (object?)subject ?? DBNull.Value);
            id = (long)cmd.ExecuteScalar()!;
        }
        var record = new AlertRecord(id, at, rule, level, value, subject);
        Added?.Invoke(this, record);
        return record;
    }

    public IReadOnlyList<AlertRecord> List(int limit = 200)
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT id, at_ms, rule, level, value, subject FROM alerts ORDER BY at_ms DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            var list = new List<AlertRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AlertRecord(r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetDouble(4), r.IsDBNull(5) ? null : r.GetString(5)));
            return list;
        }
    }

    public void Clear()
    {
        lock (_db.Gate) _db.Exec("DELETE FROM alerts;");
    }
}

public sealed record PingTestRecord(long Id, DateTimeOffset At, string Host, int Sent, int Received, double? AvgMs, double? MinMs, double? MaxMs, double? JitterMs)
{
    public double LossPercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;
}

/// <summary>Histórico dos testes de conexão (ping, jitter e perda).</summary>
public sealed class PingTestStore
{
    private readonly ShiftDatabase _db;

    public PingTestStore(ShiftDatabase db) => _db = db;

    public long Save(PingTestRecord t)
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ping_tests(at_ms, host, sent, received, avg_ms, min_ms, max_ms, jitter_ms)
                VALUES ($at, $h, $s, $r, $avg, $min, $max, $j) RETURNING id
                """;
            cmd.Parameters.AddWithValue("$at", t.At.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$h", t.Host);
            cmd.Parameters.AddWithValue("$s", t.Sent);
            cmd.Parameters.AddWithValue("$r", t.Received);
            cmd.Parameters.AddWithValue("$avg", (object?)t.AvgMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$min", (object?)t.MinMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$max", (object?)t.MaxMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$j", (object?)t.JitterMs ?? DBNull.Value);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public IReadOnlyList<PingTestRecord> List(int limit = 100)
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT id, at_ms, host, sent, received, avg_ms, min_ms, max_ms, jitter_ms FROM ping_tests ORDER BY at_ms DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            var list = new List<PingTestRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
                list.Add(new PingTestRecord(r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), r.GetString(2),
                    r.GetInt32(3), r.GetInt32(4), D(5), D(6), D(7), D(8)));
            }
            return list;
        }
    }

    public void Clear()
    {
        lock (_db.Gate) _db.Exec("DELETE FROM ping_tests;");
    }
}
