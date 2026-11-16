using System.Text.Json;
using System.Text.RegularExpressions;
using ShiftControl.Core;

namespace ShiftControl.Tests;

/// <summary>Garante que toda chave de texto usada pela interface existe no pt-BR (idioma de referência).</summary>
public partial class LocalizationTests
{
    private static string AppDir([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "ShiftControl.App"));

    private static Dictionary<string, string> Strings() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppDir(), "Strings", "pt-BR.json")))!;

    [GeneratedRegex(@"Tr Key=([\w.\-]+)")] private static partial Regex XamlKey();
    [GeneratedRegex(@"L\.(?:Get|F)\(""([\w.\-]+)""")] private static partial Regex CodeKey();

    [Fact]
    public void Every_static_key_exists()
    {
        var strings = Strings();
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppDir(), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".xaml") || f.EndsWith(".cs")) && !f.Contains(@"\obj\") && !f.Contains(@"\bin\")))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in XamlKey().Matches(text).Concat(CodeKey().Matches(text)))
                // Prefixos ("formatKit.outcome." + valor) são famílias dinâmicas, cobertas pelo teste abaixo.
                if (!m.Groups[1].Value.EndsWith('.') && !strings.ContainsKey(m.Groups[1].Value))
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void Every_dynamic_key_family_is_complete()
    {
        var strings = Strings();
        var expected = new List<string>();
        expected.AddRange(Enum.GetNames<Unavailable>().Select(n => $"reason.{n}"));
        expected.AddRange(Enum.GetNames<HealthLevel>().Select(n => $"health.{n}"));
        expected.AddRange(Appearance.Themes.Select(n => "appearance.palette." + n));
        expected.AddRange(Appearance.Buttons.Select(n => "appearance.button." + n));
        expected.AddRange(Enum.GetNames<ShiftControl.Maintenance.ProcessSort>().Select(n => "tasks.sort." + n));
        expected.AddRange(Enum.GetNames<ShiftControl.Maintenance.EndProcessResult>().Select(n => "tasks.end." + n));
        expected.AddRange(Enum.GetNames<SensorKind>().Select(n => $"kind.{n}"));
        expected.AddRange(Enum.GetNames<HardwareCategory>().Select(n => $"category.{n}"));
        expected.AddRange(Enum.GetNames<DriveHealth>().Select(n => $"drive.health.{n}"));
        expected.AddRange(MetricKeys.All.Select(m => $"metric.{m.Key}"));
        expected.AddRange(AccentPalette.Options.Select(o => $"accent.{o.Key}"));
        expected.AddRange(Enum.GetNames<ShiftControl.Overlay.OverlayAnchor>().Select(n => $"overlay.anchor.{n}"));
        expected.AddRange(Enum.GetNames<ShiftControl.Overlay.OverlayMetric>().Select(n => $"overlay.metric.{n}"));
        expected.AddRange(ShiftControl.Overlay.OverlayStyles.All.SelectMany(s => new[] { $"overlay.style.{s.Key}", $"overlay.style.{s.Key}.desc" }));
        expected.AddRange(new[] { "volume.low", "drive.health", "drive.temp", "drive.life", "memory.high", "cpu.high", "cpu.sensors" }
            .Select(code => $"finding.{code}.title"));
        expected.AddRange(Enum.GetNames<ShiftControl.Setup.WinGetOutcome>().Select(n => $"formatKit.outcome.{n}"));
        expected.AddRange(Enum.GetNames<ShiftControl.Storage.ChangeState>().Select(n => $"undo.state.{n}"));
        expected.AddRange(Enum.GetNames<ShiftControl.Hardware.RestorePointResult>().Select(n => $"undo.restorePoint.{n}"));
        expected.AddRange(Enum.GetNames<ShiftControl.Diagnostics.CheckStatus>().Select(n => $"checks.status.{n}"));
        expected.AddRange(["checks.toggle.on", "checks.toggle.off", "checks.toggle.default", "undo.area.formatKit", "undo.area.system",
            "undo.confirm.winget.uninstall"]);

        // Toda combinação código × estado que a checagem produz (cenários bons, ruins e sem dados).
        var scenarios = new[]
        {
            new ShiftControl.Diagnostics.SystemFacts(
                [new("\\\\.\\DISPLAY1", "NVIDIA GeForce RTX 5070", 1920, 1080, 60, 144, true)],
                [new(8UL << 30, 3200, 2133, null, null)], ["NVIDIA GeForce RTX 5070", "Intel(R) UHD Graphics"],
                new(false, false, false, false), "Economia", ShiftControl.Diagnostics.SystemChecks.PowerSaver, true,
                [new("X", 28, null)], false, 3),
            new ShiftControl.Diagnostics.SystemFacts(
                [new("\\\\.\\DISPLAY1", "Intel(R) UHD Graphics", 1920, 1080, 60, 60, true)], [], ["Intel(R) UHD Graphics", "NVIDIA GeForce RTX 4060"],
                new(true, true, true, true), "Equilibrado", ShiftControl.Diagnostics.SystemChecks.Balanced, false, [], true, 0),
            new ShiftControl.Diagnostics.SystemFacts([], [], [], new(null, null, null, null), null, null, null, [], null, null),
        };
        foreach (var r in scenarios.SelectMany(ShiftControl.Diagnostics.SystemChecks.Evaluate))
            expected.Add($"check.{r.Code}.{r.Status}.title");
        expected.AddRange(["check.post.activation.checking", "check.post.updates.checking"]);
        expected.AddRange(Enum.GetNames<ShiftControl.Maintenance.CleanCategory>().SelectMany(n => new[] { "cleanup.cat." + n, "cleanup.catHint." + n }));
        expected.AddRange(Enum.GetNames<ShiftControl.Maintenance.RepairTool>().SelectMany(n => new[] { "repair.tool." + n, "repair.toolHint." + n, "repair.toolTime." + n }));
        expected.AddRange(Enum.GetNames<ShiftControl.Maintenance.RepairOutcome>().SelectMany(n => new[] { "repair.outcome." + n, "repair.outcomeDetail." + n }));
        expected.AddRange(Enum.GetNames<ShiftControl.Maintenance.ErrorKind>().SelectMany(n => new[] { "errors.kind." + n, "errors.kindShort." + n, "errors.explain." + n }));
        expected.AddRange(Enum.GetNames<ShiftControl.Maintenance.StartupSource>().Select(n => "startup.source." + n));
        expected.AddRange(Enum.GetNames<ShiftControl.Maintenance.NetworkTest.Quality>().SelectMany(n => new[] { "network.q." + n, "network.verdict." + n }));
        expected.AddRange(Enum.GetNames<ShiftControl.Diagnostics.ThermalVerdict>().Select(n => "boost.thermal.verdict." + n));
        expected.AddRange(Enum.GetNames<ShiftControl.Maintenance.BoostProfile>().SelectMany(n => new[] { "boost.profile." + n, "boost.profileHint." + n }));
        expected.AddRange(Enum.GetNames<ShiftControl.Maintenance.MigrationStep>().SelectMany(n => new[] { "migration.step." + n, "migration.count." + n, "migration.restore." + n, "migration.restoreAction." + n }));
        expected.AddRange(Enum.GetNames<ShiftControl.Hardware.RestorePointResult>().Select(n => "drivers.restorePoint." + n));
        expected.AddRange(["undo.confirm.boost.restore", "undo.confirm.startup.toggle", "boost.inactiveAuto", "boost.inactiveManual", "drivers.gpu.download", "drivers.gpu.page", "drivers.problem.missing", "drivers.problem.code", "startup.journalOn", "startup.journalOff", "startup.enabled", "startup.disabled", "migration.noFolder", "network.run"]);

        var missing = expected.Distinct().Where(k => !strings.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Faltam: " + string.Join(", ", missing));
    }

    [Fact]
    public void Format_placeholders_are_well_formed()
    {
        foreach (var (key, value) in Strings())
        {
            var ex = Record.Exception(() => string.Format(value, "a", "b", "c", "d", "e", "f", "g", "h"));
            Assert.True(ex is null, $"{key}: {ex?.Message}");
        }
    }
}
