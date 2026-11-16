using Microsoft.Data.Sqlite;

namespace ShiftControl.Storage;

public enum ChangeState
{
    /// <summary>Aplicada e reversível.</summary>
    Applied,
    /// <summary>Revertida com sucesso.</summary>
    Reverted,
    /// <summary>A tentativa de reverter falhou (ver <see cref="ChangeEntry.Result"/>).</summary>
    RevertFailed,
    /// <summary>Registrada só para histórico: não há como desfazer automaticamente.</summary>
    Irreversible,
}

/// <summary>
/// Uma alteração feita pelo app no sistema. <see cref="UndoKind"/> + <see cref="UndoData"/> descrevem como
/// desfazê-la (ex.: "winget.uninstall" + ID do pacote); o executor fica na camada de aplicação.
/// </summary>
public sealed record ChangeEntry(
    long Id,
    DateTimeOffset At,
    string Area,
    string Title,
    string? Detail,
    string? UndoKind,
    string? UndoData,
    ChangeState State,
    string? Result,
    DateTimeOffset? RevertedAt)
{
    public bool CanRevert => State is ChangeState.Applied or ChangeState.RevertFailed && UndoKind is not null;
}

/// <summary>Registro central de tudo que o app altera, com como desfazer. Base da "Central de desfazer".</summary>
public sealed class ChangeJournal
{
    private readonly ShiftDatabase _db;

    public ChangeJournal(ShiftDatabase db) => _db = db;

    public event EventHandler? Changed;

    public long Record(string area, string title, string? detail, string? undoKind, string? undoData)
    {
        long id;
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO changes(at_ms, area, title, detail, undo_kind, undo_data, state)
                VALUES ($at, $area, $title, $detail, $kind, $data, $state) RETURNING id
                """;
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$area", area);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", (object?)undoKind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$data", (object?)undoData ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$state", (undoKind is null ? ChangeState.Irreversible : ChangeState.Applied).ToString());
            id = (long)cmd.ExecuteScalar()!;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return id;
    }

    public void SetState(long id, ChangeState state, string? result)
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "UPDATE changes SET state = $s, result = $r, reverted_ms = CASE WHEN $s = 'Reverted' THEN $now ELSE reverted_ms END WHERE id = $id";
            cmd.Parameters.AddWithValue("$s", state.ToString());
            cmd.Parameters.AddWithValue("$r", (object?)result ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ChangeEntry> List(int limit = 500)
    {
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT id, at_ms, area, title, detail, undo_kind, undo_data, state, result, reverted_ms FROM changes ORDER BY at_ms DESC, id DESC LIMIT $l";
            cmd.Parameters.AddWithValue("$l", limit);
            var list = new List<ChangeEntry>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                list.Add(new ChangeEntry(
                    r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), r.GetString(2), r.GetString(3), S(4), S(5), S(6),
                    Enum.TryParse<ChangeState>(r.GetString(7), out var st) ? st : ChangeState.Irreversible, S(8),
                    r.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9))));
            }
            return list;
        }
    }

    public ChangeEntry? Get(long id) => List(int.MaxValue).FirstOrDefault(e => e.Id == id);
}
