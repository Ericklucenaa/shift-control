using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.Services;
using ShiftControl.App.ViewModels;

namespace ShiftControl.App.Views;

public sealed partial class GameBoostPage : Page
{
    public GameBoostViewModel Vm { get; }

    /// <summary>Opções do seletor de perfil por jogo (padrão + três perfis).</summary>
    public static IReadOnlyList<string> Profiles => App.Services.GetRequiredService<GameBoostViewModel>().ProfileOptions;

    public GameBoostPage()
    {
        Vm = App.Services.GetRequiredService<GameBoostViewModel>();
        InitializeComponent();
        BuildProfileRadios();
        Loaded += async (_, _) =>
        {
            await Vm.LoadAsync();
            SyncRadios();
        };
    }

    private void BuildProfileRadios()
    {
        string[] keys = ["Competitive", "Balanced", "Quiet"];
        for (int i = 0; i < keys.Length; i++)
        {
            int index = i;
            var radio = new RadioButton
            {
                Content = L.Get("boost.profile." + keys[i]),
                GroupName = "BoostProfile",
                Style = (Style)Application.Current.Resources["SegmentRadio"],
                Tag = index,
            };
            radio.Checked += (_, _) =>
            {
                Vm.DefaultProfile = index;
                ProfileHint.Text = L.Get("boost.profileHint." + keys[index]);
            };
            ProfileRadios.Children.Add(radio);
        }
    }

    private void SyncRadios()
    {
        foreach (var r in ProfileRadios.Children.OfType<RadioButton>())
            r.IsChecked = (int)r.Tag == Vm.DefaultProfile;
    }
}
