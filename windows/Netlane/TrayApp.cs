using Netlane.Core;

namespace Netlane;

/// Owns the tray icon and every window; reacts to state and settings changes.
public sealed class TrayApp : ApplicationContext
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly AppState _state = new();
    private readonly NotifyIcon _tray = new();
    private readonly Icon? _appIcon = LoadAppIcon();
    private readonly PopupForm _popup;
    private SettingsForm? _settingsForm;
    private SpeedOverlay? _overlay;
    private DesktopWidget? _widget;
    private Icon? _trayIcon;
    private string? _trayIconKey;
    private string? _pendingFlag;

    public TrayApp()
    {
        _popup = new PopupForm(_state, _settings, ShowSettings);
        _popup.CreateControl();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Netlane", null, (_, _) => ShowPopup());
        menu.Items.Add("Refresh", null, async (_, _) => await _state.RefreshAsync());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());

        _tray.Icon = _appIcon ?? SystemIcons.Application;
        _tray.Text = "Netlane";
        _tray.ContextMenuStrip = menu;
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };
        _tray.BalloonTipClicked += (_, _) => ShowPopup();
        _tray.Visible = true;

        Application.ApplicationExit += (_, _) => _tray.Visible = false;
        _state.Changed += OnStateChanged;
        _state.IpChanged += OnIpChanged;
        _settings.Changed += ApplySettings;
        ApplySettings();
        _state.Start();
    }

    private static Icon? LoadAppIcon()
    {
        try { return Environment.ProcessPath is { } path ? Icon.ExtractAssociatedIcon(path) : null; }
        catch (Exception) { return null; }
    }

    private void TogglePopup()
    {
        // Clicking the tray icon deactivates (and hides) the popup first; don't immediately reopen it.
        if (_popup.Visible) _popup.Hide();
        else if ((DateTime.UtcNow - _popup.LastHidden).TotalMilliseconds > 300) ShowPopup();
    }

    private void ShowPopup() => _popup.ShowNearTray();

    private void ShowSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settings, _appIcon);
            _settingsForm.FormClosed += (_, _) => _settingsForm = null;
            _settingsForm.Show();
        }
        _settingsForm.WindowState = FormWindowState.Normal;
        _settingsForm.Activate();
    }

    private void ApplySettings()
    {
        _state.SetTrafficMonitoring(_settings.ShowSpeedOverlay);
        _popup.ApplyTheme();

        if (_settings.ShowSpeedOverlay)
        {
            _overlay ??= new SpeedOverlay(_state, _settings, TogglePopup);
            _overlay.Relayout();
            if (!_overlay.Visible)
            {
                _overlay.Place();
                _overlay.Show();
            }
            Theme.StyleWindow(_overlay.Handle, Theme.Current(_settings).Dark, round: true);
        }
        else if (_overlay is not null)
        {
            _overlay.Close();
            _overlay = null;
        }

        if (_settings.ShowDesktopWidget)
        {
            _widget ??= new DesktopWidget(_state, _settings, ShowPopup);
            _widget.Relayout();
            if (!_widget.Visible)
            {
                _widget.Place();
                _widget.Show();
            }
        }
        else if (_widget is not null)
        {
            _widget.Close();
            _widget = null;
        }

        _trayIconKey = null;
        OnStateChanged();
    }

    private void OnStateChanged()
    {
        var geo = _state.Geo;
        UpdateTrayIcon(geo);

        var tip = geo is null ? "Netlane" : $"Netlane — {geo.Country}\n{geo.Ip}";
        if (_settings.ShowSpeedOverlay)
            tip += $"\n↓ {Throughput.FormatRate(_state.DownloadRate, _settings.Unit)}  ↑ {Throughput.FormatRate(_state.UploadRate, _settings.Unit)}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;

        if (_popup.Visible) _popup.UpdateView();
        _overlay?.Relayout();
        _widget?.Invalidate();
    }

    private void UpdateTrayIcon(GeoInfo? geo)
    {
        var palette = Theme.Current(_settings);
        var offline = !_state.IsOnline || (geo is null && _state.GeoError is not null);
        var code = geo?.CountryCode;
        var key = offline ? "offline" : code is null ? "loading" : $"{code}|{FlagImages.Cached(code) is not null}|{palette.Dark}";
        if (key == _trayIconKey) return;
        _trayIconKey = key;

        Icon next;
        if (offline) next = (Icon)SystemIcons.Warning.Clone();
        else if (code is null) next = (Icon)(_appIcon ?? SystemIcons.Application).Clone();
        else next = FlagImages.TrayIcon(FlagImages.Cached(code), code, palette);

        _tray.Icon = next;
        _trayIcon?.Dispose();
        _trayIcon = next;

        if (code is not null && FlagImages.Cached(code) is null && _pendingFlag != code) _ = LoadFlagAsync(code);
    }

    private async Task LoadFlagAsync(string code)
    {
        _pendingFlag = code;
        var image = await FlagImages.GetAsync(code);
        _pendingFlag = null;
        if (image is not null)
        {
            _trayIconKey = null;
            OnStateChanged();
        }
    }

    private void OnIpChanged(GeoInfo old, GeoInfo now)
    {
        if (!_settings.ShowIpChangeToast) return;
        var sameCountry = old.CountryCode == now.CountryCode;
        var title = sameCountry ? "IP address changed" : $"Now in {now.Country}";
        var text = $"{old.Ip} → {now.Ip}" + (sameCountry ? "" : $"\nwas {old.Country}");
        _tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon?.Dispose();
        _state.Dispose();
        _overlay?.Close();
        _widget?.Close();
        _settingsForm?.Close();
        _popup.Dispose();
        base.ExitThreadCore();
    }
}
