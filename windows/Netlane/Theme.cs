using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Netlane;

public sealed record Palette(
    bool Dark, Color Background, Color Card, Color Border, Color Text, Color Secondary, Color Accent, Color Warning)
{
    public static readonly Palette Light = new(false,
        Color.FromArgb(243, 243, 243), Color.White, Color.FromArgb(224, 224, 224),
        Color.FromArgb(26, 26, 26), Color.FromArgb(110, 110, 110), Color.FromArgb(0, 103, 192), Color.FromArgb(196, 110, 0));

    public static readonly Palette DarkMode = new(true,
        Color.FromArgb(32, 32, 32), Color.FromArgb(43, 43, 43), Color.FromArgb(58, 58, 58),
        Color.FromArgb(242, 242, 242), Color.FromArgb(160, 160, 160), Color.FromArgb(96, 205, 255), Color.FromArgb(255, 176, 64));
}

public static class Theme
{
    public static readonly Font Body = new("Segoe UI", 9.5f);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font Caption = new("Segoe UI Semibold", 8.5f);
    public static readonly Font Title = new("Segoe UI Semibold", 14f);
    public static readonly Font Mono = new("Consolas", 9.5f);

    public static Palette Current(AppSettings settings) => settings.Theme switch
    {
        ThemeMode.Light => Palette.Light,
        ThemeMode.Dark => Palette.DarkMode,
        _ => SystemIsDark() ? Palette.DarkMode : Palette.Light,
    };

    public static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception) { return false; }
    }

    /// Recolours a control tree. Tags: "card" panels use the card colour, "secondary"/"warning"/"accent" labels change text colour.
    public static void Apply(Control root, Palette p)
    {
        switch (root)
        {
            case Form form:
                form.BackColor = p.Background;
                form.ForeColor = p.Text;
                break;
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = p.Border;
                button.FlatAppearance.MouseOverBackColor = p.Dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(234, 234, 234);
                button.BackColor = p.Card;
                button.ForeColor = p.Text;
                break;
            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = p.Card;
                combo.ForeColor = p.Text;
                break;
            case Label or CheckBox or RadioButton:
                root.BackColor = Color.Transparent;
                root.ForeColor = (root.Tag as string) switch
                {
                    "secondary" => p.Secondary,
                    "warning" => p.Warning,
                    "accent" => p.Accent,
                    _ => p.Text,
                };
                break;
            default:
                root.BackColor = root.Tag as string == "card" ? p.Card : root.Parent?.BackColor ?? p.Background;
                root.ForeColor = p.Text;
                break;
        }
        foreach (Control child in root.Controls) Apply(child, p);
        root.Invalidate();
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// Windows 11 rounded corners and dark title bars; silently ignored on older versions.
    public static void StyleWindow(IntPtr hwnd, bool dark, bool round)
    {
        var darkValue = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref darkValue, sizeof(int));
        if (!round) return;
        var corner = 2;
        DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
    }
}

/// A rounded, bordered container used for the popup's cards.
public sealed class Card : Panel
{
    public Card()
    {
        Tag = "card";
        DoubleBuffered = true;
        Padding = new Padding(12, 10, 12, 10);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0, 0, 0, 10);
    }

    public Color BorderColor { get; set; } = Color.Gainsboro;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8);
        using var fill = new SolidBrush(BackColor);
        using var pen = new Pen(BorderColor);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
    }
}
