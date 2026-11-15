using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Netlane.Core;

namespace Netlane;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

/// In-app updates from GitHub Releases: checks daily, and on request downloads the new Netlane.exe, verifies its
/// checksum, swaps it in place of the running exe and starts it. Events are raised on the UI thread.
public sealed class Updater
{
    private const string RestartArgument = "--after-update";

    private readonly AppSettings _settings;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly string _assetName = UpdateChecker.AssetNameFor(RuntimeInformation.ProcessArchitecture);
    private System.Threading.Timer? _timer;
    private DateTime? _lastCheck;
    private string? _announced;

    public Updater(AppSettings settings) => _settings = settings;

    public static string CurrentVersion { get; } =
        (Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public ReleaseInfo? Latest { get; private set; }
    public int Progress { get; private set; }
    public string? Error { get; private set; }
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;

    /// Raised whenever State, Progress or Latest change.
    public event Action? Changed;
    /// Raised once per newly discovered version by a background check (not by a manual one).
    public event Action<ReleaseInfo>? UpdateFound;

    /// One check shortly after launch, then at most one a day while the setting is on.
    public void Start() => _timer = new System.Threading.Timer(async _ =>
    {
        if (!_settings.CheckForUpdates || IsBusy) return;
        if (_lastCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
        await Post(() => CheckAsync(userInitiated: false));
    }, null, TimeSpan.FromSeconds(8), TimeSpan.FromHours(1));

    public void Stop() => _timer?.Dispose();

    private Task Post(Func<Task> work)
    {
        var done = new TaskCompletionSource();
        void Run() => _ = Wrap();
        async Task Wrap()
        {
            try { await work(); }
            finally { done.TrySetResult(); }
        }
        if (_ui is null) Run(); else _ui.Post(_ => Run(), null);
        return done.Task;
    }

    private void Set(UpdateState state, string? error = null)
    {
        State = state;
        Error = error;
        Changed?.Invoke();
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (IsBusy) return;
        Set(UpdateState.Checking);
        _lastCheck = DateTime.UtcNow;
        try
        {
            var release = await UpdateChecker.CheckAsync(CurrentVersion, _assetName);
            Latest = release;
            if (release is null) { Set(UpdateState.UpToDate); return; }
            Set(UpdateState.Available);
            if (!userInitiated && release.Version != _announced)
            {
                _announced = release.Version;
                UpdateFound?.Invoke(release);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or UriFormatException or InvalidOperationException)
        {
            // Background checks fail quietly (offline, rate limited); a manual check should say why.
            if (userInitiated) Set(UpdateState.Failed, "Couldn't reach the update server. Check your internet connection.");
            else Set(Latest is null ? UpdateState.Idle : UpdateState.Available);
        }
    }

    /// Installs <see cref="Latest"/> and starts the new exe. Returns true when the caller should now exit so the new copy can take over.
    public async Task<bool> InstallAsync()
    {
        if (IsBusy || Latest is not { } release) return false;
        var exe = Environment.ProcessPath;
        var work = Path.Combine(Path.GetTempPath(), "Netlane-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (exe is null) throw new UpdateException("Couldn't locate the running Netlane.exe.");
            if (release.Sha256 is not { } expected)
                throw new UpdateException("This release has no checksum, so it can't be verified. Download it from the release page instead.");
            if (release.DownloadUrl.Scheme != Uri.UriSchemeHttps) throw new UpdateException("The update isn't served over HTTPS.");
            EnsureWritable(Path.GetDirectoryName(exe)!);

            Directory.CreateDirectory(work);
            Progress = 0;
            Set(UpdateState.Downloading);
            var zip = Path.Combine(work, _assetName);
            var digest = await DownloadAsync(release.DownloadUrl, zip);
            if (!string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The downloaded file didn't match its checksum, so it was discarded.");

            Set(UpdateState.Installing);
            var staged = exe + ".new";
            Extract(zip, staged);
            Swap(exe, staged);

            Process.Start(new ProcessStartInfo(exe, $"{RestartArgument} {Environment.ProcessId}") { UseShellExecute = false });
            return true;
        }
        catch (Exception e) when (e is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            var message = e switch
            {
                UpdateException => e.Message,
                UnauthorizedAccessException => "Netlane can't write to its own folder. Download the new version from the release page instead.",
                HttpRequestException or TaskCanceledException => "The download failed. Check your internet connection and try again.",
                _ => e.Message,
            };
            Set(UpdateState.Failed, message);
            return false;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<string> DownloadAsync(Uri url, string file)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Netlane/{CurrentVersion}");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(file);
        using var hash = SHA256.Create();
        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            hash.TransformBlock(buffer, 0, read, null, 0);
            received += read;
            var percent = total > 0 ? (int)(received * 100 / total) : 0;
            if (percent != Progress) { Progress = percent; Changed?.Invoke(); }
        }
        hash.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hash.Hash!).ToLowerInvariant();
    }

    private static void Extract(string zip, string destination)
    {
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.GetEntry("Netlane.exe") ?? throw new UpdateException("The download doesn't contain Netlane.exe.");
        entry.ExtractToFile(destination, overwrite: true);
        using var stream = File.OpenRead(destination);
        if (stream.Length < 1_000_000 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
            throw new UpdateException("The downloaded file isn't a valid Netlane.exe.");
    }

    /// A running exe can be renamed but not overwritten: move it aside, put the new one in place, undo on failure.
    private static void Swap(string exe, string staged)
    {
        var old = exe + ".old";
        File.Move(exe, old, overwrite: true);
        try { File.Move(staged, exe); }
        catch
        {
            File.Move(old, exe, overwrite: true);
            throw;
        }
    }

    private static void EnsureWritable(string folder)
    {
        var probe = Path.Combine(folder, ".netlane-write-test-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
    }

    /// Called at startup: if we were launched by an update, wait for the previous copy to exit, and clear its leftovers.
    public static void HandleStartup(string[] args)
    {
        var index = Array.IndexOf(args, RestartArgument);
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(15000); }
            catch (ArgumentException) { } // already gone
        }
        if (Environment.ProcessPath is { } exe)
        {
            try { File.Delete(exe + ".old"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class UpdateException(string message) : Exception(message);
}
