using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ShiftControl.App.ViewModels;

/// <summary>Tom do indicador de estado de uma linha (mapeado para os pincéis do tema).</summary>
public enum Tone { Muted, Neutral, Good, Attention, Critical, Accent }

/// <summary>
/// Linha genérica das listas das ferramentas (drivers, limpeza, inicialização, erros…): indicador, título, detalhe,
/// metadados, estado e uma ação opcional. Opcionalmente selecionável (caixa) ou alternável (chave).
/// </summary>
public sealed partial class RowVm : ObservableObject
{
    public RowVm(string title, string detail = "", string meta = "", Tone tone = Tone.Neutral, string status = "")
    {
        Title = title;
        Detail = detail;
        Meta = meta;
        Tone = tone;
        Status = status;
    }

    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string Detail { get; set; }
    [ObservableProperty] public partial string Meta { get; set; }
    [ObservableProperty] public partial Tone Tone { get; set; }
    [ObservableProperty] public partial string Status { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }

    /// <summary>Texto à direita em destaque (ex.: tamanho). Vazio = oculto.</summary>
    [ObservableProperty] public partial string Value { get; set; } = "";

    /// <summary>Fração 0..1 para a barra de proporção (analisador de espaço). Negativo = sem barra.</summary>
    [ObservableProperty] public partial double Fraction { get; set; } = -1;

    public bool Selectable { get; init; }
    [ObservableProperty] public partial bool IsChecked { get; set; }
    [ObservableProperty] public partial bool CanCheck { get; set; } = true;

    public bool Toggleable { get; init; }
    [ObservableProperty] public partial bool IsOn { get; set; }
    [ObservableProperty] public partial bool CanToggle { get; set; } = true;

    public string? ActionLabel { get; init; }
    public ICommand? Action { get; init; }
    public string? SecondaryLabel { get; init; }
    public ICommand? SecondaryAction { get; init; }

    public object? Tag { get; init; }

    /// <summary>Chamado quando o usuário marca/desmarca (seleção) ou liga/desliga (chave).</summary>
    public Action<RowVm>? Changed { get; init; }

    public bool HasAction => ActionLabel is not null;
    public bool HasSecondary => SecondaryLabel is not null;

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_quiet) Changed?.Invoke(this);
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_quiet) Changed?.Invoke(this);
    }

    private bool _quiet;

    /// <summary>Altera sem disparar <see cref="Changed"/> (ex.: desfazer na interface depois de uma falha).</summary>
    public void SetQuietly(bool? isOn = null, bool? isChecked = null)
    {
        _quiet = true;
        if (isOn is { } on) IsOn = on;
        if (isChecked is { } c) IsChecked = c;
        _quiet = false;
    }
}

/// <summary>Base das telas de ferramenta: ocupado, mensagem de resultado e elevação.</summary>
public abstract partial class ToolViewModel : ObservableObject
{
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }
    public bool IsElevated { get; } = Hardware.Elevation.IsElevated;
    public bool NotElevated => !IsElevated;

    protected void Info(string message)
    {
        MessageIsError = false;
        Message = message;
    }

    protected void Error(string message)
    {
        MessageIsError = true;
        Message = message;
    }
}

/// <summary>Indicador numérico compacto (rótulo, valor, unidade).</summary>
public sealed partial class StatVm(string label, string unit = "") : ObservableObject
{
    public string Label { get; } = label;
    public string Unit { get; } = unit;
    [ObservableProperty] public partial string Value { get; set; } = "—";
    [ObservableProperty] public partial Tone Tone { get; set; } = Tone.Neutral;
}
