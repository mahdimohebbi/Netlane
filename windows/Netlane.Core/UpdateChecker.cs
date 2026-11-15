using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Netlane.Core;

/// A published GitHub release that can be installed.
/// <param name="Sha256">Lower-case hex SHA-256 of the download from the release asset metadata. Installs are refused without it.</param>
public sealed record ReleaseInfo(string Version, Uri PageUrl, Uri DownloadUrl, string? Sha256);

/// Netlane is distributed through GitHub Releases, so "is there an update" means "is the latest release newer than me".
public static class UpdateChecker
{
    public static readonly Uri LatestReleaseUrl = new("https://api.github.com/repos/mahdimohebbi/Netlane/releases/latest");

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Netlane", "1.0"));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return http;
    }

    public static string AssetNameFor(Architecture architecture) =>
        architecture == Architecture.Arm64 ? "Netlane-win-arm64.zip" : "Netlane-win-x64.zip";

    /// Returns the latest release if it is newer than <paramref name="currentVersion"/> and has a build for this machine; otherwise null.
    public static async Task<ReleaseInfo?> CheckAsync(string currentVersion, string assetName, CancellationToken token = default,
        Func<Uri, CancellationToken, Task<string>>? fetch = null)
    {
        string json;
        if (fetch is not null) json = await fetch(LatestReleaseUrl, token).ConfigureAwait(false);
        else
        {
            using var response = await Http.GetAsync(LatestReleaseUrl, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode} from the update server");
            json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        var release = ParseRelease(json, assetName);
        return release is not null && IsNewer(release.Version, currentVersion) ? release : null;
    }

    public static ReleaseInfo? ParseRelease(string json, string assetName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var page = new Uri(root.GetProperty("html_url").GetString()!);
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != assetName) continue;
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            string? sha = null;
            if (asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.Ordinal))
                sha = digest["sha256:".Length..].ToLowerInvariant();
            return new ReleaseInfo(Normalize(tag), page, url, sha);
        }
        return null;
    }

    /// "v1.10.2-beta" -> "1.10.2"
    public static string Normalize(string tag)
    {
        var s = tag.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        return new string(s.TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray());
    }

    /// Numeric, component-wise comparison, so 1.10 > 1.9 and 1.2 == 1.2.0.
    public static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string v) => Normalize(v).Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var a = Parts(candidate);
        var b = Parts(current);
        if (a.Length == 0) return false;
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}
