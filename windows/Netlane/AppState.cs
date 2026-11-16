using System.Net.NetworkInformation;
using Netlane.Core;

namespace Netlane;

/// All network state the UI shows. Lives on the UI thread; raises Changed after every update.
public sealed class AppState : IDisposable
{
    private readonly GeoClient _client = new();
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5 * 60 * 1000 };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _trafficTimer = new() { Interval = 1000 };
    private Dictionary<string, (long Rx, long Tx)> _lastCounters = [];
    private DateTime _lastSample;

    public GeoInfo? Geo { get; private set; }
    public string? GeoError { get; private set; }
    public bool IsRefreshing { get; private set; }
    public bool IsOnline { get; private set; } = true;
    public LocalNetwork Local { get; private set; } = new("—", "—", [], false);
    public TrafficStats Stats { get; } = new();
    public double DownloadRate { get; private set; }
    public double UploadRate { get; private set; }
    public string SpeedPhase { get; private set; } = "";
    public SpeedResult? Speed { get; private set; }
    public string? SpeedError { get; private set; }

    public event Action? Changed;
    public event Action<GeoInfo, GeoInfo>? IpChanged;

    public void Start()
    {
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await RefreshAsync();
        };
        _trafficTimer.Tick += (_, _) => SampleTraffic();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _refreshTimer.Start();
        _ = RefreshAsync();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _ui.Post(_ =>
    {
        _debounce.Stop();
        _debounce.Start();
        Local = NetworkInfo.Read();
        IsOnline = NetworkInterface.GetIsNetworkAvailable();
        Changed?.Invoke();
    }, null);

    public async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        Changed?.Invoke();
        try
        {
            Local = await Task.Run(NetworkInfo.Read);
            IsOnline = NetworkInterface.GetIsNetworkAvailable();
            var geo = await _client.LookupAsync();
            var previous = Geo;
            Geo = geo;
            GeoError = null;
            if (previous is not null && previous.Ip != geo.Ip) IpChanged?.Invoke(previous, geo);
        }
        catch (Exception e)
        {
            GeoError = IsOnline ? e.Message : "No network connection";
        }
        finally
        {
            IsRefreshing = false;
            Changed?.Invoke();
        }
    }

    public void SetTrafficMonitoring(bool enabled)
    {
        if (enabled == _trafficTimer.Enabled) return;
        DownloadRate = UploadRate = 0;
        if (enabled)
        {
            _lastCounters = NetworkInfo.TrafficCountersByInterface();
            _lastSample = DateTime.UtcNow;
            _trafficTimer.Start();
        }
        else
        {
            _trafficTimer.Stop();
        }
    }

    private void SampleTraffic()
    {
        var now = DateTime.UtcNow;
        var counters = NetworkInfo.TrafficCountersByInterface();
        var seconds = (now - _lastSample).TotalSeconds;
        var (rx, tx) = Throughput.Delta(_lastCounters, counters);
        _lastCounters = counters;
        _lastSample = now;
        if (seconds > 0)
        {
            DownloadRate = rx / seconds;
            UploadRate = tx / seconds;
            Stats.Record(rx, tx, seconds);
        }
        Changed?.Invoke();
    }

    public async Task RunSpeedTestAsync()
    {
        if (SpeedPhase.Length > 0) return;
        SpeedError = null;
        var errors = new List<string>();
        double? latency = null, down = null, up = null;

        SpeedPhase = "Measuring latency…";
        Changed?.Invoke();
        try { latency = await SpeedTester.LatencyAsync(); } catch { errors.Add("latency"); }

        SpeedPhase = "Testing download…";
        Changed?.Invoke();
        try { down = await SpeedTester.DownloadAsync(); } catch { errors.Add("download"); }

        SpeedPhase = "Testing upload…";
        Changed?.Invoke();
        try { up = await SpeedTester.UploadAsync(); } catch { errors.Add("upload"); }

        SpeedPhase = "";
        if (errors.Count < 3) Speed = new SpeedResult(latency, down, up, DateTime.Now);
        if (errors.Count > 0) SpeedError = "Failed: " + string.Join(", ", errors);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _refreshTimer.Dispose();
        _debounce.Dispose();
        _trafficTimer.Dispose();
    }
}
