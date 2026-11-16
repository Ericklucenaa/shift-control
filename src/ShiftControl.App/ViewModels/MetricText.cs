using CommunityToolkit.Mvvm.ComponentModel;
using ShiftControl.App.Services;
using ShiftControl.Core;

namespace ShiftControl.App.ViewModels;

/// <summary>
/// Valor exibível de uma métrica: o texto formatado, ou "Indisponível" com a explicação no <see cref="Hint"/>.
/// Instâncias são reaproveitadas a cada atualização; só as propriedades que mudam notificam a interface.
/// </summary>
public sealed partial class MetricText : ObservableObject
{
    [ObservableProperty] public partial string Text { get; set; } = "—";
    [ObservableProperty] public partial bool Available { get; set; }
    [ObservableProperty] public partial string? Hint { get; set; }

    public void Set(Reading reading, Func<double, string> format)
    {
        if (reading.Value is { } v)
        {
            Text = format(v);
            Available = true;
            Hint = null;
        }
        else
        {
            Text = reading.Reason == Unavailable.Pending ? "—" : Fmt.Unavailable;
            Available = false;
            Hint = Fmt.Hint(reading.Reason);
        }
    }

    public void SetText(string text)
    {
        Text = text;
        Available = true;
        Hint = null;
    }

    public void SetMissing(Unavailable reason) => Set(Reading.Missing(reason), _ => "");
}
