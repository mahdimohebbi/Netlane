namespace Netlane.Core;

public readonly record struct TrafficSample(long Id, double Down, double Up); // bytes per second

/// Live speed history for the chart plus the persisted byte log. Samples arrive once a second from AppState.
public sealed class TrafficStats
{
    public const int LiveWindow = 60;
    /// A gap this long (sleep, stall) makes the counters unreliable, so those bytes are not added to history.
    private const double MaxTrustedGap = 10;

    private readonly string _path;
    private readonly List<TrafficSample> _live = [];
    private long _counter;
    private DateTimeOffset _lastSave = DateTimeOffset.UtcNow;
    private bool _dirty;

    public TrafficStats(string? path = null)
    {
        _path = path ?? DefaultPath;
        try { Log = File.Exists(_path) ? TrafficLog.FromJson(File.ReadAllText(_path)) : new TrafficLog(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log = new TrafficLog(); }
        Log.Prune(DateTimeOffset.Now);
    }

    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Netlane", "traffic.json");

    public TrafficLog Log { get; private set; }
    public IReadOnlyList<TrafficSample> Live => _live;
    public ulong SessionRx { get; private set; }
    public ulong SessionTx { get; private set; }
    public double PeakDown { get; private set; }
    public double PeakUp { get; private set; }

    public void Record(ulong rx, ulong tx, double elapsedSeconds, DateTimeOffset? at = null)
    {
        if (elapsedSeconds <= 0) return;
        var now = at ?? DateTimeOffset.Now;
        var down = rx / elapsedSeconds;
        var up = tx / elapsedSeconds;
        _live.Add(new TrafficSample(++_counter, down, up));
        if (_live.Count > LiveWindow) _live.RemoveRange(0, _live.Count - LiveWindow);
        PeakDown = Math.Max(PeakDown, down);
        PeakUp = Math.Max(PeakUp, up);

        if (elapsedSeconds > MaxTrustedGap) return;
        SessionRx += rx;
        SessionTx += tx;
        if (rx == 0 && tx == 0) return;
        Log.Add(rx, tx, now);
        _dirty = true;
        if ((now - _lastSave).TotalSeconds >= 60) Save();
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, Log.ToJson());
            File.Move(temp, _path, overwrite: true);
            _dirty = false;
            _lastSave = DateTimeOffset.UtcNow;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* try again at the next save */ }
    }

    public void ClearHistory()
    {
        Log = new TrafficLog();
        _dirty = true;
        Save();
    }

    public (ulong Rx, ulong Tx) TotalToday(DateTimeOffset? now = null)
    {
        var n = now ?? DateTimeOffset.Now;
        var start = new DateTimeOffset(n.Date, n.Offset);
        return Log.Total(start, n.AddSeconds(1));
    }

    public (ulong Rx, ulong Tx) TotalThisMonth(DateTimeOffset? now = null)
    {
        var n = now ?? DateTimeOffset.Now;
        var start = new DateTimeOffset(new DateTime(n.Year, n.Month, 1), n.Offset);
        return Log.Total(start, start.AddMonths(1));
    }
}
