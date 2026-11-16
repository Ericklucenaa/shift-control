using ShiftControl.App.Services;

namespace ShiftControl.Tests;

public class SystemIntegrationTests
{
    [Fact]
    public void Missing_task_errors_cover_dotnet_and_com_without_hiding_access_failures()
    {
        Assert.True(SystemIntegration.IsMissingTask(new FileNotFoundException()));
        Assert.True(SystemIntegration.IsMissingTask(new DirectoryNotFoundException()));
        Assert.True(SystemIntegration.IsMissingTask(new System.Runtime.InteropServices.COMException("Missing", unchecked((int)0x80070002))));
        Assert.True(SystemIntegration.IsMissingTask(new System.Runtime.InteropServices.COMException("Missing", unchecked((int)0x80070003))));
        Assert.False(SystemIntegration.IsMissingTask(new UnauthorizedAccessException()));
        Assert.False(SystemIntegration.IsMissingTask(new System.Runtime.InteropServices.COMException("Unavailable", unchecked((int)0x800706BA))));
    }

    [Fact]
    public void Missing_startup_task_is_disabled_instead_of_blocking_settings()
    {
        // Query the real COM API with an absent name; do not register or delete tasks.
        string taskName = "ShiftControl-Regression-" + Guid.NewGuid().ToString("N");
        Assert.False(SystemIntegration.IsStartupTaskEnabled(taskName));
    }
}
