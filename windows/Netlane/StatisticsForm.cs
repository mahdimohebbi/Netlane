using System.Globalization;
using System.Text;
using Netlane.Core;

namespace Netlane;

/// Download & Upload Statistics: a live chart of the last minute, and a history you can filter by day, week or month.
public sealed class StatisticsForm : Form
{
    private const int ContentWidth = 580;
    private readonly AppState _state;
    private readonly AppSettings _settings;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private Palette _palette;
    private StatsRange _range = StatsRange.Week;

    private readonly FlowLayoutPanel _root = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(18, 14, 18, 16),
    };
    private readonly FlowLayoutPanel _livePanel = Column();
    private readonly FlowLayoutPanel _historyPanel = Column();
    private readonly List<Button> _tabButtons = [];
    private readonly List<Button> _rangeButtons = [];

    // Live
    private readonly Label _downNow = Big(), _upNow = Big(), _downPeak = Small(), _upPeak = Small();
    private readonly Label _sessionDown = Value(), _sessionUp = Value(), _todayDown = Value(), _todayUp = Value(), _monthDown = Value(), _monthUp = Value();
    private readonly LiveChart _liveChart = new();

    // History
    private readonly Label _rangeTitle = new() { Tag = "secondary", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly Label _sumDown = Value(), _sumUp = Value(), _sumThirdTitle = new() { Tag = "secondary", AutoSize = true, Font = Theme.Caption }, _sumThird = Value();
    private readonly BarChart _barChart = new();
    private readonly Panel _tableScroll = new() { AutoScroll = true, Height = 176, Width = ContentWidth - 2 };
    private readonly TableLayoutPanel _table = new() { ColumnCount = 4, AutoSize = true };
    private readonly Label _tableTitle = new() { Tag = "secondary", Font = Theme.Caption, AutoSize = true };
    private readonly List<Card> _cards = [];
    private bool _showingLive = true;
    private readonly List<Label[]> _rows = [];
    private StatsRange _rowsRange;

    public StatisticsForm(AppState state, AppSettings settings, Icon? icon)
    {
        _state = state;
        _settings = settings;
        _palette = Theme.Current(settings);

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Netlane Statistics";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = Theme.Body;

        _root.Controls.Add(Segments(["Live", "History"], _tabButtons, i => ShowTab(i == 0)));
        BuildLive();
        BuildHistory();
        _root.Controls.Add(_livePanel);
        _root.Controls.Add(_historyPanel);
        Controls.Add(_root);

        _tick.Tick += (_, _) => UpdateView();
        _tick.Start();
        _settings.Changed += OnSettingsChanged;
        ShowTab(true);
        OnSettingsChanged();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleWindow(Handle, _palette.Dark, round: false);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tick.Dispose();
        _settings.Changed -= OnSettingsChanged;
        base.OnFormClosed(e);
    }

    private void OnSettingsChanged()
    {
        _palette = Theme.Current(_settings);
        Theme.Apply(this, _palette);
        foreach (var card in _cards) card.BorderColor = _palette.Border;
        _liveChart.Colors = _barChart.Colors = _palette;
        if (IsHandleCreated) Theme.StyleWindow(Handle, _palette.Dark, round: false);
        StyleSegments(_tabButtons, _showingLive ? 0 : 1);
        StyleSegments(_rangeButtons, (int)_range);
        _rows.Clear(); // rebuilt with the new colours on the next update
        UpdateView();
    }

    // ---- Layout -------------------------------------------------------------------------------------------------

    private void BuildLive()
    {
        var tiles = Row();
        tiles.Controls.Add(Tile("Download", _downNow, _downPeak, down: true));
        tiles.Controls.Add(Tile("Upload", _upNow, _upPeak, down: false));
        _livePanel.Controls.Add(tiles);

        var title = new Label { Text = "Last 60 seconds", Font = new Font("Segoe UI Semibold", 10f), AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        _liveChart.Width = ContentWidth - 24 - 8;
        _livePanel.Controls.Add(Boxed(ContentWidth, title, _liveChart));

        var totals = Row();
        totals.Controls.Add(Total("This session", _sessionDown, _sessionUp, 3));
        totals.Controls.Add(Total("Today", _todayDown, _todayUp, 3));
        totals.Controls.Add(Total("This month", _monthDown, _monthUp, 3));
        _livePanel.Controls.Add(totals);
        _livePanel.Controls.Add(new Label
        {
            Text = "Counted while Netlane is running. Stored only on this PC.", Tag = "secondary", Font = Theme.Small, AutoSize = true,
            Margin = new Padding(0, 4, 0, 0),
        });
    }

    private void BuildHistory()
    {
        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        header.Controls.Add(Segments(["Day", "Week", "Month"], _rangeButtons, i => { _range = (StatsRange)i; StyleSegments(_rangeButtons, i); UpdateView(); }));
        header.Controls.Add(_rangeTitle);
        _rangeTitle.Margin = new Padding(14, 8, 0, 0);
        _historyPanel.Controls.Add(header);

        var summary = Row();
        summary.Controls.Add(SummaryCard("Downloaded", _sumDown, null, down: true));
        summary.Controls.Add(SummaryCard("Uploaded", _sumUp, null, down: false));
        summary.Controls.Add(SummaryCard(null, _sumThird, _sumThirdTitle, down: null));
        _historyPanel.Controls.Add(summary);

        _barChart.Width = ContentWidth - 24 - 8;
        _historyPanel.Controls.Add(Boxed(ContentWidth, _barChart));

        _tableScroll.Controls.Add(_table);
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        for (var i = 0; i < 3; i++) _table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        var head = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        for (var i = 0; i < 3; i++) head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        head.Controls.Add(_tableTitle, 0, 0);
        foreach (var (text, col) in new[] { ("Download", 1), ("Upload", 2), ("Total", 3) })
            head.Controls.Add(new Label { Text = text, Tag = "secondary", Font = Theme.Caption, AutoSize = false, Width = 112, TextAlign = ContentAlignment.TopRight }, col, 0);
        _historyPanel.Controls.Add(Boxed(ContentWidth, head, _tableScroll));

        var footer = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(new Label { Text = "Stored only on this PC · kept for 12 months", Tag = "secondary", Font = Theme.Small, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        var export = new Button { Text = "Export CSV…", AutoSize = true };
        export.Click += (_, _) => ExportCsv();
        var clear = new Button { Text = "Clear History…", AutoSize = true };
        clear.Click += (_, _) => ClearHistory();
        footer.Controls.Add(export, 1, 0);
        footer.Controls.Add(clear, 2, 0);
        _historyPanel.Controls.Add(footer);
    }

    private Card Tile(string title, Label big, Label caption, bool down)
    {
        var heading = new Label
        {
            Text = "●  " + title, Font = Theme.Caption, AutoSize = true, Tag = down ? "down" : "up", Margin = new Padding(0, 0, 0, 2),
        };
        return Boxed((ContentWidth - 12) / 2, heading, big, caption);
    }

    private Card Total(string title, Label down, Label up, int count)
    {
        var heading = new Label { Text = title, Tag = "secondary", AutoSize = true, Font = Theme.Small };
        return Boxed((ContentWidth - 12 * (count - 1)) / count, heading, down, up);
    }

    private Card SummaryCard(string? title, Label value, Label? dynamicTitle, bool? down)
    {
        var heading = dynamicTitle ?? new Label { Text = title, Font = Theme.Caption, AutoSize = true };
        heading.Tag = down switch { true => "down", false => "up", _ => "secondary" };
        return Boxed((ContentWidth - 24) / 3, heading, value);
    }

    private Card Boxed(int width, params Control[] children)
    {
        var card = new Card { Margin = new Padding(0, 0, 12, 10) };
        var flow = Column();
        flow.Location = new Point(card.Padding.Left, card.Padding.Top);
        flow.MinimumSize = new Size(width - card.Padding.Horizontal, 0);
        foreach (var c in children) flow.Controls.Add(c);
        card.Controls.Add(flow);
        _cards.Add(card);
        return card;
    }

    private static FlowLayoutPanel Column() => new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty,
    };

    private static FlowLayoutPanel Row() => new() { AutoSize = true, WrapContents = false, Margin = Padding.Empty };

    private static Label Big() => new() { Font = new Font("Segoe UI Semibold", 20f), AutoSize = true };
    private static Label Small() => new() { Tag = "secondary", Font = Theme.Small, AutoSize = true };
    private static Label Value() => new() { Font = new Font("Segoe UI Semibold", 10.5f), AutoSize = true };

    private FlowLayoutPanel Segments(string[] names, List<Button> store, Action<int> pick)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        for (var i = 0; i < names.Length; i++)
        {
            var index = i;
            var button = new Button { Text = names[i], AutoSize = false, Size = new Size(names.Length == 2 ? 100 : 76, 30), Margin = new Padding(0, 0, 4, 0) };
            button.Click += (_, _) => pick(index);
            store.Add(button);
            row.Controls.Add(button);
        }
        return row;
    }

    private void StyleSegments(List<Button> buttons, int active)
    {
        for (var i = 0; i < buttons.Count; i++)
        {
            var on = i == active;
            buttons[i].BackColor = on ? _palette.Accent : _palette.Card;
            buttons[i].ForeColor = on ? (_palette.Dark ? Color.Black : Color.White) : _palette.Text;
            buttons[i].Font = on ? new Font("Segoe UI Semibold", 9.5f) : Theme.Body;
        }
    }

    private void ShowTab(bool live)
    {
        _showingLive = live;
        _livePanel.Visible = live;
        _historyPanel.Visible = !live;
        StyleSegments(_tabButtons, live ? 0 : 1);
        UpdateView();
    }

    // ---- Data ---------------------------------------------------------------------------------------------------

    private void UpdateView()
    {
        if (IsDisposed) return;
        if (_showingLive) UpdateLive(); else UpdateHistory();
    }

    private double ToUnits(double bytesPerSecond) =>
        _settings.Unit == SpeedUnit.Bits ? bytesPerSecond * 8 / 1_000_000 : bytesPerSecond / 1_000_000;

    private void UpdateLive()
    {
        var stats = _state.Stats;
        _downNow.Text = Throughput.FormatRate(_state.DownloadRate, _settings.Unit);
        _upNow.Text = Throughput.FormatRate(_state.UploadRate, _settings.Unit);
        _downPeak.Text = $"Peak {Throughput.FormatRate(stats.PeakDown, _settings.Unit)} this session";
        _upPeak.Text = $"Peak {Throughput.FormatRate(stats.PeakUp, _settings.Unit)} this session";
        _liveChart.Samples = stats.Live.ToArray();
        _liveChart.ToDisplay = ToUnits;
        _liveChart.Unit = _settings.Unit == SpeedUnit.Bits ? "Mb/s" : "MB/s";

        Pair(_sessionDown, _sessionUp, stats.SessionRx, stats.SessionTx);
        var today = stats.TotalToday();
        Pair(_todayDown, _todayUp, today.Rx, today.Tx);
        var month = stats.TotalThisMonth();
        Pair(_monthDown, _monthUp, month.Rx, month.Tx);
        _liveChart.Invalidate();
    }

    private static void Pair(Label down, Label up, ulong rx, ulong tx)
    {
        down.Text = "↓ " + ByteFormat.Format(rx);
        up.Text = "↑ " + ByteFormat.Format(tx);
    }

    private void UpdateHistory()
    {
        var now = DateTimeOffset.Now;
        var buckets = _state.Stats.Log.Buckets(_range, now);
        var down = buckets.Aggregate(0UL, (t, b) => t + b.Rx);
        var up = buckets.Aggregate(0UL, (t, b) => t + b.Tx);
        _sumDown.Text = ByteFormat.Format(down);
        _sumUp.Text = ByteFormat.Format(up);
        if (_range == StatsRange.Day)
        {
            var busiest = buckets.MaxBy(b => b.Total);
            _sumThirdTitle.Text = "Busiest hour";
            _sumThird.Text = busiest is { Total: > 0 } ? $"{busiest.Start.LocalDateTime:HH}:00 · {ByteFormat.Format(busiest.Total)}" : "—";
        }
        else
        {
            _sumThirdTitle.Text = "Daily average";
            _sumThird.Text = ByteFormat.Format((down + up) / (ulong)Math.Max(buckets.Count, 1));
        }
        _rangeTitle.Text = _range switch
        {
            StatsRange.Day => "Today, " + now.LocalDateTime.ToString("ddd d MMM", CultureInfo.CurrentCulture),
            StatsRange.Week => "Last 7 days",
            _ => "Last 30 days",
        };

        _barChart.Buckets = buckets;
        _barChart.Label = i => _range switch
        {
            StatsRange.Day => i % 6 == 0 ? i.ToString("00") : "",
            StatsRange.Week => buckets[i].Start.LocalDateTime.ToString("ddd", CultureInfo.CurrentCulture),
            _ => i % 5 == 0 || i == buckets.Count - 1 ? buckets[i].Start.LocalDateTime.Day.ToString() : "",
        };
        _barChart.Invalidate();

        _tableTitle.Text = _range == StatsRange.Day ? "Hour" : "Day";
        var ordered = Enumerable.Reverse(buckets).ToList();
        if (_rows.Count != ordered.Count || _rowsRange != _range) RebuildTable(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var b = ordered[i];
            var local = b.Start.LocalDateTime;
            _rows[i][0].Text = _range == StatsRange.Day ? $"{local:HH}:00" : local.ToString("ddd d MMM", CultureInfo.CurrentCulture);
            _rows[i][1].Text = ByteFormat.Format(b.Rx);
            _rows[i][2].Text = ByteFormat.Format(b.Tx);
            _rows[i][3].Text = ByteFormat.Format(b.Total);
        }
    }

    /// Only when the range changes: rebuilding on every tick would reset the scroll position while you read.
    private void RebuildTable(int count)
    {
        _table.SuspendLayout();
        _table.Controls.Clear();
        _table.RowStyles.Clear();
        _table.RowCount = count;
        _rows.Clear();
        _rowsRange = _range;
        for (var row = 0; row < count; row++)
        {
            var cells = new[]
            {
                Cell(null, left: true), Cell(ChartColors.DownText(_palette)), Cell(ChartColors.UpText(_palette)), Cell(null, bold: true),
            };
            for (var col = 0; col < 4; col++) _table.Controls.Add(cells[col], col, row);
            _rows.Add(cells);
        }
        _table.ResumeLayout();
        _tableScroll.AutoScrollPosition = Point.Empty;
    }

    private Label Cell(Color? color, bool left = false, bool bold = false) => new()
    {
        AutoSize = false, Width = left ? 190 : 112, Height = 22, Margin = Padding.Empty,
        TextAlign = left ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight,
        ForeColor = color ?? _palette.Text, Font = bold ? new Font("Segoe UI Semibold", 9.5f) : Theme.Body, BackColor = Color.Transparent,
    };

    // ---- Actions ------------------------------------------------------------------------------------------------

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog { FileName = "Netlane-traffic.csv", Filter = "CSV file (*.csv)|*.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, _state.Stats.Log.ToCsv(), new UTF8Encoding(false)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, e.Message, "Netlane", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ClearHistory()
    {
        var answer = MessageBox.Show(this, "This permanently deletes the recorded download and upload totals on this PC.",
            "Clear all traffic history?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;
        _state.Stats.ClearHistory();
        UpdateView();
    }
}
