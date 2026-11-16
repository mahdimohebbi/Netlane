using Netlane.Core;

namespace Netlane;

/// Owns the tray icon and every window; reacts to state and settings changes.
public sealed class TrayApp : ApplicationContext
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly AppState _state = new();
    private readonly Updater _updater;
    private readonly ToolStripMenuItem _updateItem = new() { Visible = false };
    private bool _updateBalloon;
    private readonly NotifyIcon _tray = new();
    private readonly Icon? _appIcon = LoadAppIcon();
    private readonly PopupForm _popup;
    private SettingsForm? _settingsForm;
    private StatisticsForm? _statisticsForm;
    private SpeedOverlay? _overlay;
    private DesktopWidget? _widget;
    private Icon? _trayIcon;
    private string? _trayIconKey;
    private string? _pendingFlag;

    public TrayApp()
    {
        _updater = new Updater(_settings);
        _popup = new PopupForm(_state, _settings, ShowSettings, ShowStatistics);
        _popup.CreateControl();

        var menu = new ContextMenuStrip();
        _updateItem.Click += async (_, _) => await InstallUpdateAsync();
        menu.Items.Add(_updateItem);
        menu.Items.Add("Open Netlane", null, (_, _) => ShowPopup());
        menu.Items.Add("Refresh", null, async (_, _) => await _state.RefreshAsync());
        menu.Items.Add("Statistics…", null, (_, _) => ShowStatistics());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());

        _tray.Icon = _appIcon ?? SystemIcons.Application;
        _tray.Text = "Netlane";
        _tray.ContextMenuStrip = menu;
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };
        _tray.BalloonTipClicked += async (_, _) =>
        {
            if (_updateBalloon) await InstallUpdateAsync();
            else ShowPopup();
        };
        _tray.Visible = true;

        Application.ApplicationExit += (_, _) => _tray.Visible = false;
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => _state.Stats.Save();
        _state.Changed += OnStateChanged;
        _state.IpChanged += OnIpChanged;
        _settings.Changed += ApplySettings;
        ApplySettings();
        _state.Start();

        _updater.Changed += OnUpdaterChanged;
        _updater.UpdateFound += release =>
        {
            _updateBalloon = true;
            _tray.ShowBalloonTip(8000, $"Netlane {release.Version} is available", "Click to update now.", ToolTipIcon.Info);
        };
        _updater.Start();
    }

    private void OnUpdaterChanged()
    {
        var latest = _updater.Latest?.Version;
        _updateItem.Visible = latest is not null;
        _updateItem.Enabled = !_updater.IsBusy;
        _updateItem.Text = _updater.State switch
        {
            UpdateState.Downloading => $"Downloading update… {_updater.Progress}%",
            UpdateState.Installing => "Installing update…",
            _ => $"Update to {latest}…",
        };
    }

    private bool _installing;

    private async Task InstallUpdateAsync()
    {
        if (_installing || _updater.Latest is not { } release) return;
        _installing = true;
        try
        {
            if (await _updater.InstallAsync())
            {
                ExitThread();
                return;
            }
            if (_updater.State == UpdateState.Failed
                && MessageBox.Show($"{_updater.Error}\n\nOpen the release page to download it manually?", "Netlane update",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.PageUrl.ToString()) { UseShellExecute = true });
        }
        finally { _installing = false; }
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

    private void ShowStatistics()
    {
        if (_statisticsForm is null || _statisticsForm.IsDisposed)
        {
            _statisticsForm = new StatisticsForm(_state, _settings, _appIcon);
            _statisticsForm.FormClosed += (_, _) => _statisticsForm = null;
            _statisticsForm.Show();
        }
        _statisticsForm.WindowState = FormWindowState.Normal;
        _statisticsForm.Activate();
    }

    private void ShowSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settings, _updater, InstallUpdateAsync, _appIcon);
            _settingsForm.FormClosed += (_, _) => _settingsForm = null;
            _settingsForm.Show();
        }
        _settingsForm.WindowState = FormWindowState.Normal;
        _settingsForm.Activate();
    }

    private void ApplySettings()
    {
        _state.SetTrafficMonitoring(true); // always on: statistics are recorded even when the speed box is hidden
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
        _updateBalloon = false;
        _tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
    }

    protected override void ExitThreadCore()
    {
        _updater.Stop();
        _state.Stats.Save();
        _statisticsForm?.Close();
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
