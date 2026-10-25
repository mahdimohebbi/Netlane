using System.Diagnostics;
using System.Net;

namespace Netlane.Core;

public sealed record SpeedResult(double? LatencyMs, double? DownloadMbps, double? UploadMbps, DateTime Date);

public static class SpeedTester
{
    private const string Base = "https://speed.cloudflare.com";
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(8);
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(1) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static async Task<double> LatencyAsync()
    {
        var url = $"{Base}/__down?bytes=0";
        using var cts = new CancellationTokenSource(Cap);
        try { await Http.GetByteArrayAsync(url, cts.Token); } catch { } // warm up DNS/TLS so samples reflect round trips
        var samples = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            await Http.GetByteArrayAsync(url, cts.Token);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return samples[1];
    }

    public static async Task<double> DownloadAsync()
    {
        using var cts = new CancellationTokenSource(Cap);
        var sw = Stopwatch.StartNew();
        long bytes = 0;
        try
        {
            using var response = await Http.GetAsync($"{Base}/__down?bytes=25000000", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0) bytes += read;
        }
        catch (OperationCanceledException) when (bytes > 0) { } // hit the time cap; measure what arrived
        return Throughput.Mbps(bytes, sw.Elapsed.TotalSeconds);
    }

    public static async Task<double> UploadAsync()
    {
        using var cts = new CancellationTokenSource(Cap);
        var content = new CountingContent(10_000_000);
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await Http.PostAsync($"{Base}/__up", content, cts.Token);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (content.BytesSent > 0) { }
        return Throughput.Mbps(content.BytesSent, sw.Elapsed.TotalSeconds);
    }

    /// Streams zeros and counts bytes written, so a capped upload still yields a measurement.
    private sealed class CountingContent : HttpContent
    {
        private readonly long _length;
        private long _sent;
        public long BytesSent => Interlocked.Read(ref _sent);

        public CountingContent(long length)
        {
            _length = length;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            var chunk = new byte[64 * 1024];
            while (_sent < _length)
            {
                var count = (int)Math.Min(chunk.Length, _length - _sent);
                await stream.WriteAsync(chunk.AsMemory(0, count), token);
                Interlocked.Add(ref _sent, count);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
