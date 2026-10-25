using System.Net.Http.Headers;
using System.Text.Json;

namespace Netlane.Core;

public sealed class GeoException(string message) : Exception(message);

public sealed class GeoClient
{
    public static readonly Uri PrimaryUrl = new("https://ipwho.is/");
    public static readonly Uri FallbackUrl = new("https://ipapi.co/json/");

    private static readonly HttpClient Http = CreateHttp();
    private readonly Func<Uri, CancellationToken, Task<string>> _fetch;

    public GeoClient(Func<Uri, CancellationToken, Task<string>>? fetch = null)
    {
        _fetch = fetch ?? LiveFetch;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Netlane", "1.0"));
        http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return http;
    }

    private static async Task<string> LiveFetch(Uri url, CancellationToken token)
    {
        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new GeoException($"HTTP {(int)response.StatusCode} from {url.Host}");
        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    public async Task<GeoInfo> LookupAsync(CancellationToken token = default)
    {
        var errors = new List<string>();
        try
        {
            return ParseIpwhois(await _fetch(PrimaryUrl, token).ConfigureAwait(false));
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            errors.Add($"ipwho.is: {e.Message}");
        }
        try
        {
            return ParseIpapi(await _fetch(FallbackUrl, token).ConfigureAwait(false));
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            errors.Add($"ipapi.co: {e.Message}");
        }
        throw new GeoException("Lookup failed: " + string.Join("; ", errors));
    }

    public static GeoInfo ParseIpwhois(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new GeoException(Str(root, "message") ?? "ipwho.is reported failure");
        var ip = Str(root, "ip");
        var code = Str(root, "country_code");
        if (ip is null || code is null) throw new GeoException("Missing ip/country");
        root.TryGetProperty("connection", out var conn);
        root.TryGetProperty("timezone", out var tz);
        string asn = "";
        if (conn.ValueKind == JsonValueKind.Object && conn.TryGetProperty("asn", out var a) && a.ValueKind == JsonValueKind.Number)
            asn = $"AS{a.GetInt64()}";
        return new GeoInfo(
            ip, Str(root, "country") ?? code, code, Str(root, "city") ?? "",
            Str(conn, "isp") ?? Str(conn, "org") ?? "", asn, Str(tz, "id") ?? "", DateTime.Now);
    }

    public static GeoInfo ParseIpapi(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
            throw new GeoException(Str(root, "reason") ?? "ipapi.co reported failure");
        var ip = Str(root, "ip");
        var code = Str(root, "country_code");
        if (ip is null || code is null) throw new GeoException("Missing ip/country");
        return new GeoInfo(
            ip, Str(root, "country_name") ?? code, code, Str(root, "city") ?? "",
            Str(root, "org") ?? "", Str(root, "asn") ?? "", Str(root, "timezone") ?? "", DateTime.Now);
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
