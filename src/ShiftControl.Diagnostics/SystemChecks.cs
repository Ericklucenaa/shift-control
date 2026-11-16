using System.Text.RegularExpressions;

namespace ShiftControl.Diagnostics;

public sealed record DisplayFact(string DeviceName, string AdapterName, int Width, int Height, int CurrentHz, int MaxHzAtResolution, bool Primary);

public sealed record MemoryModuleFact(ulong CapacityBytes, int SpeedMts, int ConfiguredMts, string? PartNumber, string? Manufacturer);

public sealed record GameSettingsFact(bool? GameMode, bool? Hags, bool? WindowedOptimizations, bool? Vrr);

public sealed record ProblemDevice(string Name, int ErrorCode, string? DeviceClass);

/// <summary>Fatos coletados do Windows. Nulo = não foi possível ler (ou ainda não foi verificado).</summary>
public sealed record SystemFacts(
    IReadOnlyList<DisplayFact> Displays,
    IReadOnlyList<MemoryModuleFact> Memory,
    IReadOnlyList<string> GpuNames,
    GameSettingsFact Gaming,
    string? PowerPlanName,
    Guid? PowerPlanGuid,
    bool? RebootPending,
    IReadOnlyList<ProblemDevice> ProblemDevices,
    bool? WindowsActivated = null,
    int? PendingUpdates = null);

public enum CheckStatus { Ok, Attention, Info, Unknown }

public enum CheckGroup { Gamer, PostFormat }

/// <summary>
/// Resultado de uma verificação. <see cref="Code"/> identifica a regra (a interface traduz);
/// <see cref="Args"/> são os dados medidos que sustentam a conclusão; <see cref="Fix"/> abre o ajuste certo.
/// </summary>
public sealed record CheckResult(string Code, CheckGroup Group, CheckStatus Status, IReadOnlyList<string> Args, string? Fix);

public enum GpuKind { Discrete, Integrated, Virtual }

public static partial class SystemChecks
{
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>Código 22 = desativado pelo usuário; 45 = dispositivo desconectado. Nenhum dos dois é defeito.</summary>
    private static readonly int[] IgnoredDeviceErrors = [22, 45];

    public static GpuKind Classify(string adapterName)
    {
        string n = adapterName.ToLowerInvariant();
        if (n.Contains("parsec") || n.Contains("virtual") || n.Contains("basic display") || n.Contains("basic render")
            || n.Contains("remote") || n.Contains("citrix") || n.Contains("idd") || n.Contains("spacedesk"))
            return GpuKind.Virtual;
        if (n.Contains("nvidia") || n.Contains("radeon rx") || n.Contains("radeon pro") || n.Contains("intel(r) arc") || n.Contains("intel arc")
            || Regex.IsMatch(n, @"radeon\s*(r9|r7|hd\s*\d{4}|vii|\d{3,4}\s*xt)"))
            return GpuKind.Discrete;
        return GpuKind.Integrated;
    }

    /// <summary>Velocidade nominal escrita no código do módulo (ex.: KHX3000C15D4 → 3000, F4-3600C16 → 3600).</summary>
    public static int? RatedFromPartNumber(string? part)
    {
        if (string.IsNullOrWhiteSpace(part)) return null;
        foreach (Match m in RatedSpeed().Matches(part))
            if (int.TryParse(m.Value, out int v) && v is >= 1600 and <= 9600)
                return v;
        return null;
    }

    [GeneratedRegex(@"(?<!\d)\d{4}(?!\d)")]
    private static partial Regex RatedSpeed();

    public static IReadOnlyList<CheckResult> Evaluate(SystemFacts f)
    {
        var list = new List<CheckResult>();
        var realDisplays = f.Displays.Where(d => Classify(d.AdapterName) != GpuKind.Virtual).ToList();

        // Taxa do monitor
        if (realDisplays.Count == 0)
            list.Add(new("display.refresh", CheckGroup.Gamer, CheckStatus.Unknown, [], null));
        foreach (var d in realDisplays)
        {
            string label = Label(d, realDisplays);
            bool below = d.MaxHzAtResolution > d.CurrentHz + 1;
            list.Add(new("display.refresh", CheckGroup.Gamer, below ? CheckStatus.Attention : CheckStatus.Ok,
                [label, d.CurrentHz.ToString(), d.MaxHzAtResolution.ToString(), $"{d.Width}×{d.Height}"], "ms-settings:display-advanced"));
        }

        // Monitor na GPU integrada
        var kinds = f.GpuNames.Select(Classify).ToList();
        bool hasDiscrete = kinds.Contains(GpuKind.Discrete);
        if (hasDiscrete && realDisplays.Count > 0)
        {
            var onIntegrated = realDisplays.Where(d => Classify(d.AdapterName) == GpuKind.Integrated).ToList();
            if (onIntegrated.Count > 0)
                foreach (var d in onIntegrated)
                    list.Add(new("gpu.output", CheckGroup.Gamer, CheckStatus.Attention,
                        [Label(d, realDisplays), d.AdapterName, f.GpuNames.First(n => Classify(n) == GpuKind.Discrete)], null));
            else
                list.Add(new("gpu.output", CheckGroup.Gamer, CheckStatus.Ok,
                    [realDisplays.Select(d => d.AdapterName).Distinct().First()], null));
        }

        // Memória (XMP/EXPO)
        list.Add(EvaluateMemory(f.Memory));

        // Recursos de jogo do Windows
        list.Add(Toggle("gaming.gamemode", f.Gaming.GameMode, attentionWhenOff: true, "ms-settings:gaming-gamemode"));
        bool modernNvidia = f.GpuNames.Any(n => Regex.IsMatch(n, @"RTX\s*[45]0\d{2}", RegexOptions.IgnoreCase));
        list.Add(Toggle("gaming.hags", f.Gaming.Hags, attentionWhenOff: modernNvidia, "ms-settings:display-advancedgraphics"));
        list.Add(Toggle("gaming.windowed", f.Gaming.WindowedOptimizations, attentionWhenOff: true, "ms-settings:display-advancedgraphics"));
        list.Add(Toggle("gaming.vrr", f.Gaming.Vrr, attentionWhenOff: false, "ms-settings:display-advancedgraphics"));

        // Plano de energia
        CheckStatus power = f.PowerPlanGuid is null ? CheckStatus.Unknown
            : f.PowerPlanGuid == PowerSaver ? CheckStatus.Attention
            : CheckStatus.Ok;
        list.Add(new("power.plan", CheckGroup.Gamer, power, [f.PowerPlanName ?? "—"], "ms-settings:powersleep"));

        // Pós-formatação
        list.Add(new("post.activation", CheckGroup.PostFormat,
            f.WindowsActivated switch { true => CheckStatus.Ok, false => CheckStatus.Attention, null => CheckStatus.Unknown }, [], "ms-settings:activation"));
        list.Add(new("post.updates", CheckGroup.PostFormat,
            f.PendingUpdates switch { null => CheckStatus.Unknown, 0 => CheckStatus.Ok, _ => CheckStatus.Attention },
            [f.PendingUpdates?.ToString() ?? ""], "ms-settings:windowsupdate"));
        list.Add(new("post.reboot", CheckGroup.PostFormat,
            f.RebootPending switch { true => CheckStatus.Attention, false => CheckStatus.Ok, null => CheckStatus.Unknown }, [], "ms-settings:windowsupdate"));
        var problems = f.ProblemDevices.Where(p => !IgnoredDeviceErrors.Contains(p.ErrorCode)).ToList();
        list.Add(new("post.devices", CheckGroup.PostFormat, problems.Count == 0 ? CheckStatus.Ok : CheckStatus.Attention,
            [problems.Count.ToString(), string.Join("; ", problems.Take(6).Select(p => $"{p.Name} (código {p.ErrorCode})"))], "devmgmt.msc"));
        return list;
    }

    private static string Label(DisplayFact d, IReadOnlyList<DisplayFact> all) =>
        all.Count == 1 ? "" : (d.Primary ? "principal" : d.DeviceName.Replace(@"\\.\", ""));

    private static CheckResult Toggle(string code, bool? on, bool attentionWhenOff, string fix) => new(code, CheckGroup.Gamer,
        on switch
        {
            true => CheckStatus.Ok,
            false => attentionWhenOff ? CheckStatus.Attention : CheckStatus.Info,
            null => CheckStatus.Info,
        }, [on switch { true => "on", false => "off", null => "default" }], fix);

    public static CheckResult EvaluateMemory(IReadOnlyList<MemoryModuleFact> modules)
    {
        var valid = modules.Where(m => m.ConfiguredMts > 0).ToList();
        if (valid.Count == 0) return new("memory.xmp", CheckGroup.Gamer, CheckStatus.Unknown, [], null);

        int configured = valid.Min(m => m.ConfiguredMts);
        int smbios = valid.Max(m => m.SpeedMts);
        int? part = valid.Select(m => RatedFromPartNumber(m.PartNumber)).Where(v => v is not null).Max();
        string partText = valid.Select(m => m.PartNumber?.Trim()).FirstOrDefault(p => RatedFromPartNumber(p) is not null) ?? "";

        // 1) A placa-mãe informa uma velocidade maior que a configurada: certeza de que está abaixo.
        if (smbios > configured + 50)
            return new("memory.xmp", CheckGroup.Gamer, CheckStatus.Attention, [configured.ToString(), smbios.ToString(), "smbios", ""], null);
        // 2) O código do módulo indica velocidade maior: muito provável que o XMP/EXPO esteja desligado.
        if (part is { } rated && rated > configured + 50)
            return new("memory.xmp", CheckGroup.Gamer, CheckStatus.Attention, [configured.ToString(), rated.ToString(), "part", partText], null);
        return new("memory.xmp", CheckGroup.Gamer, CheckStatus.Ok, [configured.ToString(), (part ?? smbios).ToString(), part is null ? "smbios" : "part", partText], null);
    }
}
