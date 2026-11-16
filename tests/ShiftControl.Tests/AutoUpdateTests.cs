using ShiftControl.Maintenance;

namespace ShiftControl.Tests;

public class AutoUpdateTests
{
    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, false, false, true, false)]
    public void Automatic_install_waits_for_safe_state(bool enabled, bool game, bool busy, bool dialog, bool expected) =>
        Assert.Equal(expected, UpdateInstallation.CanApply(enabled, game, busy, dialog));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Installer_updates_same_directory_and_preserves_portable_mode(bool installed)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Shift Control Test");
        var start = UpdateInstallation.StartInfo(Path.Combine(Path.GetTempPath(), "update.exe"), directory, installed);
        Assert.True(start.UseShellExecute);
        Assert.StartsWith("/S /UPDATE ", start.Arguments);
        Assert.Contains($"/WAITPID={Environment.ProcessId} ", start.Arguments);
        Assert.EndsWith("/D=" + directory, start.Arguments);
        Assert.Equal(!installed, start.Arguments.Contains("/PORTABLE"));
    }

    [Theory]
    [InlineData("bad\"path")]
    [InlineData("bad\npath")]
    public void Invalid_directory_cannot_inject_installer_arguments(string directory) =>
        Assert.ThrowsAny<ArgumentException>(() => UpdateInstallation.StartInfo("update.exe", directory, true));
}
