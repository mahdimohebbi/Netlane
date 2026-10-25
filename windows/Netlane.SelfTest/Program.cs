using Netlane.Core;

const string ipwhoisSample = """
    {"ip":"203.0.113.42","success":true,"type":"IPv4","continent":"Europe","country":"Germany",
    "country_code":"DE","region":"Berlin","city":"Berlin","connection":{"asn":3320,
    "org":"Deutsche Telekom AG","isp":"Deutsche Telekom AG","domain":"telekom.de"},
    "timezone":{"id":"Europe/Berlin","abbr":"CET","utc":"+01:00"}}
    """;

const string ipapiSample = """
    {"ip":"198.51.100.7","city":"Istanbul","region":"Istanbul","country":"TR","country_name":"Turkey",
    "country_code":"TR","timezone":"Europe/Istanbul","asn":"AS9121","org":"Turk Telekom"}
    """;

var failures = 0;
void Check(bool condition, string name)
{
    Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
    if (!condition) failures++;
}

Check(Flags.ImageUrl("US")?.ToString() == "https://flagcdn.com/w80/us.png", "flag url US");
Check(Flags.ImageUrl("de")?.ToString() == "https://flagcdn.com/w80/de.png", "flag url DE (lowercase)");
Check(Flags.ImageUrl("IR")?.ToString() == "https://flagcdn.com/w80/ir.png", "flag url IR");
Check(Flags.ImageUrl("TR")?.ToString() == "https://flagcdn.com/w80/tr.png", "flag url TR");
Check(Flags.ImageUrl("X1") is null && Flags.ImageUrl("") is null, "flag url invalid code");

var a = GeoClient.ParseIpwhois(ipwhoisSample);
Check(a is { Ip: "203.0.113.42", CountryCode: "DE", Country: "Germany", City: "Berlin",
            Isp: "Deutsche Telekom AG", Asn: "AS3320", Timezone: "Europe/Berlin" }, "parse ipwho.is");
Check(Throws(() => GeoClient.ParseIpwhois("""{"success":false,"message":"Reserved range"}""")), "ipwho.is success=false rejected");

var b = GeoClient.ParseIpapi(ipapiSample);
Check(b is { Ip: "198.51.100.7", CountryCode: "TR", Country: "Turkey", City: "Istanbul",
            Isp: "Turk Telekom", Asn: "AS9121", Timezone: "Europe/Istanbul" }, "parse ipapi.co");
Check(Throws(() => GeoClient.ParseIpapi("""{"error":true,"reason":"RateLimited"}""")), "ipapi.co error rejected");

Check(Math.Abs(Throughput.Mbps(25_000_000, 2) - 100) < 1e-9, "Mbps 25MB/2s = 100");
Check(Math.Abs(Throughput.Mbps(1_250_000, 1) - 10) < 1e-9, "Mbps 1.25MB/1s = 10");
Check(Throughput.Mbps(100, 0) == 0, "Mbps zero duration");
Check(Throughput.FormatRate(1_250_000, SpeedUnit.Bits) == "10.0 Mb/s", "rate 10.0 Mb/s");
Check(Throughput.FormatRate(1_260_000, SpeedUnit.Bytes) == "1.3 MB/s", "rate 1.3 MB/s");
Check(Throughput.FormatRate(512, SpeedUnit.Bytes) == "512 B/s", "rate 512 B/s");
Check(Throughput.FormatRate(250_000_000, SpeedUnit.Bits) == "2.0 Gb/s", "rate 2.0 Gb/s");
Check(Throughput.FormatMbps(80, SpeedUnit.Bytes) == "10.0 MB/s", "speed test MB/s");

var fallback = new GeoClient((url, _) => url == GeoClient.PrimaryUrl
    ? throw new HttpRequestException("timed out")
    : Task.FromResult(ipapiSample));
Check((await fallback.LookupAsync()).CountryCode == "TR", "fallback to ipapi.co when primary fails");
var bothFail = new GeoClient((_, _) => throw new HttpRequestException("offline"));
Check(await ThrowsAsync(() => bothFail.LookupAsync()), "error when all providers fail");

var local = NetworkInfo.Read();
Check(local.ConnectionType.Length > 0, $"local network read ({local.ConnectionType}, {local.LocalIp})");
var (rx, tx) = NetworkInfo.TrafficCounters();
Check(rx >= 0 && tx >= 0, "traffic counters read");

if (args.Contains("--live"))
{
    var live = await new GeoClient().LookupAsync();
    Check(Flags.IsValidCode(live.CountryCode) && live.Ip.Length > 0, $"live geo lookup ({live.CountryCode}, {live.Ip}, {live.Isp})");
    var latency = await SpeedTester.LatencyAsync();
    Check(latency > 0, $"live latency ({latency:0} ms)");
    var down = await SpeedTester.DownloadAsync();
    Check(down > 0, $"live download ({down:0.0} Mbps)");
    var up = await SpeedTester.UploadAsync();
    Check(up > 0, $"live upload ({up:0.0} Mbps)");
}

Console.WriteLine(failures == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({failures})");
return failures == 0 ? 0 : 1;

static bool Throws(Action action)
{
    try { action(); return false; } catch { return true; }
}

static async Task<bool> ThrowsAsync(Func<Task> action)
{
    try { await action(); return false; } catch { return true; }
}
