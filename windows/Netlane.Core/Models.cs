namespace Netlane.Core;

public sealed record GeoInfo(
    string Ip, string Country, string CountryCode, string City,
    string Isp, string Asn, string Timezone, DateTime FetchedAt)
{
    public static GeoInfo Sample { get; } = new(
        "203.0.113.42", "Germany", "DE", "Berlin", "Deutsche Telekom AG", "AS3320", "Europe/Berlin", DateTime.Now);
}

public static class Flags
{
    public static bool IsValidCode(string? code) =>
        code is { Length: 2 } && char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1]);

    /// Windows has no colour flag emoji, so flags are drawn from flagcdn.com PNGs (cached locally).
    public static Uri? ImageUrl(string? code) =>
        IsValidCode(code) ? new Uri($"https://flagcdn.com/w80/{code!.ToLowerInvariant()}.png") : null;
}

public enum SpeedUnit { Bits, Bytes }

public static class Throughput
{
    /// Bytes moved since the previous reading, summed over interfaces present in both. A new interface
    /// (its counters start from its whole lifetime) or a counter that went backwards contributes nothing.
    public static (ulong Rx, ulong Tx) Delta(
        IReadOnlyDictionary<string, (long Rx, long Tx)> previous, IReadOnlyDictionary<string, (long Rx, long Tx)> current)
    {
        ulong rx = 0, tx = 0;
        foreach (var (id, now) in current)
        {
            if (!previous.TryGetValue(id, out var before)) continue;
            if (now.Rx > before.Rx) rx += (ulong)(now.Rx - before.Rx);
            if (now.Tx > before.Tx) tx += (ulong)(now.Tx - before.Tx);
        }
        return (rx, tx);
    }

    public static double Mbps(long bytes, double seconds) =>
        seconds > 0 && bytes > 0 ? bytes * 8.0 / seconds / 1_000_000 : 0;

    public static string FormatRate(double bytesPerSecond, SpeedUnit unit)
    {
        var (value, units) = unit == SpeedUnit.Bits
            ? (Math.Max(bytesPerSecond, 0) * 8, new[] { "b/s", "Kb/s", "Mb/s", "Gb/s" })
            : (Math.Max(bytesPerSecond, 0), new[] { "B/s", "KB/s", "MB/s", "GB/s" });
        var index = 0;
        while (value >= 1000 && index < units.Length - 1)
        {
            value /= 1000;
            index++;
        }
        var number = index == 0 || value >= 100
            ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return $"{number} {units[index]}";
    }

    public static string FormatMbps(double mbps, SpeedUnit unit) =>
        unit == SpeedUnit.Bits
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{mbps:0.0} Mbps")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{mbps / 8:0.0} MB/s");
}
