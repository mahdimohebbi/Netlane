using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netlane.Core;

public enum StatsRange { Day, Week, Month }

public sealed record TrafficBucket(DateTimeOffset Start, ulong Rx, ulong Tx)
{
    public ulong Total => Rx + Tx;
}

/// Hourly download/upload totals, kept for 12 months. Plain data so it can be tested without any UI.
public sealed class TrafficLog
{
    public const int RetentionDays = 366;

    public sealed class Entry
    {
        [JsonPropertyName("t")] public long T { get; set; }   // start of the local hour, seconds since 1970
        [JsonPropertyName("rx")] public ulong Rx { get; set; }
        [JsonPropertyName("tx")] public ulong Tx { get; set; }
    }

    [JsonPropertyName("entries")] public List<Entry> Entries { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public void Add(ulong rx, ulong tx, DateTimeOffset at, TimeZoneInfo? zone = null)
    {
        if (rx == 0 && tx == 0) return;
        var start = HourStart(at, zone ?? TimeZoneInfo.Local);
        // Almost always the newest entry, so search from the end.
        var i = Entries.Count - 1;
        while (i >= 0 && Entries[i].T > start) i--;
        if (i >= 0 && Entries[i].T == start)
        {
            Entries[i].Rx += rx;
            Entries[i].Tx += tx;
        }
        else
        {
            Entries.Insert(i + 1, new Entry { T = start, Rx = rx, Tx = tx });
        }
    }

    public void Prune(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-RetentionDays).ToUnixTimeSeconds();
        Entries.RemoveAll(e => e.T < cutoff);
    }

    /// Bytes recorded in [from, to).
    public (ulong Rx, ulong Tx) Total(DateTimeOffset from, DateTimeOffset to)
    {
        var a = from.ToUnixTimeSeconds();
        var b = to.ToUnixTimeSeconds();
        ulong rx = 0, tx = 0;
        foreach (var e in Entries)
        {
            if (e.T < a || e.T >= b) continue;
            rx += e.Rx;
            tx += e.Tx;
        }
        return (rx, tx);
    }

    /// Day: each hour of today. Week: the last 7 days. Month: the last 30 days. Oldest first.
    public List<TrafficBucket> Buckets(StatsRange range, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var result = new List<TrafficBucket>();
        TrafficBucket Make(DateTimeOffset start, DateTimeOffset end)
        {
            var (rx, tx) = Total(start, end);
            return new TrafficBucket(start, rx, tx);
        }

        if (range == StatsRange.Day)
        {
            var end = DayStart(today.AddDays(1), zone);
            for (var s = DayStart(today, zone); s < end; s = s.AddHours(1)) result.Add(Make(s, s.AddHours(1)));
            return result;
        }

        var days = range == StatsRange.Week ? 7 : 30;
        for (var back = days - 1; back >= 0; back--)
        {
            var d = today.AddDays(-back);
            result.Add(Make(DayStart(d, zone), DayStart(d.AddDays(1), zone)));
        }
        return result;
    }

    /// Everything recorded, one row per hour.
    public string ToCsv()
    {
        var sb = new StringBuilder("hour_start,downloaded_bytes,uploaded_bytes\n");
        foreach (var e in Entries)
            sb.Append(DateTimeOffset.FromUnixTimeSeconds(e.T).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))
              .Append(',').Append(e.Rx).Append(',').Append(e.Tx).Append('\n');
        return sb.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static TrafficLog FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<TrafficLog>(json, Json) ?? new TrafficLog(); }
        catch (JsonException) { return new TrafficLog(); }
    }

    private static long HourStart(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        // Drop minutes and seconds only, so zones with a 30 or 45 minute offset still get whole local hours.
        var truncated = local - TimeSpan.FromMinutes(local.Minute) - TimeSpan.FromSeconds(local.Second)
                        - TimeSpan.FromTicks(local.Ticks % TimeSpan.TicksPerSecond);
        return truncated.ToUnixTimeSeconds();
    }

    private static DateTimeOffset DayStart(DateTime localDate, TimeZoneInfo zone) =>
        new(DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified), zone.GetUtcOffset(localDate.Date));
}

public static class ByteFormat
{
    /// Decimal units, like the macOS app: "0 B", "950 MB", "6.41 GB".
    public static string Format(ulong bytes)
    {
        if (bytes == 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var i = 0;
        while (value >= 1000 && i < units.Length - 1)
        {
            value /= 1000;
            i++;
        }
        var text = i == 0 || value >= 100 ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                  : value >= 10 ? value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                  : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $"{text} {units[i]}";
    }
}
