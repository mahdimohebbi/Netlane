using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Netlane.Core;

namespace Netlane;

/// Borderless, draggable, non-activating window base for the speed pill and the desktop widget.
public abstract class FloatingWindow : Form
{
    protected readonly AppState State;
    protected readonly AppSettings Settings;
    private readonly Action _onClick;
    private Point _pressPoint;
    private Point _pressLocation;
    private bool _dragging;

    protected FloatingWindow(AppState state, AppSettings settings, Action onClick)
    {
        State = state;
        Settings = settings;
        _onClick = onClick;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Text = "Netlane";
    }

    protected float Dpi => DeviceDpi / 96f;
    protected Palette Palette => Theme.Current(Settings);
    protected abstract Point? SavedLocation { get; set; }
    protected abstract Point DefaultLocation(Rectangle workArea);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleWindow(Handle, Palette.Dark, round: true);
    }

    public void Place()
    {
        var saved = SavedLocation;
        var screen = saved is { } p ? Screen.FromPoint(p) : Screen.PrimaryScreen!;
        var work = screen.WorkingArea;
        var location = saved ?? DefaultLocation(work);
        // Keep the window on-screen if monitors changed since the position was saved.
        location.X = Math.Clamp(location.X, work.Left, Math.Max(work.Left, work.Right - Width));
        location.Y = Math.Clamp(location.Y, work.Top, Math.Max(work.Top, work.Bottom - Height));
        Location = location;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressPoint = Cursor.Position;
        _pressLocation = Location;
        _dragging = false;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Capture || e.Button != MouseButtons.Left) return;
        var delta = new Size(Cursor.Position.X - _pressPoint.X, Cursor.Position.Y - _pressPoint.Y);
        if (!_dragging && Math.Abs(delta.Width) + Math.Abs(delta.Height) < 4) return;
        _dragging = true;
        Location = _pressLocation + delta;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        Capture = false;
        if (_dragging)
        {
            SavedLocation = Location;
            Settings.Save(notify: false);
        }
        else
        {
            _onClick();
        }
        _dragging = false;
    }

    protected static void DrawText(Graphics g, string text, Font font, Color color, PointF at) =>
        TextRenderer.DrawText(g, text, font, Point.Round(at), color, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

    protected static Size Measure(string text, Font font) =>
        TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
}

/// The Windows stand-in for the macOS menu bar label: flag plus live ↓/↑ speed, docked near the tray.
public sealed class SpeedOverlay : FloatingWindow
{
    private Font? _font;
    private float _fontScale;

    public SpeedOverlay(AppState state, AppSettings settings, Action onClick) : base(state, settings, onClick)
    {
        TopMost = true;
    }

    protected override Point? SavedLocation { get => Settings.OverlayLocation; set => Settings.OverlayLocation = value; }

    protected override Point DefaultLocation(Rectangle work) =>
        new(work.Right - Width - (int)(12 * Dpi), work.Bottom - Height - (int)(8 * Dpi));

    private IReadOnlyList<(string Arrow, double Rate, Color Color)> Rows()
    {
        var palette = Palette;
        var down = ("↓", State.DownloadRate, Settings.CustomColors ? AppSettings.ParseColor(Settings.DownloadColor, palette.Text) : palette.Text);
        var up = ("↑", State.UploadRate, Settings.CustomColors ? AppSettings.ParseColor(Settings.UploadColor, palette.Text) : palette.Text);
        return Settings.Bandwidth switch
        {
            BandwidthData.Download => [down],
            BandwidthData.Upload => [up],
            _ => Settings.UploadOnTop ? [up, down] : [down, up],
        };
    }

    private bool DoubleLine => Settings.DoubleLine && Settings.Bandwidth == BandwidthData.Both;

    private Font CurrentFont()
    {
        var size = DoubleLine ? 8f : 9.5f;
        if (_font is null || Math.Abs(_fontScale - size) > 0.01f)
        {
            _font?.Dispose();
            _font = new Font("Segoe UI Semibold", size);
            _fontScale = size;
        }
        return _font;
    }

    /// Resizes to fit the current layout; width comes from a worst-case string so the pill doesn't jitter.
    public void Relayout()
    {
        var font = CurrentFont();
        var cell = Measure("↓ " + (Settings.Unit == SpeedUnit.Bits ? "888.8 Kb/s" : "888.8 KB/s"), font);
        var rows = Rows().Count;
        var pad = (int)(8 * Dpi);
        var flagWidth = (int)(26 * Dpi);
        var textWidth = DoubleLine ? cell.Width : cell.Width * rows + pad * (rows - 1);
        var height = Math.Max((int)(30 * Dpi), (DoubleLine ? cell.Height * 2 : cell.Height) + pad);
        var size = new Size(pad + flagWidth + pad + textWidth + pad, height);
        if (Size != size)
        {
            Size = size;
            if (Visible) Place();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var palette = Palette;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(palette.Card);
        using (var pen = new Pen(palette.Border))
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        var pad = 8 * Dpi;
        var flagHeight = 19 * Dpi;
        var code = State.Geo?.CountryCode;
        FlagImages.Draw(g, FlagImages.Cached(code), code, new RectangleF(pad, (Height - flagHeight) / 2, 26 * Dpi, flagHeight), palette);

        var font = CurrentFont();
        var rows = Rows();
        var cell = Measure("↓ " + (Settings.Unit == SpeedUnit.Bits ? "888.8 Kb/s" : "888.8 KB/s"), font);
        var x = pad + 26 * Dpi + pad;
        if (DoubleLine)
        {
            var y = (Height - cell.Height * 2) / 2f;
            foreach (var row in rows)
            {
                DrawText(g, $"{row.Arrow} {Throughput.FormatRate(row.Rate, Settings.Unit)}", font, row.Color, new PointF(x, y));
                y += cell.Height;
            }
        }
        else
        {
            var y = (Height - cell.Height) / 2f;
            foreach (var row in rows)
            {
                DrawText(g, $"{row.Arrow} {Throughput.FormatRate(row.Rate, Settings.Unit)}", font, row.Color, new PointF(x, y));
                x += cell.Width + pad;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font?.Dispose();
        base.Dispose(disposing);
    }
}

/// A desktop card with the same small/medium layouts as the macOS widget. Sits behind normal windows.
public sealed class DesktopWidget : FloatingWindow
{
    private static readonly Font Big = new("Segoe UI Semibold", 13f);
    private static readonly Font Sub = new("Segoe UI", 9.5f);
    private static readonly Font Tiny = new("Segoe UI", 8f);
    private static readonly Font Mono = new("Consolas", 9f);

    public DesktopWidget(AppState state, AppSettings settings, Action onClick) : base(state, settings, onClick) { }

    protected override Point? SavedLocation { get => Settings.WidgetLocation; set => Settings.WidgetLocation = value; }

    protected override Point DefaultLocation(Rectangle work) =>
        new(work.Right - Width - (int)(24 * Dpi), work.Top + (int)(24 * Dpi));

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    public void Relayout()
    {
        var size = Settings.WidgetSize == WidgetSize.Small
            ? new Size((int)(170 * Dpi), (int)(170 * Dpi))
            : new Size((int)(360 * Dpi), (int)(170 * Dpi));
        if (Size != size)
        {
            Size = size;
            if (Visible) Place();
        }
        Invalidate();
    }

    public void SendToBottom() => SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_BOTTOM, NOSIZE|NOMOVE|NOACTIVATE

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SendToBottom();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var palette = Palette;
        var s = Dpi;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(palette.Card);
        using (var pen = new Pen(palette.Border))
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

        var pad = 16 * s;
        var geo = State.Geo;
        if (geo is null)
        {
            DrawText(g, State.IsOnline ? "Locating…" : "Offline", Big, palette.Text, new PointF(pad, pad));
            DrawText(g, State.IsOnline ? "Fetching your IP" : "Retrying soon", Sub, palette.Secondary, new PointF(pad, pad + 26 * s));
            return;
        }

        var stale = State.GeoError is not null ? "  ⚠" : "";
        var flagRect = new RectangleF(pad, pad, 64 * s, 48 * s);
        FlagImages.Draw(g, FlagImages.Cached(geo.CountryCode), geo.CountryCode, flagRect, palette);
        var leftWidth = Settings.WidgetSize == WidgetSize.Small ? Width - 2 * pad : Width / 2f - pad;
        var countryY = Height - pad - (Settings.WidgetSize == WidgetSize.Small ? 40 : 44) * s;
        DrawClipped(g, geo.Country + stale, Big, palette.Text, new RectangleF(pad, countryY, leftWidth, 24 * s));

        if (Settings.WidgetSize == WidgetSize.Small)
        {
            DrawClipped(g, geo.Ip, Mono, palette.Secondary, new RectangleF(pad, countryY + 24 * s, leftWidth, 18 * s));
            return;
        }

        DrawClipped(g, string.IsNullOrEmpty(geo.City) ? "—" : geo.City, Sub, palette.Secondary, new RectangleF(pad, countryY + 24 * s, leftWidth, 18 * s));
        var x = Width / 2f + 4 * s;
        var width = Width - x - pad;
        var y = pad - 2 * s;
        foreach (var (label, value, font) in new[] { ("Public IP", geo.Ip, Mono), ("ISP", Or(geo.Isp), Sub), ("Timezone", Or(geo.Timezone), Sub) })
        {
            DrawText(g, label, Tiny, palette.Secondary, new PointF(x, y));
            DrawClipped(g, value, font, palette.Text, new RectangleF(x, y + 14 * s, width, 18 * s));
            y += 36 * s;
        }
        DrawText(g, $"Updated {geo.FetchedAt:HH:mm}", Tiny, palette.Secondary, new PointF(x, Height - pad - 12 * s));
    }

    private static void DrawClipped(Graphics g, string text, Font font, Color color, RectangleF r) =>
        TextRenderer.DrawText(g, text, font, Rectangle.Round(r), color,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

    private static string Or(string value) => string.IsNullOrEmpty(value) ? "—" : value;
}
