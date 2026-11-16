using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShiftControl.Core;

namespace ShiftControl.Storage;

public sealed class SettingsStore : ISettingsStore
{
    private const string AppKey = "app";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly ShiftDatabase _db;
    private readonly ILogger _log;

    public SettingsStore(ShiftDatabase db, ILogger<SettingsStore>? log = null)
    {
        _db = db;
        _log = log ?? NullLogger<SettingsStore>.Instance;
    }

    public AppSettings Load() => (LoadSection<AppSettings>(AppKey) ?? new AppSettings()).Normalize();

    public void Save(AppSettings settings) => SaveSection(AppKey, settings.Clone().Normalize());

    public T? LoadSection<T>(string key) where T : class
    {
        string? json;
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT json FROM settings WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            json = cmd.ExecuteScalar() as string;
        }
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException ex)
        {
            // Configuração corrompida não pode impedir o app de abrir: volta ao padrão e registra.
            _log.LogWarning(ex, "Configuração {Key} inválida; usando valores padrão.", key);
            return null;
        }
    }

    public void SaveSection<T>(string key, T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, Json);
        lock (_db.Gate)
        {
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings(key, json, updated_at) VALUES ($k, $j, $t)
                ON CONFLICT(key) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
    }
}
