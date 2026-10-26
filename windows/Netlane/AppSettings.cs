using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Netlane.Core;

namespace Netlane;

public enum ThemeMode { System, Light, Dark }
public enum BandwidthData { Both, Download, Upload }
public enum WidgetSize { Small, Medium }

public sealed class AppSettings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Netlane");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public SpeedUnit Unit { get; set; } = SpeedUnit.Bits;
    public bool PopupClosesAutomatically { get; set; } = true;
    public bool ShowIpChangeToast { get; set; } = true;
    public bool ShowSpeedOverlay { get; set; } = true;
    public bool DoubleLine { get; set; } = true;
    public BandwidthData Bandwidth { get; set; } = BandwidthData.Both;
    public bool UploadOnTop { get; set; }
    public bool CustomColors { get; set; }
    public string DownloadColor { get; set; } = "#34C759";
    public string UploadColor { get; set; } = "#0A84FF";
    public bool ShowDesktopWidget { get; set; }
    public WidgetSize WidgetSize { get; set; } = WidgetSize.Medium;
    public Point? OverlayLocation { get; set; }
    public Point? WidgetLocation { get; set; }

    [JsonIgnore] public Action? Changed { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    /// Persists and notifies listeners. Pass notify: false for changes nobody needs to react to (window positions).
    public void Save(bool notify = true)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (notify) Changed?.Invoke();
    }

    public static Color ParseColor(string hex, Color fallback)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch (Exception) { return fallback; }
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Netlane";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string path && path.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
