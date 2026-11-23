using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShiftControl.App.Services;
using ShiftControl.Core;
using ShiftControl.Hardware;
using Windows.ApplicationModel.DataTransfer;

namespace ShiftControl.App.Views;

public sealed record ComponentRow(string Name, string? Detail = null)
{
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <param name="Summary">Texto curto à direita do título (ex.: "4 de 4 slots").</param>
/// <param name="Note">Aviso prático sobre a peça, quando houver.</param>
public sealed record ComponentSection(string Title, IReadOnlyList<ComponentRow> Rows, string? Summary = null, string? Note = null)
{
    public bool HasSummary => !string.IsNullOrEmpty(Summary);
    public bool HasNote => !string.IsNullOrEmpty(Note);
}

public sealed partial class ComponentsPage : Page
{
    private HardwareInventory? _inventory;
    private ComponentSection[] _sections = [];
    private bool _twoColumns = true;
    private const double TwoColumnMinWidth = 860;

    public ComponentsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_inventory is null) await RefreshAsync();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_inventory is null) return;
        try
        {
            var package = new DataPackage();
            package.SetText(_inventory.ToText(b => Fmt.Bytes(b, 0), L.Get));
            Clipboard.SetContent(package);
            Show(InfoBarSeverity.Success, L.Get("components.copied"));
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<ComponentsPage>>().LogWarning(ex, "Falha ao copiar as especificações.");
            Show(InfoBarSeverity.Error, L.Get("components.copyFailed"));
        }
    }

    private async Task RefreshAsync()
    {
        if (!RefreshButton.IsEnabled) return;
        RefreshButton.IsEnabled = false;
        LoadingBar.Visibility = Visibility.Visible;
        StatusBar.IsOpen = false;
        if (_inventory is null) Headline.Text = L.Get("components.loading");
        try
        {
            _inventory = await Task.Run(HardwareInventory.Read);
            Headline.Text = _inventory.Headline(b => Fmt.Bytes(b, 0)) is { Length: > 0 } h ? h : L.Get("components.unavailable");
            _sections = BuildSections(_inventory);
            Arrange();
            CopyButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<ComponentsPage>>().LogWarning(ex, "Não foi possível ler os componentes do computador.");
            if (_inventory is null) Headline.Text = L.Get("components.unavailable");
            Show(InfoBarSeverity.Error, L.Get("components.error"));
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
        }
    }

    private void OnColumnsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool two = e.NewSize.Width >= TwoColumnMinWidth;
        if (two == _twoColumns) return;
        _twoColumns = two;
        Arrange();
    }

    /// <summary>
    /// Reparte os cartões entre as duas colunas mantendo a ordem de leitura e equilibrando a altura
    /// (estimada pelo número de linhas de cada cartão). Em janela estreita, tudo numa coluna só.
    /// </summary>
    private void Arrange()
    {
        RightColumnDef.Width = _twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Columns.ColumnSpacing = _twoColumns ? 12 : 0;
        if (!_twoColumns)
        {
            LeftSections.ItemsSource = _sections;
            RightSections.ItemsSource = null;
            return;
        }
        var (left, right) = SplitColumns(_sections);
        LeftSections.ItemsSource = left;
        RightSections.ItemsSource = right;
    }

    internal static (List<ComponentSection> Left, List<ComponentSection> Right) SplitColumns(IReadOnlyList<ComponentSection> sections)
    {
        static int Weight(ComponentSection s) => 2 + s.Rows.Count * 2 + (s.HasNote ? 3 : 0);
        var left = new List<ComponentSection>();
        var right = new List<ComponentSection>();
        int lh = 0, rh = 0;
        foreach (var s in sections)
        {
            if (lh <= rh) { left.Add(s); lh += Weight(s); }
            else { right.Add(s); rh += Weight(s); }
        }
        return (left, right);
    }

    private void Show(InfoBarSeverity severity, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    internal static ComponentSection[] BuildSections(HardwareInventory inv)
    {
        var sections = new List<ComponentSection>
        {
            Section(L.Get("components.cpu"), inv.Processors.Select(cpu => new ComponentRow(Detected(cpu.Name), Join(
                cpu.Cores > 0 ? L.F("components.cores", cpu.Cores) : null,
                cpu.Threads > 0 ? L.F("components.threads", cpu.Threads) : null,
                cpu.MaxMhz > 0 ? Fmt.Clock(cpu.MaxMhz) : null,
                cpu.Socket is { } socket ? L.F("components.socket", socket) : null,
                cpu.L3Kb > 0 ? L.F("components.l3", Fmt.Bytes(cpu.L3Kb * 1024.0, 0)) : null)))),
            Section(L.Get("components.gpu"), inv.Gpus.Select(g => new ComponentRow(g.Name, Join(
                g.VramBytes is { } vram ? L.F("components.vram", Fmt.Bytes(vram, 0)) : null,
                g.DriverVersion is { } drv ? L.F("components.driver", drv + (g.DriverDate is { } d ? " (" + d.ToString("d", L.Culture) + ")" : "")) : null,
                g.Width > 0 && g.Height > 0 ? L.F("components.display", $"{g.Width}×{g.Height}", g.RefreshHz) : null)))),
            MemorySection(inv),
            Section(L.Get("components.motherboard"), [new ComponentRow(Detected(Spaced(inv.Board.Manufacturer, inv.Board.Product)),
                Spaced(HardwareInventory.CleanVendor(inv.Board.BiosVendor), inv.Board.BiosVersion) is { Length: > 0 } bios
                    ? L.F("components.bios", bios + (inv.Board.BiosDate is { } bd ? " (" + bd.ToString("d", L.Culture) + ")" : ""))
                    : null)]),
            Section(L.Get("components.storage"), inv.Drives.Select(d => new ComponentRow(Detected(d.Model), Join(
                d.CapacityBytes > 0 ? Fmt.Bytes(d.CapacityBytes, 0) : null,
                Join(d.Kind switch { DriveKind.Ssd or DriveKind.NvmeSsd => "SSD", DriveKind.Hdd => L.Get("components.hdd"), _ => null }, d.Bus) is { Length: > 0 } kind ? kind : null))),
                summary: inv.Drives.Count > 1 ? Fmt.Bytes(inv.Drives.Aggregate(0UL, (s, d) => s + d.CapacityBytes), 1) : null),
            Section(L.Get("components.monitors"), inv.Monitors.Select(m => new ComponentRow(Detected(m.Name), m.Manufacturer))),
            Section(L.Get("components.network"), inv.Network.Select(n => new ComponentRow(n.Name,
                n.SpeedBps is > 0 and < 1_000_000_000_000 ? L.F("components.linkSpeed", LinkSpeed(n.SpeedBps.Value)) : null))),
            Section(L.Get("components.audio"), inv.Audio.Select(a => new ComponentRow(a))),
        };
        return sections.ToArray();
    }

    private static ComponentSection MemorySection(HardwareInventory inv)
    {
        var modules = inv.MemoryModules;
        string title = inv.TotalMemoryBytes > 0
            ? L.F("components.memoryTotal", string.Join(' ', new[] { Fmt.Bytes(inv.TotalMemoryBytes, 0), inv.MemoryType }.Where(x => x is not null)))
            : L.Get("components.memory");
        string? slots = inv.MemorySlots > 0 && modules.Count > 0 ? L.F("components.slots", modules.Count, inv.MemorySlots) : null;
        var rows = modules.Select((m, i) => new ComponentRow(
            Join(m.Slot ?? L.F("components.module", i + 1), m.CapacityBytes > 0 ? Fmt.Bytes(m.CapacityBytes, 0) : null)!,
            Join(m.ConfiguredMts > 0 ? $"{m.ConfiguredMts} MT/s" : null, Join(m.Manufacturer, m.PartNumber))));

        // Avisos que ajudam de verdade: XMP/EXPO desligado e pentes de modelos diferentes (causa comum de instabilidade).
        var notes = new List<string>();
        if (modules.FirstOrDefault(m => m.BelowRated) is { } slow)
            notes.Add(L.F("components.belowRated", slow.ConfiguredMts, slow.RatedMts));
        if (modules.Select(m => m.PartNumber?.Trim()).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            notes.Add(L.Get("components.mixedKits"));
        return Section(title, rows, slots, notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    private static string LinkSpeed(ulong bps) =>
        bps >= 1_000_000_000 ? Fmt.Number(bps / 1e9, bps % 1_000_000_000 == 0 ? 0 : 1) + " Gb/s" : Fmt.Number(bps / 1e6, 0) + " Mb/s";

    private static ComponentSection Section(string title, IEnumerable<ComponentRow> rows, string? summary = null, string? note = null) =>
        new(title, rows.DefaultIfEmpty(new ComponentRow(L.Get("components.unavailable"))).ToArray(), summary, note);

    private static string? Spaced(params string?[] values)
    {
        var text = string.Join(' ', values.Where(v => !string.IsNullOrWhiteSpace(v)));
        return text.Length == 0 ? null : text;
    }

    private static string Detected(string? value) => string.IsNullOrWhiteSpace(value) ? L.Get("components.unavailable") : value;

    private static string? Join(params string?[] values)
    {
        var text = string.Join(" · ", values.Where(v => !string.IsNullOrWhiteSpace(v)));
        return text.Length == 0 ? null : text;
    }
}
