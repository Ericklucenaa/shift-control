using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShiftControl.App.Services;
using ShiftControl.Diagnostics;
using ShiftControl.Hardware;

namespace ShiftControl.App.ViewModels;

public sealed record CheckItemVm(CheckResult Result, string Title, string Detail, string StatusText, string? FixLabel)
{
    public CheckStatus Status => Result.Status;
    public bool HasFix => FixLabel is not null;
    public bool HasDetail => Detail.Length > 0;
}

public sealed partial class ChecksViewModel : ObservableObject
{
    private readonly ILogger<ChecksViewModel> _log;
    private SystemFacts? _facts;

    public ChecksViewModel(ILogger<ChecksViewModel> log) => _log = log;

    public ObservableCollection<CheckItemVm> Gamer { get; } = [];
    public ObservableCollection<CheckItemVm> PostFormat { get; } = [];

    [ObservableProperty] public partial bool Loading { get; set; }
    [ObservableProperty] public partial bool CheckingSlow { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string CheckedAt { get; set; } = "";
    [ObservableProperty] public partial bool HasResults { get; set; }

    [RelayCommand]
    public async Task RunAsync()
    {
        if (Loading) return;
        Loading = true;
        try
        {
            _facts = await Task.Run(SystemFactsProbe.ReadFast);
            Show(_facts, slowPending: true);
            HasResults = true;
            Loading = false;

            // Ativação e Windows Update levam de segundos a mais de um minuto: rodam depois, sem travar a tela.
            CheckingSlow = true;
            var (activated, updates) = await Task.Run(SystemFactsProbe.ReadSlow);
            _facts = _facts with { WindowsActivated = activated, PendingUpdates = updates };
            Show(_facts, slowPending: false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha na checagem do sistema.");
            Summary = L.Get("checks.error");
        }
        finally
        {
            Loading = false;
            CheckingSlow = false;
        }
    }

    private void Show(SystemFacts facts, bool slowPending)
    {
        var results = SystemChecks.Evaluate(facts);
        Gamer.Clear();
        PostFormat.Clear();
        foreach (var r in results.OrderBy(r => r.Status switch { CheckStatus.Attention => 0, CheckStatus.Unknown => 1, CheckStatus.Info => 2, _ => 3 }))
        {
            var vm = Describe(r, slowPending);
            (r.Group == CheckGroup.Gamer ? Gamer : PostFormat).Add(vm);
        }
        int attention = results.Count(r => r.Status == CheckStatus.Attention);
        Summary = attention == 0 ? L.F("checks.summaryOk", results.Count) : L.F("checks.summaryAttention", attention, results.Count);
        CheckedAt = L.F("checks.checkedAt", DateTime.Now.ToString("t", L.Culture));
    }

    /// <summary>Textos de cada resultado. Os argumentos são os dados medidos (evidência), nunca suposições.</summary>
    private static CheckItemVm Describe(CheckResult r, bool slowPending)
    {
        var a = r.Args.Select(x => (object?)x).ToList();
        while (a.Count < 4) a.Add("");
        string status = r.Status.ToString();

        if (r.Code is "gaming.hags" or "gaming.windowed" or "gaming.vrr" or "gaming.gamemode")
            a[0] = L.Get("checks.toggle." + r.Args.FirstOrDefault());
        if (r.Code == "memory.xmp" && r.Args.Count > 2)
            a[2] = r.Args[2] == "part" ? L.F("checks.memory.fromPart", r.Args[3]) : L.Get("checks.memory.fromBios");
        if (r.Code == "display.refresh" && r.Args.Count > 0 && r.Args[0].Length > 0)
            a[0] = L.F("checks.display.which", r.Args[0] == "principal" ? L.Get("checks.display.primary") : r.Args[0]);

        bool pendingSlow = slowPending && r.Code is "post.activation" or "post.updates";
        string title = pendingSlow ? L.Get($"check.{r.Code}.checking") : L.F($"check.{r.Code}.{status}.title", [.. a]);
        string detailKey = $"check.{r.Code}.{status}.detail";
        string detail = pendingSlow ? "" : L.Get(detailKey) == detailKey ? "" : L.F(detailKey, [.. a]);
        string statusText = pendingSlow ? L.Get("checks.status.checking") : L.Get("checks.status." + status);
        string? fix = r.Fix is null || r.Status == CheckStatus.Ok ? null : L.Get(r.Fix == "devmgmt.msc" ? "checks.fix.devices" : "checks.fix.settings");
        return new CheckItemVm(r, title.Trim(), detail, statusText, fix);
    }

    [RelayCommand]
    private void OpenFix(CheckItemVm? item)
    {
        if (item?.Result.Fix is not { } fix) return;
        try
        {
            Process.Start(new ProcessStartInfo(fix) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(ex, "Não foi possível abrir {Fix}.", fix);
        }
    }
}
