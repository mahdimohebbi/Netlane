using Netlane.Core;

namespace Netlane;

public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly FlowLayoutPanel _root = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(18, 12, 18, 18),
    };
    private readonly List<Control> _speedControls = [];
    private readonly List<Control> _colorControls = [];
    private readonly List<Control> _widgetControls = [];
    private CheckBox? _uploadOnTop;

    public SettingsForm(AppSettings settings, Icon? icon)
    {
        _settings = settings;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Netlane Settings";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = Theme.Body;

        Section("General");
        Check("Launch at login", LaunchAtLogin.IsEnabled, v =>
        {
            try { LaunchAtLogin.Set(v); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Netlane", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        });
        Check("Popup closes automatically when clicking outside", settings.PopupClosesAutomatically, v => settings.PopupClosesAutomatically = v);
        Check("Show a notification when the IP address changes", settings.ShowIpChangeToast, v => settings.ShowIpChangeToast = v);
        Radios("Theme", [("System", ThemeMode.System), ("Light", ThemeMode.Light), ("Dark", ThemeMode.Dark)], settings.Theme, v => settings.Theme = v);
        Radios("Unit (Bits or Bytes)", [("Mb/s", SpeedUnit.Bits), ("MB/s", SpeedUnit.Bytes)], settings.Unit, v => settings.Unit = v);

        Section("Live speed");
        Check("Show live upload & download speed near the taskbar", settings.ShowSpeedOverlay, v => settings.ShowSpeedOverlay = v);
        _speedControls.Add(Combo("Bandwidth Data", [("Download & Upload", BandwidthData.Both), ("Download only", BandwidthData.Download), ("Upload only", BandwidthData.Upload)],
            settings.Bandwidth, v => settings.Bandwidth = v));
        _speedControls.Add(Radios("Button Style", [("Single line", false), ("Double line", true)], settings.DoubleLine, v => settings.DoubleLine = v));
        _uploadOnTop = Check("Show upload on top", settings.UploadOnTop, v => settings.UploadOnTop = v);
        _speedControls.Add(_uploadOnTop);
        _speedControls.Add(Check("Enable custom font color", settings.CustomColors, v => settings.CustomColors = v));
        var colors = Row();
        colors.Controls.Add(ColorButton("Download data color", () => settings.DownloadColor, v => settings.DownloadColor = v));
        colors.Controls.Add(ColorButton("Upload data color", () => settings.UploadColor, v => settings.UploadColor = v));
        _root.Controls.Add(colors);
        _colorControls.Add(colors);

        Section("Desktop widget");
        Check("Show desktop widget", settings.ShowDesktopWidget, v => settings.ShowDesktopWidget = v);
        _widgetControls.Add(Radios("Size", [("Small", WidgetSize.Small), ("Medium", WidgetSize.Medium)], settings.WidgetSize, v => settings.WidgetSize = v));

        Controls.Add(_root);
        settings.Changed += OnSettingsChanged;
        OnSettingsChanged();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleWindow(Handle, Theme.Current(_settings).Dark, round: false);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _settings.Changed -= OnSettingsChanged;
        base.OnFormClosed(e);
    }

    private void OnSettingsChanged()
    {
        var palette = Theme.Current(_settings);
        Theme.Apply(this, palette);
        if (IsHandleCreated) Theme.StyleWindow(Handle, palette.Dark, round: false);
        foreach (var c in _speedControls) c.Enabled = _settings.ShowSpeedOverlay;
        foreach (var c in _colorControls) c.Enabled = _settings.ShowSpeedOverlay && _settings.CustomColors;
        foreach (var c in _widgetControls) c.Enabled = _settings.ShowDesktopWidget;
        if (_uploadOnTop is not null)
            _uploadOnTop.Enabled = _settings.ShowSpeedOverlay && _settings.DoubleLine && _settings.Bandwidth == BandwidthData.Both;
    }

    private void Section(string title)
    {
        var first = _root.Controls.Count == 0;
        _root.Controls.Add(new Label
        {
            Text = title, Font = new Font("Segoe UI Semibold", 11f), AutoSize = true, Margin = new Padding(0, first ? 4 : 16, 0, 6),
        });
    }

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
        box.CheckedChanged += (_, _) =>
        {
            set(box.Checked);
            _settings.Save();
        };
        _root.Controls.Add(box);
        return box;
    }

    private FlowLayoutPanel Radios<T>(string label, (string Text, T Value)[] options, T current, Action<T> set)
    {
        var row = Row();
        row.Controls.Add(new Label { Text = label + ":", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        foreach (var (text, value) in options)
        {
            var radio = new RadioButton { Text = text, AutoSize = true, Checked = Equals(value, current), Margin = new Padding(0, 3, 12, 0) };
            radio.CheckedChanged += (_, _) =>
            {
                if (!radio.Checked) return;
                set(value);
                _settings.Save();
            };
            row.Controls.Add(radio);
        }
        _root.Controls.Add(row);
        return row;
    }

    private FlowLayoutPanel Combo<T>(string label, (string Text, T Value)[] options, T current, Action<T> set)
    {
        var row = Row();
        row.Controls.Add(new Label { Text = label + ":", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        foreach (var (text, _) in options) combo.Items.Add(text);
        combo.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => Equals(o.Value, current)));
        combo.SelectedIndexChanged += (_, _) =>
        {
            set(options[combo.SelectedIndex].Value);
            _settings.Save();
        };
        row.Controls.Add(combo);
        _root.Controls.Add(row);
        return row;
    }

    private Control ColorButton(string label, Func<string> get, Action<string> set)
    {
        var row = Row();
        row.Margin = new Padding(0, 0, 18, 0);
        row.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        var swatch = new Button { Width = 44, Height = 24, Text = "" };
        swatch.Paint += (_, e) =>
        {
            using var brush = new SolidBrush(AppSettings.ParseColor(get(), Color.White));
            e.Graphics.FillRectangle(brush, 4, 4, swatch.Width - 9, swatch.Height - 9);
        };
        swatch.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { Color = AppSettings.ParseColor(get(), Color.White), FullOpen = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            set(AppSettings.ToHex(dialog.Color));
            _settings.Save();
            swatch.Invalidate();
        };
        row.Controls.Add(swatch);
        return row;
    }

    private static FlowLayoutPanel Row() => new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
}
