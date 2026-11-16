using ShiftControl.Diagnostics;
using ShiftControl.Hardware;
using ShiftControl.Setup;
using ShiftControl.Storage;

namespace ShiftControl.Tests;

public class CatalogTests
{
    [Fact]
    public void Catalog_loads_with_unique_ids_valid_categories_and_profiles()
    {
        var c = Catalog.Load();
        Assert.True(c.Packages.Count >= 30);
        Assert.Equal(c.Packages.Count, c.Packages.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(c.Packages, p => Assert.Contains(p.Category, c.Categories));
        Assert.All(c.Packages.SelectMany(p => p.Profiles), k => Assert.Contains(k, c.Profiles.Select(x => x.Key)));
        Assert.All(c.Packages, p => Assert.Contains(p.Source, new[] { "winget", "msstore" }));
    }

    [Fact]
    public void User_installers_are_covered_by_official_packages()
    {
        var c = Catalog.Load();
        foreach (var id in new[] { "Google.Chrome", "Opera.OperaGX", "Discord.Discord", "Microsoft.Teams", "Valve.Steam",
                     "EpicGames.EpicGamesLauncher", "ElectronicArts.EADesktop", "XP8CLZL93F5Z4P", "IObit.DriverBooster",
                     "Skillbrains.Lightshot", "RARLab.WinRAR", "Microsoft.VCRedist.2015+.x64", "Microsoft.DirectX" })
            Assert.NotNull(c.Find(id));
        Assert.Equal("msstore", c.Find("XP8CLZL93F5Z4P")!.Source);
        Assert.Equal("nvidia", c.Find("XP8CLZL93F5Z4P")!.Requires);
    }

    [Fact]
    public void WebView2_is_detected_from_the_registry_on_this_pc()
    {
        // O WinGet não lista o WebView2; a versão fica na chave do atualizador do Edge.
        var found = RegistryDetection.Find(Catalog.Load().Find("Microsoft.EdgeWebView2Runtime")!);
        Assert.NotNull(found);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", found!.Version);
        Assert.Null(RegistryDetection.Find(Catalog.Load().Find("7zip.7zip")!));
    }

    [Fact]
    public void Gamer_profile_includes_runtimes_and_launchers()
    {
        var gamer = Catalog.Load().ForProfile("gamer").Select(p => p.Id).ToList();
        Assert.Contains("Microsoft.VCRedist.2015+.x64", gamer);
        Assert.Contains("Valve.Steam", gamer);
        Assert.DoesNotContain("IObit.DriverBooster", gamer);
    }
}

public class WinGetOutputTests
{
    // Saída real de "winget list" neste PC (pt-BR), recortada.
    private static readonly string[] Sample =
    [
        "   - ",
        "Nome                                                           ID                                                    Versão              Disponível           Origem",
        "-----------------------------------------------------------------------------------------------------------------------------------------------------------------",
        "Discord                                                        ARP\\User\\X64\\Discord                                 1.0.9268                                 ",
        "EA app                                                         ElectronicArts.EADesktop                              13.827.2.6342       13.840.0.6400        winget",
        "Google Chrome                                                  ARP\\Machine\\X86\\Google Chrome                        157.0.8103.78                            ",
        "NVIDIA App 11.0.9.585                                          ARP\\Machine\\X64\\{B2FE1952}_Display.NvApp              11.0.9.585                               ",
        "Python 3.12.10 (64-bit)                                        Python.Python.3.12                                    3.12.10                                  winget",
        "Steam                                                          Valve.Steam                                           2.10.91.91                               winget",
    ];

    [Fact]
    public void Parses_localized_table_by_header_positions()
    {
        var list = WinGetListParser.Parse(Sample);
        Assert.Equal(6, list.Count);
        var ea = list.Single(e => e.Id == "ElectronicArts.EADesktop");
        Assert.Equal("EA app", ea.Name);
        Assert.Equal("13.827.2.6342", ea.Version);
        Assert.Equal("13.840.0.6400", ea.Available);
        Assert.Equal("winget", ea.Source);
        Assert.Null(list.Single(e => e.Name == "Steam").Available);
    }

    [Fact]
    public void Matches_by_id_or_by_installed_name()
    {
        var installed = WinGetListParser.Parse(Sample);
        var c = Catalog.Load();
        Assert.Equal("Valve.Steam", WinGetListParser.Match(c.Find("Valve.Steam")!, installed)!.Id);
        Assert.Equal("Google Chrome", WinGetListParser.Match(c.Find("Google.Chrome")!, installed)!.Name);      // instalado fora do WinGet
        Assert.NotNull(WinGetListParser.Match(c.Find("XP8CLZL93F5Z4P")!, installed));                         // "NVIDIA App 11.0.9.585"
        Assert.NotNull(WinGetListParser.Match(c.Find("Python.Python.3.13")!, installed));                     // qualquer Python 3 conta
        Assert.Null(WinGetListParser.Match(c.Find("7zip.7zip")!, installed));
    }

    [Fact]
    public void Prefix_match_does_not_confuse_similar_names()
    {
        var installed = new List<InstalledEntry> { new("Steamworks Common Redistributables", "ARP\\X", "1", null, "") };
        Assert.Null(WinGetListParser.Match(Catalog.Load().Find("Valve.Steam")!, installed));
    }

    [Theory]
    [InlineData(0, WinGetOutcome.Success)]
    [InlineData(unchecked((int)0x8A150061), WinGetOutcome.AlreadyInstalled)]
    [InlineData(unchecked((int)0x8A15002B), WinGetOutcome.NoUpdate)]
    [InlineData(unchecked((int)0x8A150014), WinGetOutcome.NotFound)]
    [InlineData(unchecked((int)0x8A150109), WinGetOutcome.RebootRequired)]
    [InlineData(unchecked((int)0x8A150101), WinGetOutcome.AppRunning)]
    [InlineData(unchecked((int)0x8A15010C), WinGetOutcome.Cancelled)]
    [InlineData(unchecked((int)0x8A150107), WinGetOutcome.NoNetwork)]
    [InlineData(1603, WinGetOutcome.Failed)]
    public void Exit_codes(int code, WinGetOutcome expected) => Assert.Equal(expected, WinGetCodes.Map(code));

    [Theory]
    [InlineData("  ██████████▒▒▒▒▒▒  45.2 MB / 98.0 MB", "45.2 MB / 98.0 MB")]
    [InlineData("\\", null)]
    [InlineData("Instalando o pacote...", "Instalando o pacote...")]
    [InlineData("   ", null)]
    public void Progress_lines(string line, string? expected) => Assert.Equal(expected, WinGetClient.Progress(line));

    [Fact]
    public void Install_arguments_are_silent_exact_and_from_the_right_source()
    {
        var args = WinGetClient.InstallArgs(Catalog.Load().Find("XP8CLZL93F5Z4P")!);
        Assert.Equal(["install", "--id", "XP8CLZL93F5Z4P", "--exact", "--source", "msstore", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"], args);
    }
}

public class KitFileTests
{
    [Fact]
    public void Round_trip_in_winget_export_format()
    {
        var json = KitFile.Write([new("Valve.Steam", "winget"), new("XP8CLZL93F5Z4P", "msstore"), new("valve.steam", "winget")], DateTimeOffset.UnixEpoch);
        Assert.Contains("winget-packages.schema.2.0.json", json);
        Assert.Contains("StoreEdgeFD", json);
        var back = KitFile.Read(json);
        Assert.Equal(2, back.Count);
        Assert.Contains(back, e => e is { Id: "XP8CLZL93F5Z4P", Source: "msstore" });
    }

    [Fact]
    public void Reads_a_real_winget_export_and_rejects_garbage()
    {
        const string exported = """
            {"$schema":"https://aka.ms/winget-packages.schema.2.0.json","CreationDate":"2026-11-15T12:00:00.000-00:00",
             "Sources":[{"Packages":[{"PackageIdentifier":"Valve.Steam","Version":"2.10.91.91"},{"PackageIdentifier":"RARLab.WinRAR"}],
             "SourceDetails":{"Argument":"https://cdn.winget.microsoft.com/cache","Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Name":"winget","Type":"Microsoft.PreIndexed.Package"}}],
             "WinGetVersion":"1.29.380"}
            """;
        Assert.Equal(["Valve.Steam", "RARLab.WinRAR"], KitFile.Read(exported).Select(e => e.Id));
        Assert.Throws<InvalidDataException>(() => KitFile.Read("not json"));
        Assert.Throws<InvalidDataException>(() => KitFile.Read("{\"x\":1}"));
    }
}

public class InstallQueueTests
{
    private sealed class FakeRunner(Func<IReadOnlyList<string>, int> exit) : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(string.Join(' ', args));
            onLine?.Invoke("  ████▒▒  10 MB / 20 MB");
            return Task.FromResult(new ProcessResult(exit(args), "Encontrado X\nInstalado com êxito"));
        }
    }

    private static InstallJob Job(string id) => new() { Package = Catalog.Load().Find(id)!, Kind = JobKind.Install };

    [Fact]
    public async Task Failures_do_not_stop_the_queue_and_successes_are_reported()
    {
        var runner = new FakeRunner(args => args[2] == "Discord.Discord" ? 1603 : args[2] == "Valve.Steam" ? unchecked((int)0x8A150061) : 0);
        var queue = new InstallQueue(new WinGetClient(runner));
        var installed = new List<string>();
        queue.Installed += j => installed.Add(j.Package.Id);
        var jobs = new[] { Job("Discord.Discord"), Job("Valve.Steam"), Job("7zip.7zip") };

        var report = await queue.RunAsync(jobs, TestContext.Current.CancellationToken);

        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(JobState.Failed, jobs[0].State);
        Assert.Equal(JobState.Done, jobs[1].State);
        Assert.Equal(WinGetOutcome.AlreadyInstalled, jobs[1].Outcome);
        Assert.Equal(["7zip.7zip"], installed); // "já instalado" não vai para a Central de desfazer
        Assert.Equal(2, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Equal("10 MB / 20 MB", jobs[2].LastLine);
    }

    [Fact]
    public async Task Cancel_marks_remaining_jobs()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var jobs = new[] { Job("7zip.7zip"), Job("VideoLAN.VLC") };
        await new InstallQueue(new WinGetClient(new FakeRunner(_ => 0))).RunAsync(jobs, cts.Token);
        Assert.All(jobs, j => Assert.Equal(JobState.Cancelled, j.State));
    }
}

public class ChangeJournalTests
{
    [Fact]
    public void Records_lists_and_reverts()
    {
        using var dir = new TempDir();
        using var db = new ShiftDatabase(dir.File("j.db"));
        var j = new ChangeJournal(db);
        int events = 0;
        j.Changed += (_, _) => events++;

        long a = j.Record("format-kit", "Instalou 7-Zip", "7zip.7zip", "winget.uninstall", "7zip.7zip");
        long b = j.Record("checks", "Ponto de restauração criado", null, null, null);
        var list = j.List();
        Assert.Equal(2, list.Count);
        Assert.True(list.Single(e => e.Id == a).CanRevert);
        Assert.Equal(ChangeState.Irreversible, list.Single(e => e.Id == b).State);
        Assert.False(list.Single(e => e.Id == b).CanRevert);

        j.SetState(a, ChangeState.Reverted, "Desinstalado");
        var reverted = j.Get(a)!;
        Assert.Equal(ChangeState.Reverted, reverted.State);
        Assert.NotNull(reverted.RevertedAt);
        Assert.False(reverted.CanRevert);

        j.SetState(b, ChangeState.RevertFailed, "x");
        Assert.Equal(4, events);
    }
}

public class SystemChecksTests
{
    private static SystemFacts Facts(
        IReadOnlyList<DisplayFact>? displays = null, IReadOnlyList<MemoryModuleFact>? memory = null, IReadOnlyList<string>? gpus = null,
        GameSettingsFact? gaming = null, Guid? plan = null, bool? activated = true, int? updates = 0, IReadOnlyList<ProblemDevice>? problems = null) =>
        new(displays ?? [new DisplayFact(@"\\.\DISPLAY1", "NVIDIA GeForce RTX 5070", 2560, 1440, 165, 165, true)],
            memory ?? [new MemoryModuleFact(8UL << 30, 3000, 3000, "KHX3000C15D4/8GX", "Kingston")],
            gpus ?? ["NVIDIA GeForce RTX 5070", "Parsec Virtual Display Adapter"],
            gaming ?? new GameSettingsFact(true, true, true, true),
            "Equilibrado", plan ?? SystemChecks.Balanced, false, problems ?? [], activated, updates);

    private static CheckResult Get(IReadOnlyList<CheckResult> r, string code) => r.First(x => x.Code == code);

    [Fact]
    public void Healthy_gaming_pc_has_no_attention()
    {
        var r = SystemChecks.Evaluate(Facts());
        Assert.DoesNotContain(r, x => x.Status == CheckStatus.Attention);
    }

    [Fact]
    public void Monitor_below_maximum_refresh()
    {
        var r = SystemChecks.Evaluate(Facts(displays: [new DisplayFact(@"\\.\DISPLAY1", "NVIDIA GeForce RTX 5070", 1920, 1080, 60, 144, true)]));
        var c = Get(r, "display.refresh");
        Assert.Equal(CheckStatus.Attention, c.Status);
        Assert.Equal(["", "60", "144", "1920×1080"], c.Args);
    }

    [Fact]
    public void Virtual_displays_are_ignored()
    {
        var r = SystemChecks.Evaluate(Facts(displays:
        [
            new DisplayFact(@"\\.\DISPLAY1", "NVIDIA GeForce RTX 5070", 2560, 1440, 165, 165, true),
            new DisplayFact(@"\\.\DISPLAY9", "Parsec Virtual Display Adapter", 1920, 1080, 30, 240, false),
        ]));
        Assert.Single(r, x => x.Code == "display.refresh");
        Assert.Equal(CheckStatus.Ok, Get(r, "gpu.output").Status);
    }

    [Fact]
    public void Monitor_on_integrated_gpu_with_discrete_present()
    {
        var r = SystemChecks.Evaluate(Facts(
            displays: [new DisplayFact(@"\\.\DISPLAY1", "AMD Radeon(TM) Graphics", 1920, 1080, 60, 60, true)],
            gpus: ["AMD Radeon(TM) Graphics", "NVIDIA GeForce RTX 4060"]));
        var c = Get(r, "gpu.output");
        Assert.Equal(CheckStatus.Attention, c.Status);
        Assert.Equal("NVIDIA GeForce RTX 4060", c.Args[2]);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070", GpuKind.Discrete)]
    [InlineData("AMD Radeon RX 7800 XT", GpuKind.Discrete)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", GpuKind.Discrete)]
    [InlineData("AMD Radeon(TM) Graphics", GpuKind.Integrated)]
    [InlineData("Intel(R) UHD Graphics 770", GpuKind.Integrated)]
    [InlineData("Parsec Virtual Display Adapter", GpuKind.Virtual)]
    [InlineData("Microsoft Basic Display Adapter", GpuKind.Virtual)]
    public void Gpu_classification(string name, GpuKind kind) => Assert.Equal(kind, SystemChecks.Classify(name));

    [Theory]
    [InlineData("KHX3000C15D4/8GX", 3000)]
    [InlineData("CMK16GX4M2B3200C16", 3200)]
    [InlineData("F4-3600C16D-16GVKC", 3600)]
    [InlineData("M378A1K43EB2-CWE", null)]
    [InlineData("SUPER FRAME", null)]
    [InlineData(null, null)]
    public void Rated_speed_from_part_number(string? part, int? rated) => Assert.Equal(rated, SystemChecks.RatedFromPartNumber(part));

    [Fact]
    public void Memory_running_at_jedec_while_kit_is_faster()
    {
        var mem = new[] { new MemoryModuleFact(16UL << 30, 2133, 2133, "F4-3600C16D-16GVKC", "G.Skill") };
        var c = SystemChecks.EvaluateMemory(mem);
        Assert.Equal(CheckStatus.Attention, c.Status);
        Assert.Equal(["2133", "3600", "part", "F4-3600C16D-16GVKC"], c.Args);

        var smbios = SystemChecks.EvaluateMemory([new MemoryModuleFact(8UL << 30, 3200, 2400, null, null)]);
        Assert.Equal(CheckStatus.Attention, smbios.Status);
        Assert.Equal("smbios", smbios.Args[2]);

        Assert.Equal(CheckStatus.Unknown, SystemChecks.EvaluateMemory([]).Status);
    }

    [Fact]
    public void Gaming_settings_and_power_plan()
    {
        var off = SystemChecks.Evaluate(Facts(gaming: new GameSettingsFact(false, false, false, null), plan: SystemChecks.PowerSaver));
        Assert.Equal(CheckStatus.Attention, Get(off, "gaming.gamemode").Status);
        Assert.Equal(CheckStatus.Attention, Get(off, "gaming.hags").Status); // RTX 50: necessário para geração de quadros
        Assert.Equal(CheckStatus.Attention, Get(off, "gaming.windowed").Status);
        Assert.Equal(CheckStatus.Info, Get(off, "gaming.vrr").Status);
        Assert.Equal(CheckStatus.Attention, Get(off, "power.plan").Status);

        var gtx = SystemChecks.Evaluate(Facts(gpus: ["NVIDIA GeForce GTX 1660"], gaming: new GameSettingsFact(true, false, true, true)));
        Assert.Equal(CheckStatus.Info, Get(gtx, "gaming.hags").Status); // sem recurso que dependa dele: só informa
    }

    [Fact]
    public void Post_format_checklist()
    {
        var r = SystemChecks.Evaluate(Facts(activated: false, updates: 7,
            problems: [new ProblemDevice("Controlador Ethernet", 28, "Net"), new ProblemDevice("Desativado", 22, null)]));
        Assert.Equal(CheckStatus.Attention, Get(r, "post.activation").Status);
        Assert.Equal(["7"], Get(r, "post.updates").Args);
        var dev = Get(r, "post.devices");
        Assert.Equal(CheckStatus.Attention, dev.Status);
        Assert.Equal("1", dev.Args[0]); // o desativado pelo usuário (22) não conta
        Assert.Equal(CheckStatus.Unknown, Get(SystemChecks.Evaluate(Facts(activated: null, updates: null)), "post.updates").Status);
    }

    [Fact]
    public void Probe_helpers()
    {
        Assert.Equal(("Equilibrado", (Guid?)SystemChecks.Balanced),
            SystemFactsProbe.ParsePowerPlan("GUID do Esquema de Energia: 381b4222-f694-41f0-9685-ff5bb260df2e  (Equilibrado)"));
        Assert.True(SystemFactsProbe.Flag("SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;", "SwapEffectUpgradeEnable"));
        Assert.False(SystemFactsProbe.Flag("SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;", "VRROptimizeEnable"));
        Assert.Null(SystemFactsProbe.Flag(null, "VRROptimizeEnable"));
    }
}

[Trait("Category", "Integration")]
public class SystemFactsIntegrationTests
{
    [Fact]
    public void Reads_this_pc_without_changing_anything()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var f = SystemFactsProbe.ReadFast();
        TestContext.Current.SendDiagnosticMessage($"leitura rápida em {sw.ElapsedMilliseconds} ms");
        foreach (var d in f.Displays) TestContext.Current.SendDiagnosticMessage($"monitor {d.DeviceName} {d.AdapterName} {d.Width}x{d.Height} {d.CurrentHz}/{d.MaxHzAtResolution} Hz");
        foreach (var r in SystemChecks.Evaluate(f)) TestContext.Current.SendDiagnosticMessage($"{r.Code}: {r.Status} [{string.Join(", ", r.Args)}]");
        Assert.NotEmpty(f.Displays);
        Assert.NotEmpty(f.Memory);
        Assert.NotNull(f.PowerPlanGuid);
        Assert.True(sw.ElapsedMilliseconds < 10_000);
    }
}
