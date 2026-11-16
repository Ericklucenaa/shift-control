using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShiftControl.Diagnostics;
using ShiftControl.Maintenance;
using ShiftControl.Setup;
using ShiftControl.Storage;

namespace ShiftControl.Tests;

public class MaintenanceTests
{
    [Fact]
    public void Device_classes_are_case_insensitive()
    {
        var driver = new DriverInfo("GPU", "DISPLAY", "NVIDIA", null, "1.0", null, null, true, null, 0);
        Assert.True(driver.Important);
        Assert.True((driver with { DeviceClass = "Display" }).Important);
        Assert.True((driver with { ErrorCode = 28 }).MissingDriver);
        Assert.False((driver with { ErrorCode = 22 }).HasProblem);
    }
    [Theory]
    [InlineData("32.0.16.2114", "621.14")]
    [InlineData("31.0.15.5123", "551.23")]
    [InlineData(null, null)]
    [InlineData("broken", null)]
    public void Nvidia_version_conversion(string? input, string? expected) => Assert.Equal(expected, Drivers.NvidiaVersion(input));

    [Fact]
    public void Nvidia_matches_exact_model_and_series()
    {
        const string xml = "<root><LookupValue ParentID='131'><Name>GeForce RTX 5070 Ti</Name><Value>1</Value></LookupValue><LookupValue ParentID='131'><Name>GeForce RTX 5070</Name><Value>1070</Value></LookupValue></root>";
        Assert.Equal(("131", "1070"), Drivers.MatchNvidiaProduct(xml, "NVIDIA GeForce RTX 5070"));
        Assert.Equal((null, null), Drivers.MatchNvidiaProduct(xml, "NVIDIA GeForce RTX 5070 Laptop GPU"));
    }

    [Fact]
    public void Power_profiles_only_choose_available_plans()
    {
        var plans = BoostEngine.ParsePlans($"GUID: {BoostEngine.Balanced} (Equilibrado) *\nGUID: {BoostEngine.HighPerformance} (Alto desempenho)\n");
        Assert.Equal(2, plans.Count);
        Assert.True(plans[0].Active);
        Assert.False(plans[1].Active);
        Assert.Equal(BoostEngine.HighPerformance, BoostEngine.PlanFor(BoostProfile.Competitive, plans));
        Assert.Equal(BoostEngine.Balanced, BoostEngine.PlanFor(BoostProfile.Quiet, plans));
        Assert.Null(BoostEngine.PlanFor(BoostProfile.Competitive, plans.Take(1).ToList()));
    }

    [Fact]
    public void Boost_preferences_exclude_protected_processes_and_deduplicate()
    {
        var settings = new GameBoostSettings { PauseApps = ["svchost.exe", "ShiftControl.exe", "DISCORD.EXE", "discord.exe", " "] }.Normalize();
        Assert.Equal(["discord.exe"], settings.PauseApps);
        settings.PerGame["GAME.EXE"] = BoostProfile.Quiet;
        Assert.Equal(BoostProfile.Quiet, settings.ProfileFor("game.exe"));
    }

    [Fact]
    public void Boost_state_survives_restart_and_corrupt_state_is_not_executed()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "boost.json");
        var state = new BoostState(BoostProfile.Competitive, "game.exe", BoostEngine.Balanced, BoostEngine.HighPerformance,
            [new SuspendedProcess(999, "example", 1234)], DateTimeOffset.UtcNow, 4);
        BoostEngine.SaveState(path, state);
        var restored = Assert.IsType<BoostState>(BoostEngine.LoadState(path));
        Assert.Equal(state.PreviousPlan, restored.PreviousPlan);
        Assert.Equal(state.Suspended, restored.Suspended);
        Assert.False(File.Exists(path + ".tmp"));
        File.WriteAllText(path, "{");
        Assert.Null(BoostEngine.LoadState(path));
        BoostEngine.ClearState(path);
        Assert.Null(BoostEngine.LoadState(path));
    }

    [Theory]
    [InlineData("Sfc", 0, "Windows Resource Protection did not find any integrity violations.", "Healthy")]
    [InlineData("Sfc", 0, "A Proteção de Recursos do Windows encontrou arquivos corrompidos e reparou-os com êxito.", "Repaired")]
    [InlineData("Sfc", 0, "Windows Resource Protection found corrupt files but was unable to fix some of them.", "ProblemsRemaining")]
    [InlineData("Sfc", 0, "Unknown result", "Failed")]
    [InlineData("DismRestoreHealth", 3010, "", "RebootRequired")]
    [InlineData("DismRestoreHealth", 5, "", "Failed")]
    [InlineData("ChkdskScan", 3, "", "ProblemsRemaining")]
    public void Repair_does_not_claim_unconfirmed_success(string tool, int exit, string output, string expected) =>
        Assert.Equal(Enum.Parse<RepairOutcome>(expected), Repair.Interpret(Enum.Parse<RepairTool>(tool), exit, output));

    [Fact]
    public void Cleanup_preserves_recent_temp_and_unrelated_explorer_files()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "file.tmp");
        File.WriteAllText(path, "test");
        Assert.False(Cleaner.Eligible(CleanCategory.UserTemp, new FileInfo(path), DateTime.Now));
        File.SetLastWriteTime(path, DateTime.Now.AddDays(-2));
        Assert.True(Cleaner.Eligible(CleanCategory.UserTemp, new FileInfo(path), DateTime.Now));
        Assert.False(Cleaner.Eligible(CleanCategory.Thumbnails, new FileInfo(Path.Combine(dir.Path, "thumbcache_personal.txt")), DateTime.Now));
        Assert.True(Cleaner.Eligible(CleanCategory.Thumbnails, new FileInfo(Path.Combine(dir.Path, "thumbcache_32.db")), DateTime.Now));
        Assert.False(Cleaner.IsUnderRoot(dir.Path + "-other\\file", [dir.Path]));
        Assert.False(Cleaner.SafeRoot(Path.GetPathRoot(dir.Path)!));
        Assert.True(Cleaner.SafeFile(path, [dir.Path]));
    }

    [Fact]
    public void Cleanup_actually_deletes_only_eligible_test_files()
    {
        using var dir = new TempDir();
        string old = Path.Combine(dir.Path, "old.tmp");
        string recent = Path.Combine(dir.Path, "recent.tmp");
        File.WriteAllBytes(old, new byte[42]);
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-2));
        File.WriteAllText(recent, "preserve");
        var result = Cleaner.CleanFiles(CleanCategory.UserTemp, [dir.Path], TestContext.Current.CancellationToken);
        Assert.Equal(42, result.FreedBytes);
        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Session_with_boost_changed_midgame_is_marked_mixed()
    {
        var session = new ShiftControl.Overlay.SessionAggregator("game.exe", "Game", DateTimeOffset.UtcNow);
        session.ObserveBoost(null);
        session.ObserveBoost("Competitive");
        session.ObserveBoost(null);
        Assert.Equal("Mixed", session.Boost);
    }

    [Fact]
    public void Storage_scan_counts_nested_sizes_and_limits_largest_files()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "nested"));
        File.WriteAllBytes(Path.Combine(dir.Path, "a.bin"), new byte[20]);
        File.WriteAllBytes(Path.Combine(dir.Path, "nested", "b.bin"), new byte[50]);
        File.WriteAllBytes(Path.Combine(dir.Path, "nested", "c.bin"), new byte[30]);
        var scan = StorageAnalyzer.Scan(dir.Path, largestFiles: 2, ct: TestContext.Current.CancellationToken);
        Assert.Equal(100, scan.TotalBytes);
        Assert.Equal(80, scan.Children.First().Bytes);
        Assert.Equal([50L, 30L], scan.LargestFiles.Select(f => f.Bytes));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => StorageAnalyzer.Scan(dir.Path, ct: cts.Token));
    }

    [Fact]
    public void Startup_approval_and_quoted_command_parsing()
    {
        Assert.True(Startup.IsEnabled(null));
        Assert.True(Startup.IsEnabled([2, 0]));
        Assert.False(Startup.IsEnabled([3, 0]));
        Assert.Equal(@"C:\Program Files\Example\app.exe", Startup.ExecutableFrom("\"C:\\Program Files\\Example\\app.exe\" --start"));
    }

    [Fact]
    public void Ping_summary_preserves_loss_and_jitter()
    {
        var r = NetworkTest.Summarize("test", [10, 20, null, 30]);
        Assert.Equal(4, r.Sent);
        Assert.Equal(3, r.Received);
        Assert.Equal(25, r.LossPercent);
        Assert.Equal(20, r.AvgMs);
        Assert.Equal(10, r.JitterMs);
        Assert.Equal(NetworkTest.Quality.Poor, NetworkTest.Rate(r));
        Assert.Equal(NetworkTest.Quality.Offline, NetworkTest.Rate(NetworkTest.Summarize("test", [null])));
    }

    [Fact]
    public async Task Ping_loopback_and_cancellation()
    {
        var result = await NetworkTest.RunAsync("127.0.0.1", count: 2, intervalMs: 1, ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Received);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NetworkTest.RunAsync("127.0.0.1", ct: cts.Token));
    }

    [Fact]
    public async Task Migration_preferences_roundtrip_and_manifest_validation()
    {
        using var dir = new TempDir();
        var result = await Migration.CreateAsync(dir.Path, new(false, false, false, true), new WinGetClient(), "{\"app\":{}}", ct: TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(result).Ok);
        Assert.True(Migration.Read(dir.Path)!.AppSettings);
        Assert.Equal("{\"app\":{}}", Migration.AppSettings(dir.Path));
        File.WriteAllText(Path.Combine(dir.Path, Migration.ManifestFile), "{\"Version\":999}");
        Assert.Null(Migration.Read(dir.Path));
    }

    [Theory]
    [InlineData("evil", "Example.App")]
    [InlineData("winget", "--override")]
    [InlineData("winget", "invalid id")]
    public void Migration_rejects_untrusted_package_sources_and_invalid_ids(string source, string id)
    {
        string kit = KitFile.Write([new(id, source)], DateTimeOffset.Now);
        Assert.Throws<InvalidDataException>(() => KitFile.Read(kit));
    }

    [Fact]
    public void Event_stores_persist_nullable_data_and_clear_independently()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "test.db");
        using (var db = new ShiftDatabase(path))
        {
            new AlertStore(db).Add("disk.health", "Critical", null, "SSD");
            new PingTestStore(db).Save(new(0, DateTimeOffset.UtcNow, "local", 3, 0, null, null, null, null));
        }
        using var reopened = new ShiftDatabase(path);
        var alerts = new AlertStore(reopened);
        var pings = new PingTestStore(reopened);
        Assert.Null(Assert.Single(alerts.List()).Value);
        Assert.Equal(100, Assert.Single(pings.List()).LossPercent);
        alerts.Clear();
        Assert.Empty(alerts.List());
        Assert.Single(pings.List());
    }
}

public class AlertThermalTests
{
    private static AlertInput Input(double? temp) => new(temp, null, null, [], []);

    [Fact]
    public void Alerts_sustain_reset_and_cooldown()
    {
        var evaluator = new AlertEvaluator();
        var settings = new AlertSettings { SustainSeconds = 20, CooldownMinutes = 1 };
        var at = DateTimeOffset.UtcNow;
        Assert.Empty(evaluator.Evaluate(Input(95), settings, at));
        Assert.Empty(evaluator.Evaluate(Input(null), settings, at.AddSeconds(19)));
        Assert.Empty(evaluator.Evaluate(Input(95), settings, at.AddSeconds(21)));
        Assert.Single(evaluator.Evaluate(Input(95), settings, at.AddSeconds(41)));
        Assert.Empty(evaluator.Evaluate(Input(95), settings, at.AddSeconds(42)));
        Assert.Single(evaluator.Evaluate(Input(95), settings, at.AddSeconds(101)));
    }

    [Fact]
    public void Disabled_alerts_do_not_accumulate_duration()
    {
        var evaluator = new AlertEvaluator();
        var settings = new AlertSettings { SustainSeconds = 20 };
        var at = DateTimeOffset.UtcNow;
        evaluator.Evaluate(Input(100), settings, at);
        settings.Enabled = false;
        evaluator.Evaluate(Input(100), settings, at.AddSeconds(30));
        settings.Enabled = true;
        Assert.Empty(evaluator.Evaluate(Input(100), settings, at.AddSeconds(60)));
    }

    [Fact]
    public void Thermal_analysis_requires_measured_clocks_and_distinguishes_hot_drop()
    {
        Assert.Equal(ThermalVerdict.NotEnoughData, ThermalAnalyzer.Analyze("CPU", [], 95).Verdict);
        var steady = Enumerable.Range(0, 120).Select(s => new ThermalSample(s, 90, 80, 4000)).ToList();
        Assert.Equal(ThermalVerdict.NoLimitation, ThermalAnalyzer.Analyze("CPU", steady, 95).Verdict);
        var hot = steady.Select(s => s.Second >= 80 ? s with { Temp = 97, Clock = 3000 } : s).ToList();
        var evidence = ThermalAnalyzer.Analyze("CPU", hot, 95);
        Assert.Equal(ThermalVerdict.Limited, evidence.Verdict);
        Assert.Equal(40, evidence.ThrottledSeconds);
        Assert.Equal(25, evidence.ClockDropPercent);
    }
}

public class ReleaseTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    [Theory]
    [InlineData("0.1.0", "0.1", false)]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("0.2.0-beta", "0.1.0", false)]
    [InlineData("invalid", "0.1.0", false)]
    public void Release_versions_compare_normalized_components(string candidate, string current, bool expected) => Assert.Equal(expected, ReleaseClient.IsNewer(candidate, current));

    [Fact]
    public async Task Unpublished_release_is_explicit()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound)));
        var result = await new ReleaseClient(http, "owner/repo").CheckAsync("0.1.0", TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseStatus.NoReleases, result.Status);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task Update_download_verifies_hash_and_removes_corrupt_payload()
    {
        using var dir = new TempDir();
        byte[] payload = Encoding.UTF8.GetBytes("test installer content");
        string hash = Convert.ToHexString(SHA256.HashData(payload));
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var client = new ReleaseClient(http, "owner/repo");
        var update = new ReleaseUpdate("0.2.0", "https://github.com/owner/repo/releases/tag/v0.2.0", "https://github.com/owner/repo/releases/download/v0.2.0/ShiftControl-setup.exe", null, hash);
        string path = await client.DownloadAsync(update, dir.Path, ct: TestContext.Current.CancellationToken);
        Assert.Equal(payload, File.ReadAllBytes(path));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(update with { Sha256 = new string('0', 64) }, dir.Path, ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(update with { InstallerUrl = "https://example.com/bad.exe" }, dir.Path, ct: TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Release_metadata_selects_only_official_installer()
    {
        string json = JsonSerializer.Serialize(new { tag_name = "v0.2.0", html_url = "https://github.com/owner/repo/releases/tag/v0.2.0", assets = new[] { new { name = "ShiftControl-setup.exe", browser_download_url = "https://github.com/owner/repo/releases/download/v0.2.0/ShiftControl-setup.exe", digest = "sha256:" + new string('a', 64) } } });
        using var http = new HttpClient(new Handler(request => request.RequestUri!.Host == "github.com"
            ? new(HttpStatusCode.NotFound)
            : new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        var result = await new ReleaseClient(http, "owner/repo").CheckAsync("0.1.0", TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseStatus.Available, result.Status);
        Assert.Equal(new string('a', 64), result.Update!.Sha256);
    }
}
