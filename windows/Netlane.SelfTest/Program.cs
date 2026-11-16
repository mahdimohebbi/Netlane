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

Check(UpdateChecker.IsNewer("1.3", "1.2"), "update: 1.3 > 1.2");
Check(UpdateChecker.IsNewer("1.10", "1.9"), "update: 1.10 > 1.9 (numeric)");
Check(UpdateChecker.IsNewer("v2.0.0", "1.9.9"), "update: tag prefix v");
Check(!UpdateChecker.IsNewer("1.2.0", "1.2"), "update: 1.2.0 == 1.2");
Check(!UpdateChecker.IsNewer("1.1", "1.2"), "update: older is not newer");
Check(!UpdateChecker.IsNewer("", "1.2"), "update: empty version");
const string releaseJson = """
    {"tag_name":"v1.3","html_url":"https://github.com/mahdimohebbi/Netlane/releases/tag/v1.3","assets":[
    {"name":"Netlane.dmg","browser_download_url":"https://github.com/x/Netlane.dmg","digest":"sha256:AA"},
    {"name":"Netlane-win-x64.zip","browser_download_url":"https://github.com/x/Netlane-win-x64.zip","digest":"sha256:ABCDEF"}]}
    """;
var rel = UpdateChecker.ParseRelease(releaseJson, "Netlane-win-x64.zip");
Check(rel is { Version: "1.3", Sha256: "abcdef" } && rel.DownloadUrl.AbsolutePath.EndsWith("Netlane-win-x64.zip"), "update: parse release picks this platform's zip and digest");
Check(UpdateChecker.ParseRelease(releaseJson, "Netlane-win-arm64.zip") is null, "update: release without a matching build is ignored");
Check(await UpdateChecker.CheckAsync("1.2", "Netlane-win-x64.zip", default, (_, _) => Task.FromResult(releaseJson)) is not null, "update: newer release is offered");
Check(await UpdateChecker.CheckAsync("1.3", "Netlane-win-x64.zip", default, (_, _) => Task.FromResult(releaseJson)) is null, "update: same version is not offered");

var utc = TimeZoneInfo.Utc;
DateTimeOffset At(int day, int hour, int minute = 30) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);
var now = At(15, 21);
var log = new TrafficLog();
log.Add(100, 10, At(15, 21, 5), utc);
log.Add(50, 5, At(15, 21, 55), utc);   // same hour: merged
log.Add(7, 1, At(15, 3), utc);         // earlier hour, inserted before
log.Add(1000, 100, At(14, 12), utc);   // yesterday
log.Add(0, 0, At(15, 22), utc);        // nothing to record
Check(log.Entries.Count == 3 && log.Entries.Zip(log.Entries.Skip(1), (a, b) => a.T < b.T).All(x => x), "traffic: hours merge and stay sorted");
var day = log.Buckets(StatsRange.Day, now, utc);
Check(day.Count == 24 && day[21] is { Rx: 150, Tx: 15 } && day[3].Rx == 7 && day[0].Total == 0, "traffic: day = 24 hourly buckets");
var week = log.Buckets(StatsRange.Week, now, utc);
Check(week.Count == 7 && week[6].Rx == 157 && week[5].Rx == 1000 && week[0].Total == 0, "traffic: week = 7 daily buckets, today last");
Check(log.Buckets(StatsRange.Month, now, utc).Count == 30, "traffic: month = 30 daily buckets");
Check(log.Total(At(15, 0, 0), At(16, 0, 0)).Rx == 157, "traffic: total in range");
Check(TrafficLog.FromJson(log.ToJson()).ToJson() == log.ToJson(), "traffic: log survives save/load");
Check(TrafficLog.FromJson("not json").Entries.Count == 0, "traffic: corrupt file is ignored");
var csv = log.ToCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries);
Check(csv.Length == 4 && csv[0] == "hour_start,downloaded_bytes,uploaded_bytes" && csv[3].EndsWith(",150,15"), "traffic: csv export");
var old = new TrafficLog();
old.Add(5, 5, now.AddDays(-400), utc);
old.Add(6, 6, now.AddDays(-30), utc);
old.Prune(now);
Check(old.Entries.Count == 1 && old.Entries[0].Rx == 6, "traffic: entries older than 12 months are pruned");
var india = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromMinutes(330), "t", "t");
var half = new TrafficLog();
half.Add(1, 1, new DateTimeOffset(2026, 11, 15, 10, 50, 0, TimeSpan.Zero), india); // 16:20 local -> 16:00 local hour
Check(half.Buckets(StatsRange.Day, new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero), india)[16].Rx == 1, "traffic: half-hour time zones get whole local hours");
Check(ByteFormat.Format(0) == "0 B" && ByteFormat.Format(950_000_000) == "950 MB" && ByteFormat.Format(6_410_000_000) == "6.41 GB" && ByteFormat.Format(512) == "512 B", "traffic: byte formatting");

var before = new Dictionary<string, (long Rx, long Tx)> { ["wifi"] = (1000, 500), ["eth"] = (2000, 100) };
var after = new Dictionary<string, (long Rx, long Tx)> { ["wifi"] = (3000, 700), ["eth"] = (1500, 100), ["new"] = (9_000_000_000, 9_000_000_000) };
Check(Throughput.Delta(before, after) == (2000UL, 200UL), "traffic: per-interface delta ignores new adapters and counter resets");

var statsFile = Path.Combine(Path.GetTempPath(), $"netlane-selftest-{Guid.NewGuid():N}.json");
try
{
    var stats = new TrafficStats(statsFile);
    for (var i = 0; i < 75; i++) stats.Record(1_000_000, 200_000, 1);
    Check(stats.Live.Count == 60 && stats.Live[^1].Id == 75, "traffic: live window keeps the last 60 samples");
    Check(stats.SessionRx == 75_000_000 && stats.SessionTx == 15_000_000, "traffic: session totals");
    Check(Math.Abs(stats.PeakDown - 1_000_000) < 1 && Math.Abs(stats.Live[0].Up - 200_000) < 1, "traffic: rates and peaks");
    stats.Record(9_000_000_000, 0, 3600); // woke from sleep: counters can't be trusted
    Check(stats.SessionRx == 75_000_000 && stats.Log.Entries.Sum(e => (long)e.Rx) == 75_000_000, "traffic: long gaps are not counted");
    stats.Save();
    Check(new TrafficStats(statsFile).Log.Entries.Sum(e => (long)e.Rx) == 75_000_000, "traffic: history persists across launches");
    var reloaded = new TrafficStats(statsFile);
    reloaded.ClearHistory();
    Check(new TrafficStats(statsFile).Log.Entries.Count == 0, "traffic: clear history");
}
finally { File.Delete(statsFile); }

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
