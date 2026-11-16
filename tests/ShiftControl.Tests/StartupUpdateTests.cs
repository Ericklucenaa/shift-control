using System.Net;
using System.Text;
using System.Text.Json;
using ShiftControl.Maintenance;

namespace ShiftControl.Tests;

public class StartupUpdateTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }

    [Theory]
    [InlineData("0.3.2", ReleaseStatus.Available)]
    [InlineData("0.3.3", ReleaseStatus.UpToDate)]
    [InlineData("0.3.4", ReleaseStatus.UpToDate)]
    public async Task Public_manifest_checks_updates_without_calling_rate_limited_api(string current, ReleaseStatus expected)
    {
        var update = new ReleaseUpdate("0.3.3", "https://github.com/owner/repo/releases/tag/v0.3.3",
            "https://github.com/owner/repo/releases/download/v0.3.3/ShiftControl-0.3.3-setup.exe", null, new string('a', 64));
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https://github.com/owner/repo/releases/latest/download/update.json", request.RequestUri!.AbsoluteUri);
            Assert.True(request.Headers.CacheControl!.NoCache);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(update), Encoding.UTF8, "application/json") };
        }));
        var result = await new ReleaseClient(http, "owner/repo").CheckAsync(current, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.Equal(update, result.Update);
    }

    [Theory]
    [InlineData("https://example.com/installer.exe", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://github.com/owner/repo/releases/download/v0.3.3/setup.exe", "bad-hash")]
    public async Task Invalid_manifest_cannot_start_an_unverified_install(string installer, string hash)
    {
        var update = new ReleaseUpdate("0.3.3", "https://github.com/owner/repo/releases/tag/v0.3.3", installer, null, hash);
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(update), Encoding.UTF8, "application/json")
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseClient(http, "owner/repo").CheckAsync("0.3.2", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Startup_installer_shows_progress_and_waits_for_the_app_to_exit(bool installed)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Shift Control Test");
        var start = UpdateInstallation.StartInfo("update.exe", directory, installed, visible: true);
        Assert.StartsWith("/UPDATE ", start.Arguments);
        Assert.DoesNotContain("/S ", start.Arguments);
        Assert.Contains($"/WAITPID={Environment.ProcessId} ", start.Arguments);
        Assert.Equal(!installed, start.Arguments.Contains("/PORTABLE"));
        Assert.EndsWith("/D=" + directory, start.Arguments);
    }
}
