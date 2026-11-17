using Microsoft.Win32;
using ShiftControl.Maintenance;

namespace ShiftControl.Tests;

public class ProgramsTests
{
    private static InstalledProgram P(string name, string uninstall = @"C:\Windows\System32\cmd.exe", string? location = null,
        string key = "TestKey", string? publisher = null) =>
        new(ProgramScope.User, key, name, "1.0", publisher, null, location, null, uninstall, null, null, null);

    private static InstalledProgram? From(string key, Dictionary<string, object> values) =>
        InstalledPrograms.FromValues(ProgramScope.Machine, key, n => values.GetValueOrDefault(n));

    [Fact]
    public void Registry_entries_hide_components_updates_and_entries_without_uninstaller()
    {
        var ok = new Dictionary<string, object> { ["DisplayName"] = "App", ["UninstallString"] = "\"C:\\App\\unins.exe\"" };
        Assert.NotNull(From("App", ok));
        Assert.Null(From("App", new(ok) { ["SystemComponent"] = 1 }));
        Assert.Null(From("App", new(ok) { ["ParentKeyName"] = "Office" }));
        Assert.Null(From("App", new(ok) { ["ReleaseType"] = "Security Update" }));
        Assert.Null(From("App", new() { ["DisplayName"] = "App" }));
        Assert.Null(From("App", new() { ["UninstallString"] = "x.exe" }));
    }

    [Fact]
    public void Msi_products_use_the_product_code_and_values_are_parsed()
    {
        const string code = "{A36273C3-B90A-420D-AF67-D2F490864B7D}";
        var p = From(code, new() { ["DisplayName"] = "Auto Macro Recorder", ["UninstallString"] = "MsiExec.exe /I" + code, ["WindowsInstaller"] = 1,
            ["InstallDate"] = "20261116", ["EstimatedSize"] = 2048 })!;
        Assert.Equal(code, p.MsiProductCode);
        Assert.Equal(new DateTime(2026, 11, 16), p.InstalledOn);
        Assert.Equal(2048 * 1024L, p.SizeBytes);
        // Comando sempre /x com o código, nunca o /I (reparo) do registro.
        var cmd = InstalledPrograms.Command(p, quiet: false)!;
        Assert.EndsWith("msiexec.exe", cmd.File, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/x " + code, cmd.Arguments);
        Assert.Equal("/x " + code + " /passive /norestart", InstalledPrograms.Command(p, quiet: true)!.Arguments);
        // MSI detectado pelo texto do desinstalador quando a chave não é o código.
        Assert.Equal(code, From("Other", new() { ["DisplayName"] = "X", ["UninstallString"] = "MsiExec.exe /X" + code.ToLowerInvariant() })!.MsiProductCode);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /SILENT", "C:\\Program Files\\App\\unins000.exe", "/SILENT")]
    [InlineData("C:\\Program Files\\App\\unins000.exe /SILENT", "C:\\Program Files\\App\\unins000.exe", "/SILENT")]
    [InlineData("C:\\Program Files (x86)\\App\\uninstall.exe", "C:\\Program Files (x86)\\App\\uninstall.exe", "")]
    [InlineData("rundll32.exe dfshim.dll,ShArpMaintain app", "rundll32.exe", "dfshim.dll,ShArpMaintain app")]
    public void Uninstall_strings_are_split_with_and_without_quotes(string line, string file, string args)
    {
        var cmd = InstalledPrograms.SplitCommand(line)!;
        Assert.Equal(file, cmd.File);
        Assert.Equal(args, cmd.Arguments);
    }

    [Fact]
    public void Missing_uninstaller_is_reported_and_shift_control_is_protected()
    {
        Assert.Null(InstalledPrograms.Command(P("Gone", @"C:\Nao\Existe\unins000.exe"), false));
        Assert.True(InstalledPrograms.IsProtected(P("Shift Control 0.3.6", key: "ShiftControl")));
        Assert.False(InstalledPrograms.IsProtected(P("Discord")));
        Assert.True(InstalledPrograms.IsSharedRuntime(P("Microsoft Visual C++ 2015-2022 Redistributable (x64)")));
        Assert.False(InstalledPrograms.IsSharedRuntime(P("7-Zip 24.08 (x64)")));
    }

    [Fact]
    public void Search_and_sort_find_by_name_or_publisher()
    {
        var all = new[] { P("Zeta", publisher: "Acme") with { SizeBytes = 10 }, P("alpha", publisher: "Other") with { SizeBytes = 99 } };
        Assert.Equal(["alpha", "Zeta"], InstalledPrograms.Query(all, "", ProgramSort.Name).Select(p => p.Name));
        Assert.Equal(["alpha", "Zeta"], InstalledPrograms.Query(all, "", ProgramSort.Size).Select(p => p.Name));
        Assert.Equal(["Zeta"], InstalledPrograms.Query(all, "acme", ProgramSort.Name).Select(p => p.Name));
    }

    [Fact]
    public void Candidate_names_drop_version_and_architecture()
    {
        var names = InstalledPrograms.CandidateNames(P("Python 3.12.0 (64-bit)", key: "{11111111-2222-3333-4444-555555555555}"));
        Assert.Contains("Python", names);
        Assert.DoesNotContain(names, n => n.StartsWith('{'));
        Assert.Empty(InstalledPrograms.CandidateNames(P("Microsoft", key: "Microsoft")));
    }

    [Fact]
    public void Leftover_scan_finds_exact_folders_and_skips_shared_protected_and_linked_ones()
    {
        using var dir = new TempDir();
        string root = Directory.CreateDirectory(Path.Combine(dir.Path, "ProgramFiles")).FullName;
        string mine = Directory.CreateDirectory(Path.Combine(root, "Cool App")).FullName;
        File.WriteAllBytes(Path.Combine(mine, "settings.bin"), new byte[300]);
        string shared = Directory.CreateDirectory(Path.Combine(root, "Shared Tool")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(dir.Path, "Outside")).FullName;
        string link = Path.Combine(root, "Linked App");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
            { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit();
        var roots = new LeftoverRoots([root], [], [root]);
        var others = new[] { P("Shared Tool", location: shared, key: "Shared") };

        var found = InstalledPrograms.ScanLeftovers(P("Cool App 2.1"), others, roots);
        var folder = Assert.Single(found);
        Assert.Equal(mine, folder.Path);
        Assert.Equal(300, folder.Bytes);
        Assert.Empty(InstalledPrograms.ScanLeftovers(P("Shared Tool", key: "Gone"), others, roots));       // ainda usada por outro programa
        Assert.Empty(InstalledPrograms.ScanLeftovers(P("Linked App"), [], roots));                          // junção nunca é seguida
        Assert.Empty(InstalledPrograms.ScanLeftovers(P("X", location: root), [], roots));                   // a raiz em si é protegida
        Assert.Empty(InstalledPrograms.ScanLeftovers(P("Y", location: dir.Path), [], roots));               // pasta que contém uma protegida
        Directory.Delete(link);
    }

    [Fact]
    public async Task Registry_leftovers_are_backed_up_before_deletion_and_kept_when_shared()
    {
        string baseKey = @"Software\ShiftControlTests\" + Guid.NewGuid().ToString("N");
        using (var k = Registry.CurrentUser.CreateSubKey(baseKey + @"\Cool App")) k.SetValue("Theme", "dark");
        Registry.CurrentUser.CreateSubKey(baseKey + @"\Still Here")!.Dispose();
        try
        {
            using var dir = new TempDir();
            var roots = new LeftoverRoots([], [(RegistryHive.CurrentUser, RegistryView.Registry64, baseKey)], []);
            Assert.Empty(InstalledPrograms.ScanLeftovers(P("Still Here", key: "Gone"), [P("Still Here", key: "Other")], roots));
            var found = Assert.Single(InstalledPrograms.ScanLeftovers(P("Cool App"), [], roots));
            Assert.Equal(LeftoverKind.Registry, found.Kind);

            var result = await InstalledPrograms.RemoveLeftoversAsync([found], dir.Path, TestContext.Current.CancellationToken);
            Assert.Equal(1, result.Removed);
            Assert.Null(Registry.CurrentUser.OpenSubKey(baseKey + @"\Cool App"));
            string backup = Assert.Single(result.RegistryBackups);
            Assert.Contains("\"Theme\"=\"dark\"", File.ReadAllText(backup));

            // Arquivo do usuário comum (dono não é Administradores): a verificação padrão recusa importar.
            if (!Shell.IsElevated)
                Assert.Equal((0, 1), await InstalledPrograms.RestoreRegistryAsync([backup], dir.Path, ct: TestContext.Current.CancellationToken));
            // Fora da pasta de backups: recusado mesmo com confiança total.
            Assert.Equal((0, 1), await InstalledPrograms.RestoreRegistryAsync([backup], Path.Combine(dir.Path, "outra"), _ => true, TestContext.Current.CancellationToken));
            // Arquivo confiável dentro da pasta: a chave volta com o valor original.
            Assert.Equal((1, 0), await InstalledPrograms.RestoreRegistryAsync([backup], dir.Path, _ => true, TestContext.Current.CancellationToken));
            using var back = Registry.CurrentUser.OpenSubKey(baseKey + @"\Cool App");
            Assert.Equal("dark", back?.GetValue("Theme"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\ShiftControlTests", throwOnMissingSubKey: false); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uninstall_waits_for_detached_child_uninstallers_and_confirms_removal(bool detached)
    {
        // Programa falso no HKCU cujo "desinstalador" apaga a própria entrada. No modo detached ele dispara um processo filho
        // e sai na hora (como o Inno Setup); o filho só apaga a entrada ~5 s depois, mais que os 3 s de tolerância que o
        // UninstallAsync espera pelo registro: o teste só passa se o Job Object acompanhar o filho.
        string keyName = "ShiftControlTest-" + Guid.NewGuid().ToString("N");
        string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + keyName;
        string delete = $"reg delete \"HKCU\\{path}\" /f";
        string command = detached
            ? $"cmd.exe /c start \"\" /b cmd.exe /c \"ping -n 6 127.0.0.1 >nul & {delete} >nul\""
            : $"cmd.exe /c \"{delete} >nul\"";
        using (var k = Registry.CurrentUser.CreateSubKey(path)) { k.SetValue("DisplayName", "Shift Control Test App"[6..]); k.SetValue("UninstallString", command); }
        try
        {
            var program = InstalledPrograms.List().Single(p => p.KeyName == keyName);
            Assert.Equal(ProgramScope.User, program.Scope);
            var outcome = await InstalledPrograms.UninstallAsync(program, quiet: false, TestContext.Current.CancellationToken);
            Assert.Equal(UninstallResult.Removed, outcome.Result);
            Assert.False(InstalledPrograms.StillInstalled(program));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }
}
