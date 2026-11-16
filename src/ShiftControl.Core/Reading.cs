namespace ShiftControl.Core;

/// <summary>Por que uma leitura não tem valor. A interface traduz o motivo em texto para o usuário.</summary>
public enum Unavailable : byte
{
    None = 0,
    /// <summary>O hardware ou o driver não expõe esta métrica.</summary>
    NotSupported,
    /// <summary>A leitura exige executar o aplicativo como administrador.</summary>
    RequiresAdmin,
    /// <summary>A leitura exige o driver de sensores (PawnIO) instalado.</summary>
    RequiresDriver,
    /// <summary>O dispositivo não possui este sensor.</summary>
    NoSensor,
    /// <summary>O sensor respondeu com um valor fora do plausível; o valor foi descartado.</summary>
    Invalid,
    /// <summary>Ainda não há amostras suficientes (taxas precisam de duas leituras).</summary>
    Pending,
    /// <summary>Falha ao ler; os detalhes ficam no log.</summary>
    Error,
}

/// <summary>
/// Uma medição real ou a explicação de por que ela não existe. Nunca carrega valor inventado:
/// quando <see cref="Value"/> é nulo, <see cref="Reason"/> diz o motivo.
/// </summary>
public readonly record struct Reading(double? Value, Unavailable Reason)
{
    public static Reading Of(double value) =>
        double.IsFinite(value) ? new(value, Unavailable.None) : new(null, Unavailable.Invalid);

    public static Reading Missing(Unavailable reason) =>
        new(null, reason == Unavailable.None ? Unavailable.NotSupported : reason);

    public static Reading From(double? value, Unavailable whenNull) =>
        value is { } v ? Of(v) : Missing(whenNull);

    public bool HasValue => Value.HasValue;

    public double Or(double fallback) => Value ?? fallback;

    public Reading Map(Func<double, double> f) => Value is { } v ? Of(f(v)) : this;

    public override string ToString() => Value is { } v ? v.ToString("0.##") : $"({Reason})";
}
