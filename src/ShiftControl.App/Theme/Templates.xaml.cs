using Microsoft.UI.Xaml;

namespace ShiftControl.App.Theme;

/// <summary>Modelos de lista compartilhados entre as telas (x:Bind exige um dicionário com classe).</summary>
public sealed partial class Templates : ResourceDictionary
{
    public Templates() => InitializeComponent();
}
