using Netlane.Core;

namespace Netlane;

/// The tray flyout: mirrors the macOS popover (header, Network card, Speed card, footer).
public sealed class PopupForm : Form
{
    private const int ContentWidth = 320;
    private readonly AppState _state;
    private readonly AppSettings _settings;
    private readonly Action _openSettings;
    private readonly Action _openStatistics;
    private Palette _palette;

    private readonly PictureBox _flag = new() { Size = new Size(56, 42), Margin = new Padding(0, 0, 12, 0) };
    private readonly Label _country = new() { Font = Theme.Title, AutoSize = true, Margin = Padding.Empty };
    private readonly Label _city = new() { Tag = "secondary", AutoSize = true, Margin = Padding.Empty };
    private readonly Button _refresh = new() { Text = "↻", Size = new Size(32, 32), Font = new Font("Segoe UI", 12f) };
    private readonly Label _geoError = new() { Tag = "warning", Font = Theme.Small, AutoSize = true, Visible = false };
    private readonly Label _publicIp = new() { Font = Theme.Mono, AutoSize = true, Cursor = Cursors.Hand };
    private readonly Label _localIp = new() { Font = Theme.Mono, AutoSize = true };
    private readonly Label _isp = new() { AutoSize = true };
    private readonly Label _connection = new() { AutoSize = true };
    private readonly Label _vpn = new() { Tag = "accent", Font = Theme.Caption, AutoSize = true, Text = "VPN likely", Visible = false };
    private readonly Label _dns = new() { Font = Theme.Mono, AutoSize = true, TextAlign = ContentAlignment.TopRight };
    private readonly Label _timezone = new() { AutoSize = true };
    private readonly Label _latency = new() { AutoSize = true };
    private readonly Label _download = new() { AutoSize = true };
    private readonly Label _upload = new() { AutoSize = true };
    private readonly Label _speedError = new() { Tag = "warning", Font = Theme.Small, AutoSize = true, Visible = false };
    private readonly Label _speedStatus = new() { Tag = "secondary", Font = Theme.Small, AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _runTest = new() { Text = "Run test", AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly Label _statsDown = new() { Tag = "down", Font = Theme.Caption, AutoSize = true };
    private readonly Label _statsUp = new() { Tag = "up", Font = Theme.Caption, AutoSize = true };
    private readonly Label _statsToday = new() { Tag = "secondary", Font = Theme.Small, AutoSize = true };
    private readonly Sparkline _spark = new() { Margin = new Padding(0, 0, 0, 2) };
    private readonly List<Card> _cards = [];
    private DateTime _copiedUntil;
    private readonly System.Windows.Forms.Timer _copiedTimer = new() { Interval = 1200 };

    public DateTime LastHidden { get; private set; }

    public PopupForm(AppState state, AppSettings settings, Action openSettings, Action openStatistics)
    {
        _state = state;
        _settings = settings;
        _openSettings = openSettings;
        _openStatistics = openStatistics;
        _palette = Theme.Current(settings);

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);
        Font = Theme.Body;
        Text = "Netlane";

        var root = Column();
        root.Controls.Add(BuildHeader());
        root.Controls.Add(BuildNetworkCard());
        root.Controls.Add(BuildStatisticsCard());
        root.Controls.Add(BuildSpeedCard());
        root.Controls.Add(BuildFooter());
        Controls.Add(root);

        _refresh.Click += async (_, _) => await _state.RefreshAsync();
        _runTest.Click += async (_, _) => await _state.RunSpeedTestAsync();
        _publicIp.Click += (_, _) => CopyIp();
        _copiedTimer.Tick += (_, _) => { _copiedTimer.Stop(); UpdateView(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };

        ApplyTheme();
        UpdateView();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: keep out of Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleWindow(Handle, _palette.Dark, round: true);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_settings.PopupClosesAutomatically) Hide();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) LastHidden = DateTime.UtcNow;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(_palette.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    public void ApplyTheme()
    {
        _palette = Theme.Current(_settings);
        Theme.Apply(this, _palette);
        foreach (var card in _cards) card.BorderColor = _palette.Border;
        if (IsHandleCreated) Theme.StyleWindow(Handle, _palette.Dark, round: true);
        UpdateView();
    }

    /// Positions the flyout in the corner nearest the taskbar on the screen under the cursor.
    public void ShowNearTray()
    {
        UpdateView();
        var screen = Screen.FromPoint(Cursor.Position);
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        var size = PreferredSize;
        const int margin = 12;
        var x = work.Right - size.Width - margin;
        var y = work.Bottom - size.Height - margin;
        if (work.Top > bounds.Top) y = work.Top + margin;           // taskbar on top
        if (work.Left > bounds.Left) x = work.Left + margin;        // taskbar on the left
        Location = new Point(Math.Max(work.Left, x), Math.Max(work.Top, y));
        Show();
        Activate();
    }

    public void UpdateView()
    {
        var geo = _state.Geo;
        _country.Text = geo?.Country ?? (_state.IsOnline ? "Locating…" : "Offline");
        _city.Text = string.IsNullOrEmpty(geo?.City) ? "—" : geo.City;
        _flag.Image?.Dispose();
        _flag.Image = RenderFlag(geo?.CountryCode);
        _refresh.Enabled = !_state.IsRefreshing;
        _refresh.Text = _state.IsRefreshing ? "…" : "↻";

        _geoError.Visible = _state.GeoError is not null;
        _geoError.Text = _state.GeoError ?? "";
        _publicIp.Text = DateTime.UtcNow < _copiedUntil ? "Copied ✓" : geo?.Ip ?? "—";
        _localIp.Text = _state.Local.LocalIp;
        _isp.Text = Join(" · ", geo?.Isp, geo?.Asn);
        _connection.Text = _state.Local.ConnectionType;
        _vpn.Visible = _state.Local.VpnLikely;
        _dns.Text = _state.Local.DnsServers.Count > 0 ? string.Join("\n", _state.Local.DnsServers) : "—";
        _timezone.Text = string.IsNullOrEmpty(geo?.Timezone) ? "—" : geo.Timezone;

        var stats = _state.Stats;
        _statsDown.Text = "↓ " + Throughput.FormatRate(_state.DownloadRate, _settings.Unit);
        _statsUp.Text = "↑ " + Throughput.FormatRate(_state.UploadRate, _settings.Unit);
        var today = stats.TotalToday();
        _statsToday.Text = $"Today {ByteFormat.Format(today.Rx)} ↓ · {ByteFormat.Format(today.Tx)} ↑";
        _spark.Samples = stats.Live.ToArray();
        _spark.Invalidate();

        var speed = _state.Speed;
        _latency.Text = speed?.LatencyMs is { } ms ? $"{ms:0} ms" : "—";
        _download.Text = speed?.DownloadMbps is { } d ? Throughput.FormatMbps(d, _settings.Unit) : "—";
        _upload.Text = speed?.UploadMbps is { } u ? Throughput.FormatMbps(u, _settings.Unit) : "—";
        _speedError.Visible = _state.SpeedError is not null;
        _speedError.Text = _state.SpeedError ?? "";
        var running = _state.SpeedPhase.Length > 0;
        _speedStatus.Text = running ? _state.SpeedPhase : speed is null ? "" : $"Last run {speed.Date:HH:mm}";
        _runTest.Enabled = !running && _state.IsOnline;
    }

    private Bitmap RenderFlag(string? code)
    {
        var bitmap = new Bitmap(_flag.Width, _flag.Height);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(_palette.Background);
        if (code is not null || _state.Geo is not null)
            FlagImages.Draw(g, FlagImages.Cached(code), code, new RectangleF(0, 0, bitmap.Width, bitmap.Height), _palette);
        else
        {
            using var emoji = new Font("Segoe UI Emoji", 22f);
            TextRenderer.DrawText(g, _state.IsOnline ? "🌐" : "⚠", emoji, new Rectangle(Point.Empty, bitmap.Size), _palette.Text);
        }
        return bitmap;
    }

    private void CopyIp()
    {
        if (_state.Geo?.Ip is not { } ip) return;
        try { Clipboard.SetText(ip); } catch (System.Runtime.InteropServices.ExternalException) { return; }
        _copiedUntil = DateTime.UtcNow.AddMilliseconds(1200);
        _copiedTimer.Stop();
        _copiedTimer.Start();
        UpdateView();
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        FixWidth(header, ContentWidth);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var names = Column();
        names.Anchor = AnchorStyles.Left;
        names.Controls.Add(_country);
        names.Controls.Add(_city);
        _refresh.Anchor = AnchorStyles.Right;
        header.Controls.Add(_flag, 0, 0);
        header.Controls.Add(names, 1, 0);
        header.Controls.Add(_refresh, 2, 0);
        return header;
    }

    private Control BuildNetworkCard()
    {
        var connection = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _vpn.Margin = new Padding(0, 1, 6, 0);
        connection.Controls.Add(_vpn);
        connection.Controls.Add(_connection);
        return BuildCard("NETWORK", _geoError, [
            ("Public IP", _publicIp), ("Local IP", _localIp), ("ISP", _isp),
            ("Connection", connection), ("DNS", _dns), ("Timezone", _timezone),
        ], null);
    }

    private Control BuildStatisticsCard()
    {
        var card = new Card();
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Location = new Point(card.Padding.Left, card.Padding.Top) };
        FixWidth(table, ContentWidth - card.Padding.Horizontal);
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var heading = new Label { Text = "STATISTICS", Tag = "secondary", Font = Theme.Caption, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        table.Controls.Add(heading, 0, 0);
        table.SetColumnSpan(heading, 2);
        var text = Column();
        text.Controls.Add(_statsDown);
        text.Controls.Add(_statsUp);
        text.Controls.Add(_statsToday);
        table.Controls.Add(text, 0, 1);
        table.Controls.Add(_spark, 1, 1);
        var open = new Button { Text = "Open Statistics", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        open.Click += (_, _) => { Hide(); _openStatistics(); };
        table.Controls.Add(open, 0, 2);
        table.SetColumnSpan(open, 2);
        card.Controls.Add(table);
        _cards.Add(card);
        return card;
    }

    private Control BuildSpeedCard()
    {
        var bottom = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        FixWidth(bottom, ContentWidth - 24);
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_speedStatus, 0, 0);
        bottom.Controls.Add(_runTest, 1, 0);
        return BuildCard("SPEED", _speedError, [("Latency", _latency), ("Download", _download), ("Upload", _upload)], bottom);
    }

    private Card BuildCard(string title, Label error, (string Label, Control Value)[] rows, Control? footer)
    {
        var card = new Card();
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Location = new Point(card.Padding.Left, card.Padding.Top) };
        FixWidth(table, ContentWidth - card.Padding.Horizontal);
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var heading = new Label { Text = title, Tag = "secondary", Font = Theme.Caption, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        table.Controls.Add(heading, 0, 0);
        table.SetColumnSpan(heading, 2);
        table.Controls.Add(error, 0, 1);
        table.SetColumnSpan(error, 2);
        error.MaximumSize = new Size(table.MinimumSize.Width, 0);
        var row = 2;
        foreach (var (label, value) in rows)
        {
            table.Controls.Add(new Label { Text = label, Tag = "secondary", AutoSize = true, Margin = new Padding(0, 3, 12, 3) }, 0, row);
            value.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            value.Margin = new Padding(0, 3, 0, 3);
            if (value is Label l) l.MaximumSize = new Size(table.MinimumSize.Width - 90, 0);
            table.Controls.Add(value, 1, row++);
        }
        if (footer is not null)
        {
            table.Controls.Add(footer, 0, row);
            table.SetColumnSpan(footer, 2);
        }
        card.Controls.Add(table);
        _cards.Add(card);
        return card;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        FixWidth(footer, ContentWidth);
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var settings = new Button { Text = "⚙  Settings…", AutoSize = true, Anchor = AnchorStyles.Left };
        settings.Click += (_, _) => { Hide(); _openSettings(); };
        var quit = new Button { Text = "Quit", AutoSize = true, Anchor = AnchorStyles.Right };
        quit.Click += (_, _) => Application.Exit();
        footer.Controls.Add(settings, 0, 0);
        footer.Controls.Add(quit, 1, 0);
        return footer;
    }

    /// AutoSize tables otherwise shrink to their content, which would make the cards different widths.
    private static void FixWidth(Control control, int width)
    {
        control.MinimumSize = new Size(width, 0);
        control.MaximumSize = new Size(width, 0);
    }

    private static FlowLayoutPanel Column() => new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty,
    };

    private static string Join(string separator, params string?[] parts)
    {
        var text = string.Join(separator, parts.Where(p => !string.IsNullOrEmpty(p)));
        return text.Length > 0 ? text : "—";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _copiedTimer.Dispose();
            _flag.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
