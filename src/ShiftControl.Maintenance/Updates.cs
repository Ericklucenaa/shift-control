using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace ShiftControl.Maintenance;

public sealed record ReleaseUpdate(string Version, string PageUrl, string? InstallerUrl, string? Notes, string? Sha256);
public enum ReleaseStatus { UpToDate, Available, NoReleases }

/// <summary>Releases oficiais; só libera o instalador após conferir SHA-256.</summary>
public sealed class ReleaseClient(HttpClient http, string repository)
{
    private sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url,
        [property: JsonPropertyName("digest")] string? Digest);
    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string Tag,
        [property: JsonPropertyName("html_url")] string Url,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("assets")] List<Asset>? Assets);

    public async Task<(ReleaseStatus Status, ReleaseUpdate? Update)> CheckAsync(string current, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        ct = timeout.Token;
        // Public release asset avoids the unauthenticated GitHub API rate limit.
        using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{repository}/releases/latest/download/update.json");
        manifestRequest.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var manifestResponse = await http.SendAsync(manifestRequest, ct);
        if (manifestResponse.IsSuccessStatusCode)
        {
            var manifest = await manifestResponse.Content.ReadFromJsonAsync<ReleaseUpdate>(ct)
                ?? throw new InvalidDataException("Empty update manifest.");
            if (!TryVersion(manifest.Version, out _) || !OfficialReleaseUrl(manifest.PageUrl)
                || manifest.InstallerUrl is null || !OfficialReleaseUrl(manifest.InstallerUrl)
                || manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid update manifest.");
            return (IsNewer(manifest.Version, current) ? ReleaseStatus.Available : ReleaseStatus.UpToDate, manifest);
        }
        if (manifestResponse.StatusCode != HttpStatusCode.NotFound) manifestResponse.EnsureSuccessStatusCode();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return (ReleaseStatus.NoReleases, null);
        res.EnsureSuccessStatusCode();
        var release = await res.Content.ReadFromJsonAsync<Release>(ct);
        if (release is null || release.Draft || release.Prerelease || !TryVersion(release.Tag, out _))
            return (ReleaseStatus.NoReleases, null);
        if (!OfficialReleaseUrl(release.Url)) throw new InvalidDataException("Endereço de release inválido.");
        var asset = release.Assets?.FirstOrDefault(a => a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) && OfficialReleaseUrl(a.Url));
        string? hash = asset?.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit)) hash = null;
        var info = new ReleaseUpdate(release.Tag.TrimStart('v', 'V'), release.Url, asset?.Url, release.Body, hash);
        return (IsNewer(info.Version, current) ? ReleaseStatus.Available : ReleaseStatus.UpToDate, info);
    }

    private bool OfficialReleaseUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/" + repository + "/releases/", StringComparison.OrdinalIgnoreCase);

    public async Task<string> DownloadAsync(ReleaseUpdate update, string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (update.InstallerUrl is null || !OfficialReleaseUrl(update.InstallerUrl) || update.Sha256 is not { Length: 64 })
            throw new InvalidDataException("A release não fornece instalador com SHA-256 verificável.");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "ShiftControl-" + Guid.NewGuid().ToString("N") + "-setup.exe");
        string temporary = target + ".part";
        try
        {
            using var response = await http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    if (response.Content.Headers.ContentLength is > 0 and var size) progress?.Report(total * 100.0 / size);
                }
            }
            await using (var file = File.OpenRead(temporary))
            {
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("O SHA-256 do instalador não confere.");
            }
            File.Move(temporary, target);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool IsNewer(string candidate, string current) => TryVersion(candidate, out var a) && TryVersion(current, out var b) && a > b;
    private static bool TryVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        string core = text.TrimStart('v', 'V');
        if (core.Contains('-') || core.Contains('+') || !Version.TryParse(core.Contains('.') ? core : core + ".0", out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }
}
