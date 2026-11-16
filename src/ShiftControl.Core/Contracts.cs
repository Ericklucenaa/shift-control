namespace ShiftControl.Core;

/// <summary>
/// Fonte de dados do hardware. Todas as chamadas acontecem na mesma thread de coleta
/// (bibliotecas de sensores não são thread-safe). Falhas de um componente não devem impedir os outros.
/// </summary>
public interface IHardwareSource : IDisposable
{
    /// <summary>Abre os drivers/APIs e lê o inventário estático. Pode demorar ~1 s.</summary>
    SystemInfo Initialize();

    /// <summary>Lê todos os sensores. Nunca lança por falha de um componente isolado.</summary>
    SystemSnapshot Sample(long sequence);
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
    T? LoadSection<T>(string key) where T : class;
    void SaveSection<T>(string key, T value) where T : class;
}

public readonly record struct HistoryPoint(DateTimeOffset Time, double Avg, double Min, double Max);

public interface IHistoryStore : IDisposable
{
    TimeSpan RawRetention { get; set; }
    TimeSpan AggregatedRetention { get; set; }

    void Append(DateTimeOffset time, IEnumerable<KeyValuePair<string, double>> values);
    void Flush();

    /// <summary>Série agregada em até <paramref name="maxPoints"/> baldes de tempo.</summary>
    IReadOnlyList<HistoryPoint> Query(string metric, DateTimeOffset from, DateTimeOffset to, int maxPoints);

    /// <summary>Apaga amostras além da retenção configurada. Retorna o número de linhas removidas.</summary>
    int ApplyRetention(DateTimeOffset now);

    long DatabaseSizeBytes { get; }
    void Clear();
}
