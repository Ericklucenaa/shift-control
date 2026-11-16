using Microsoft.Data.Sqlite;
using ShiftControl.Overlay;

namespace ShiftControl.Storage;

public sealed class GameSessionStore
{
    private readonly ShiftDatabase _db;

    public GameSessionStore(ShiftDatabase db) => _db = db;

    public long Save(GameSessionRecord s, IReadOnlyList<SessionPoint> points)
    {
        lock (_db.Gate)
        {
            using var tx = _db.Connection.BeginTransaction();
            using var cmd = _db.Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO game_sessions(executable, name, start_ms, end_ms, frames, avg_fps, low1_fps, low01_fps,
                    avg_frametime, p99_frametime, stutters, cpu_load_avg, gpu_load_avg, cpu_temp_avg, cpu_temp_max,
                    gpu_temp_avg, gpu_temp_max, vram_max_mb, runtime, boost)
                VALUES ($exe, $name, $start, $end, $frames, $avg, $l1, $l01, $aft, $p99, $st, $cpu, $gpu, $ctavg, $ctmax,
                    $gtavg, $gtmax, $vram, $rt, $boost)
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("$exe", s.Executable);
            cmd.Parameters.AddWithValue("$name", s.Name);
            cmd.Parameters.AddWithValue("$start", s.Start.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$end", s.End.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$frames", s.Frames);
            cmd.Parameters.AddWithValue("$avg", Db(s.AverageFps));
            cmd.Parameters.AddWithValue("$l1", Db(s.OnePercentLowFps));
            cmd.Parameters.AddWithValue("$l01", Db(s.PointOnePercentLowFps));
            cmd.Parameters.AddWithValue("$aft", Db(s.AverageFrametimeMs));
            cmd.Parameters.AddWithValue("$p99", Db(s.P99FrametimeMs));
            cmd.Parameters.AddWithValue("$st", s.Stutters);
            cmd.Parameters.AddWithValue("$cpu", Db(s.CpuLoadAvg));
            cmd.Parameters.AddWithValue("$gpu", Db(s.GpuLoadAvg));
            cmd.Parameters.AddWithValue("$ctavg", Db(s.CpuTempAvg));
            cmd.Parameters.AddWithValue("$ctmax", Db(s.CpuTempMax));
            cmd.Parameters.AddWithValue("$gtavg", Db(s.GpuTempAvg));
            cmd.Parameters.AddWithValue("$gtmax", Db(s.GpuTempMax));
            cmd.Parameters.AddWithValue("$vram", Db(s.VramMaxMb));
            cmd.Parameters.AddWithValue("$rt", (object?)s.Runtime ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$boost", (object?)s.Boost ?? DBNull.Value);
            long id = (long)cmd.ExecuteScalar()!;

            using var pt = _db.Connection.CreateCommand();
            pt.Transaction = tx;
            pt.CommandText = "INSERT OR REPLACE INTO game_session_points(session_id, second, fps, cpu_load, gpu_load, cpu_temp, gpu_temp, cpu_clock, gpu_clock, gpu_power) VALUES ($id, $s, $fps, $cpu, $gpu, $ct, $gt, $cc, $gc, $gp)";
            var pId = pt.Parameters.Add("$id", SqliteType.Integer);
            var pS = pt.Parameters.Add("$s", SqliteType.Integer);
            var pFps = pt.Parameters.Add("$fps", SqliteType.Real);
            var pCpu = pt.Parameters.Add("$cpu", SqliteType.Real);
            var pGpu = pt.Parameters.Add("$gpu", SqliteType.Real);
            var pCt = pt.Parameters.Add("$ct", SqliteType.Real);
            var pGt = pt.Parameters.Add("$gt", SqliteType.Real);
            var pCc = pt.Parameters.Add("$cc", SqliteType.Real);
            var pGc = pt.Parameters.Add("$gc", SqliteType.Real);
            var pGp = pt.Parameters.Add("$gp", SqliteType.Real);
            pt.Prepare();
            foreach (var p in points)
            {
                pId.Value = id;
                pS.Value = p.Second;
                pFps.Value = Db(p.Fps);
                pCpu.Value = Db(p.CpuLoad);
                pGpu.Value = Db(p.GpuLoad);
                pCt.Value = Db(p.CpuTemp);
                pGt.Value = Db(p.GpuTemp);
                pCc.Value = Db(p.CpuClock);
                pGc.Value = Db(p.GpuClock);
                pGp.Value = Db(p.GpuPower);
                pt.ExecuteNonQuery();
            }
            tx.Commit();
            return id;
        }
    }

    public IReadOnlyList<GameSessionRecord> List(string? executable = null, int limit = 200)
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, executable, name, start_ms, end_ms, frames, avg_fps, low1_fps, low01_fps, avg_frametime,
                       p99_frametime, stutters, cpu_load_avg, gpu_load_avg, cpu_temp_avg, cpu_temp_max, gpu_temp_avg,
                       gpu_temp_max, vram_max_mb, runtime, boost
                FROM game_sessions {(executable is null ? "" : "WHERE executable = $exe COLLATE NOCASE")}
                ORDER BY start_ms DESC LIMIT $limit
                """;
            if (executable is not null) cmd.Parameters.AddWithValue("$exe", executable);
            cmd.Parameters.AddWithValue("$limit", limit);
            var list = new List<GameSessionRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
                list.Add(new GameSessionRecord(
                    r.GetInt64(0), r.GetString(1), r.GetString(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
                    r.GetInt64(5), D(6), D(7), D(8), D(9), D(10), r.GetInt32(11), D(12), D(13), D(14), D(15), D(16), D(17), D(18),
                    r.IsDBNull(19) ? null : r.GetString(19), r.IsDBNull(20) ? null : r.GetString(20)));
            }
            return list;
        }
    }

    public IReadOnlyList<SessionPoint> Points(long sessionId)
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT second, fps, cpu_load, gpu_load, cpu_temp, gpu_temp, cpu_clock, gpu_clock, gpu_power FROM game_session_points WHERE session_id = $id ORDER BY second";
            cmd.Parameters.AddWithValue("$id", sessionId);
            var list = new List<SessionPoint>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
                list.Add(new SessionPoint(r.GetInt32(0), D(1), D(2), D(3), D(4), D(5), D(6), D(7), D(8)));
            }
            return list;
        }
    }

    public IReadOnlyList<string> Games()
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT executable FROM game_sessions GROUP BY executable COLLATE NOCASE ORDER BY MAX(start_ms) DESC";
            var list = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public void Delete(long sessionId)
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "DELETE FROM game_session_points WHERE session_id = $id; DELETE FROM game_sessions WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", sessionId);
            cmd.ExecuteNonQuery();
        }
    }

    private static object Db(double? v) => v is { } d && double.IsFinite(d) ? d : DBNull.Value;
}
