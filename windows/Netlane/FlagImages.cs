using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Netlane.Core;

namespace Netlane;

/// Downloads flag PNGs once and caches them on disk, because Windows can't render flag emoji.
public static class FlagImages
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly Dictionary<string, Image> Memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Netlane", "flags");

    public static Image? Cached(string? code) =>
        code is not null && Memory.TryGetValue(code, out var image) ? image : null;

    public static async Task<Image?> GetAsync(string code)
    {
        if (Cached(code) is { } hit) return hit;
        if (Flags.ImageUrl(code) is not { } url) return null;
        var file = Path.Combine(CacheDir, code.ToLowerInvariant() + ".png");
        try
        {
            byte[] bytes;
            if (File.Exists(file))
            {
                bytes = await File.ReadAllBytesAsync(file);
            }
            else
            {
                bytes = await Http.GetByteArrayAsync(url);
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllBytesAsync(file, bytes);
            }
            var image = Image.FromStream(new MemoryStream(bytes));
            Memory[code] = image;
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// Draws the flag (or a country-code badge when the image isn't available) letterboxed into a rectangle.
    public static void Draw(Graphics g, Image? flag, string? code, RectangleF bounds, Palette palette)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (flag is not null)
        {
            var scale = Math.Min(bounds.Width / flag.Width, bounds.Height / flag.Height);
            var size = new SizeF(flag.Width * scale, flag.Height * scale);
            var rect = new RectangleF(bounds.X + (bounds.Width - size.Width) / 2, bounds.Y + (bounds.Height - size.Height) / 2, size.Width, size.Height);
            using var clip = Theme.RoundedRect(rect, Math.Max(1.5f, rect.Height * 0.12f));
            var state = g.Save();
            g.SetClip(clip);
            g.DrawImage(flag, rect);
            g.Restore(state);
            using var pen = new Pen(Color.FromArgb(50, 0, 0, 0));
            g.DrawPath(pen, clip);
            return;
        }
        var badge = new RectangleF(bounds.X, bounds.Y + bounds.Height * 0.12f, bounds.Width, bounds.Height * 0.76f);
        using var path = Theme.RoundedRect(badge, badge.Height * 0.2f);
        using var fill = new SolidBrush(palette.Accent);
        g.FillPath(fill, path);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI Semibold", Math.Max(5, badge.Height * 0.5f), GraphicsUnit.Pixel);
        using var text = new SolidBrush(palette.Dark ? Color.Black : Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Flags.IsValidCode(code) ? code!.ToUpperInvariant() : "?", font, text, badge, format);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon TrayIcon(Image? flag, string? code, Palette palette)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            Draw(g, flag, code, new RectangleF(0, 4, 32, 24), palette);
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
}
