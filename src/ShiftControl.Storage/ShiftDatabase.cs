using Microsoft.Data.Sqlite;

namespace ShiftControl.Storage;

/// <summary>
/// Uma conexão SQLite compartilhada, protegida por lock. O volume de escrita é baixo (lotes a cada ~15 s),
/// então uma conexão única evita contenção de arquivo e simplifica as migrações.
/// </summary>
public sealed class ShiftDatabase : IDisposable
{
    private const int SchemaVersion = 4;

    internal SqliteConnection Connection { get; }
    internal Lock Gate { get; } = new();
    public string Path { get; }

    public ShiftDatabase(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        Connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        Connection.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    private void Migrate()
    {
        long version = Scalar<long>("PRAGMA user_version;");
        if (version > SchemaVersion)
            throw new InvalidOperationException($"Banco de dados criado por uma versão mais nova (schema {version}).");

        if (version < 1)
        {
            using var tx = Connection.BeginTransaction();
            Exec("""
                CREATE TABLE IF NOT EXISTS settings (
                    key  TEXT PRIMARY KEY,
                    json TEXT NOT NULL,
                    updated_at INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS metrics (
                    id  INTEGER PRIMARY KEY,
                    key TEXT NOT NULL UNIQUE
                );
                CREATE TABLE IF NOT EXISTS samples_raw (
                    metric_id INTEGER NOT NULL,
                    ts        INTEGER NOT NULL,
                    v         REAL NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_raw_metric_ts ON samples_raw(metric_id, ts);
                CREATE TABLE IF NOT EXISTS samples_minute (
                    metric_id INTEGER NOT NULL,
                    ts        INTEGER NOT NULL,
                    avg       REAL NOT NULL,
                    min       REAL NOT NULL,
                    max       REAL NOT NULL,
                    n         INTEGER NOT NULL,
                    PRIMARY KEY (metric_id, ts)
                ) WITHOUT ROWID;
                """, tx);
            Exec("PRAGMA user_version = 1;", tx);
            tx.Commit();
            version = 1;
        }

        if (version < 2)
        {
            using var tx = Connection.BeginTransaction();
            Exec("""
                CREATE TABLE IF NOT EXISTS game_sessions (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    executable   TEXT NOT NULL,
                    name         TEXT NOT NULL,
                    start_ms     INTEGER NOT NULL,
                    end_ms       INTEGER NOT NULL,
                    frames       INTEGER NOT NULL,
                    avg_fps      REAL, low1_fps REAL, low01_fps REAL,
                    avg_frametime REAL, p99_frametime REAL,
                    stutters     INTEGER NOT NULL,
                    cpu_load_avg REAL, gpu_load_avg REAL,
                    cpu_temp_avg REAL, cpu_temp_max REAL,
                    gpu_temp_avg REAL, gpu_temp_max REAL,
                    vram_max_mb  REAL,
                    runtime      TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_sessions_exe_start ON game_sessions(executable, start_ms);
                CREATE TABLE IF NOT EXISTS game_session_points (
                    session_id INTEGER NOT NULL REFERENCES game_sessions(id) ON DELETE CASCADE,
                    second     INTEGER NOT NULL,
                    fps REAL, cpu_load REAL, gpu_load REAL, cpu_temp REAL, gpu_temp REAL,
                    PRIMARY KEY (session_id, second)
                ) WITHOUT ROWID;
                """, tx);
            Exec("PRAGMA user_version = 2;", tx);
            tx.Commit();
            version = 2;
        }

        if (version < 3)
        {
            using var tx = Connection.BeginTransaction();
            Exec("""
                CREATE TABLE IF NOT EXISTS changes (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    at_ms       INTEGER NOT NULL,
                    area        TEXT NOT NULL,
                    title       TEXT NOT NULL,
                    detail      TEXT,
                    undo_kind   TEXT,
                    undo_data   TEXT,
                    state       TEXT NOT NULL,
                    result      TEXT,
                    reverted_ms INTEGER
                );
                CREATE INDEX IF NOT EXISTS ix_changes_at ON changes(at_ms);
                """, tx);
            Exec("PRAGMA user_version = 3;", tx);
            tx.Commit();
            version = 3;
        }

        if (version < 4)
        {
            // Game Boost (perfil por sessão, clocks para evidência térmica), alertas e testes de rede.
            using var tx = Connection.BeginTransaction();
            Exec("""
                ALTER TABLE game_sessions ADD COLUMN boost TEXT;
                ALTER TABLE game_session_points ADD COLUMN cpu_clock REAL;
                ALTER TABLE game_session_points ADD COLUMN gpu_clock REAL;
                ALTER TABLE game_session_points ADD COLUMN gpu_power REAL;
                CREATE TABLE IF NOT EXISTS alerts (
                    id      INTEGER PRIMARY KEY AUTOINCREMENT,
                    at_ms   INTEGER NOT NULL,
                    rule    TEXT NOT NULL,
                    level   TEXT NOT NULL,
                    value   REAL,
                    subject TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_alerts_at ON alerts(at_ms);
                CREATE TABLE IF NOT EXISTS ping_tests (
                    id       INTEGER PRIMARY KEY AUTOINCREMENT,
                    at_ms    INTEGER NOT NULL,
                    host     TEXT NOT NULL,
                    sent     INTEGER NOT NULL,
                    received INTEGER NOT NULL,
                    avg_ms   REAL, min_ms REAL, max_ms REAL, jitter_ms REAL
                );
                CREATE INDEX IF NOT EXISTS ix_ping_at ON ping_tests(at_ms);
                """, tx);
            Exec($"PRAGMA user_version = {SchemaVersion};", tx);
            tx.Commit();
        }
    }

    internal void Exec(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    internal T Scalar<T>(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, typeof(T));
    }

    public void Dispose()
    {
        lock (Gate)
        {
            try { Exec("PRAGMA optimize;"); } catch (SqliteException) { }
            Connection.Dispose();
        }
    }
}
