using ShiftControl.Core;

namespace ShiftControl.Monitoring;

/// <summary>
/// Escolhe os poucos sensores que resumem um componente numa linha (ex.: CPU → temperatura, uso, frequência, potência).
/// A escolha depende só de nome e tipo, nunca do valor: a linha não "pula" de sensor enquanto as leituras mudam.
/// </summary>
public static class SensorHighlights
{
    public const int Max = 4;

    private sealed record Slot(SensorKind Kind, string[] Names, bool NamesOnly = false);

    private static readonly Dictionary<HardwareCategory, Slot[]> Rules = new()
    {
        [HardwareCategory.Cpu] =
        [
            new(SensorKind.Temperature, ["Core (Tctl/Tdie)", "CPU Package", "Core (Tdie)", "Core (Tctl)", "Core Average", "CPU Cores"]),
            new(SensorKind.Load, ["CPU Total"]),
            new(SensorKind.Clock, ["Cores (Average)", "Core Average", "CPU Core #1"]),
            new(SensorKind.Power, ["Package", "CPU Package", "CPU Cores"]),
        ],
        [HardwareCategory.Gpu] =
        [
            new(SensorKind.Temperature, ["GPU Core"]),
            new(SensorKind.Load, ["GPU Core", "D3D 3D"]),
            new(SensorKind.Clock, ["GPU Core"]),
            new(SensorKind.Power, ["GPU Package", "GPU Power", "GPU Core"]),
        ],
        [HardwareCategory.Memory] =
        [
            new(SensorKind.Load, ["Memory"]),
            new(SensorKind.Data, ["Memory Used", "Used Memory"]),
        ],
        [HardwareCategory.Storage] =
        [
            new(SensorKind.Temperature, ["Temperature", "Composite Temperature"]),
            new(SensorKind.Load, ["Activity", "Total Activity", "Used Space"]),
            new(SensorKind.Throughput, ["Read Rate"], NamesOnly: true),
            new(SensorKind.Throughput, ["Write Rate"], NamesOnly: true),
        ],
        [HardwareCategory.Network] =
        [
            new(SensorKind.Throughput, ["Download Speed"], NamesOnly: true),
            new(SensorKind.Throughput, ["Upload Speed"], NamesOnly: true),
            new(SensorKind.Load, ["Network Utilization"], NamesOnly: true),
        ],
    };

    private static readonly Slot[] Fallback =
    [
        new(SensorKind.Temperature, []),
        new(SensorKind.Load, []),
        new(SensorKind.Power, []),
        new(SensorKind.Fan, []),
        new(SensorKind.Level, []),
    ];

    /// <summary>Sensores de um mesmo componente, na ordem em que o hardware os lista.</summary>
    public static IReadOnlyList<SensorReading> Pick(HardwareCategory category, IReadOnlyList<SensorReading> sensors) =>
        Pick(category, sensors, s => s.Kind, s => s.Name);

    public static IReadOnlyList<T> Pick<T>(HardwareCategory category, IReadOnlyList<T> sensors, Func<T, SensorKind> kind, Func<T, string> name)
        where T : class
    {
        var slots = Rules.TryGetValue(category, out var rule) ? rule : Fallback;
        var picked = new List<T>(Max);
        foreach (var slot in slots)
        {
            if (picked.Count == Max) break;
            var candidates = sensors.Where(s => kind(s) == slot.Kind && !picked.Any(p => ReferenceEquals(p, s))).ToList();
            var match = slot.Names.Select(n => candidates.FirstOrDefault(s => name(s).Equals(n, StringComparison.OrdinalIgnoreCase)))
                            .FirstOrDefault(s => s is not null)
                        ?? (slot.NamesOnly ? null : candidates.FirstOrDefault());
            if (match is not null) picked.Add(match);
        }
        return picked;
    }
}
