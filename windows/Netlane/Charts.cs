using System.Drawing.Drawing2D;
using Netlane.Core;

namespace Netlane;

internal static class ChartColors
{
    public static readonly Color Down = Color.FromArgb(52, 199, 89);
    public static readonly Color Up = Color.FromArgb(10, 132, 255);

    /// Text-safe variants: the chart colours are too light to read as text on a white background.
    public static Color DownText(Palette p) => p.Dark ? Down : Color.FromArgb(26, 127, 55);
    public static Color UpText(Palette p) => p.Dark ? Color.FromArgb(90, 170, 255) : Color.FromArgb(0, 96, 223);

    /// 1, 2, 5 or 10 times a power of ten, at or above the value.
    public static double NiceMax(double value)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return 1;
        var scale = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var f = value / scale;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * scale;
    }
}

/// Line chart of the last 60 seconds of download and upload speed.
internal sealed class LiveChart : Control
{
    private const int LeftGutter = 52;

    public LiveChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Height = 180;
    }

    public IReadOnlyList<TrafficSample> Samples { get; set; } = [];
    public Func<double, double> ToDisplay { get; set; } = v => v;
    public string Unit { get; set; } = "";
    public Palette Colors { get; set; } = Palette.DarkMode;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = new RectangleF(LeftGutter, 14, Math.Max(10, Width - LeftGutter - 6), Math.Max(10, Height - 14 - 6));
        var peak = Samples.Count == 0 ? 0 : Samples.Max(s => Math.Max(ToDisplay(s.Down), ToDisplay(s.Up)));
        var max = ChartColors.NiceMax(Math.Max(peak, 1));

        using var grid = new Pen(Colors.Border);
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            TextRenderer.DrawText(g, (max * i / 4).ToString("0.##"), Theme.Small, new Rectangle(0, (int)y - 8, LeftGutter - 6, 16),
                Colors.Secondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        TextRenderer.DrawText(g, Unit, Theme.Small, new Rectangle(0, 0, LeftGutter - 6, 14), Colors.Secondary,
            TextFormatFlags.Right | TextFormatFlags.NoPadding);

        Series(g, plot, max, s => s.Down, ChartColors.Down);
        Series(g, plot, max, s => s.Up, ChartColors.Up);
    }

    private void Series(Graphics g, RectangleF plot, double max, Func<TrafficSample, double> pick, Color color)
    {
        if (Samples.Count < 2) return;
        var step = plot.Width / (TrafficStats.LiveWindow - 1);
        var points = new PointF[Samples.Count];
        for (var i = 0; i < points.Length; i++)
        {
            var x = plot.Right - (points.Length - 1 - i) * step;
            var y = plot.Bottom - (float)(Math.Clamp(ToDisplay(pick(Samples[i])) / max, 0, 1) * plot.Height);
            points[i] = new PointF(x, y);
        }
        var area = points.Append(new PointF(points[^1].X, plot.Bottom)).Append(new PointF(points[0].X, plot.Bottom)).ToArray();
        using var fill = new SolidBrush(Color.FromArgb(45, color));
        g.FillPolygon(fill, area);
        using var pen = new Pen(color, 2f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, points);
    }
}

/// Grouped download/upload bars for the History view.
internal sealed class BarChart : Control
{
    private const int LeftGutter = 62;
    private const int BottomGutter = 20;

    public BarChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Height = 190;
    }

    public IReadOnlyList<TrafficBucket> Buckets { get; set; } = [];
    public Func<int, string> Label { get; set; } = _ => "";
    public Palette Colors { get; set; } = Palette.DarkMode;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var plot = new RectangleF(LeftGutter, 8, Math.Max(10, Width - LeftGutter - 6), Math.Max(10, Height - 8 - BottomGutter));
        var peak = Buckets.Count == 0 ? 0 : Buckets.Max(b => (double)Math.Max(b.Rx, b.Tx));
        var max = ChartColors.NiceMax(Math.Max(peak, 1_000_000));

        using var grid = new Pen(Colors.Border);
        for (var i = 0; i <= 2; i++)
        {
            var y = plot.Bottom - plot.Height * i / 2;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            TextRenderer.DrawText(g, ByteFormat.Format((ulong)(max * i / 2)), Theme.Small, new Rectangle(0, (int)y - 8, LeftGutter - 6, 16),
                Colors.Secondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        if (Buckets.Count == 0) return;
        var slot = plot.Width / Buckets.Count;
        var barWidth = (float)Math.Clamp(slot * 0.38, 2, 14);
        using var down = new SolidBrush(ChartColors.Down);
        using var up = new SolidBrush(ChartColors.Up);
        for (var i = 0; i < Buckets.Count; i++)
        {
            var cx = plot.Left + slot * (i + 0.5f);
            Bar(g, down, cx - barWidth - 0.5f, barWidth, plot, Buckets[i].Rx / max);
            Bar(g, up, cx + 0.5f, barWidth, plot, Buckets[i].Tx / max);
            var label = Label(i);
            if (label.Length > 0)
                TextRenderer.DrawText(g, label, Theme.Small, new Rectangle((int)(cx - slot), (int)plot.Bottom + 3, (int)(slot * 2), 16),
                    Colors.Secondary, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
    }

    private static void Bar(Graphics g, Brush brush, float x, float width, RectangleF plot, double fraction)
    {
        var height = (float)Math.Max(2, Math.Clamp(fraction, 0, 1) * plot.Height);
        g.FillRectangle(brush, x, plot.Bottom - height, width, height);
    }
}

/// Tiny trend line for the popup's Statistics card.
internal sealed class Sparkline : Control
{
    public Sparkline()
    {
        DoubleBuffered = true;
        Size = new Size(110, 40);
    }

    public IReadOnlyList<TrafficSample> Samples { get; set; } = [];

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Samples.Count < 2) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var max = Math.Max(1, Samples.Max(s => Math.Max(s.Down, s.Up)));
        Draw(g, max, s => s.Down, ChartColors.Down);
        Draw(g, max, s => s.Up, ChartColors.Up);
    }

    private void Draw(Graphics g, double max, Func<TrafficSample, double> pick, Color color)
    {
        var step = (Width - 4f) / (TrafficStats.LiveWindow - 1);
        var points = Samples.Select((s, i) => new PointF(
            Width - 2 - (Samples.Count - 1 - i) * step,
            Height - 3 - (float)(pick(s) / max * (Height - 6)))).ToArray();
        using var pen = new Pen(color, 2f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, points);
    }
}
