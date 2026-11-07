using System.Drawing.Drawing2D;

namespace OpenCodeWarp.App;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(11, 15, 20);
    public static readonly Color Surface = Color.FromArgb(18, 24, 32);
    public static readonly Color SurfaceAlt = Color.FromArgb(24, 33, 44);
    public static readonly Color SurfaceHover = Color.FromArgb(30, 41, 54);
    public static readonly Color Border = Color.FromArgb(34, 47, 61);
    public static readonly Color Text = Color.FromArgb(231, 238, 244);
    public static readonly Color Muted = Color.FromArgb(139, 152, 165);
    public static readonly Color Accent = Color.FromArgb(61, 220, 151);
    public static readonly Color AccentSoft = Color.FromArgb(22, 70, 54);
    public static readonly Color Cyan = Color.FromArgb(34, 211, 238);
    public static readonly Color Warn = Color.FromArgb(245, 165, 36);
    public static readonly Color Error = Color.FromArgb(248, 113, 113);
    public static readonly Color Info = Color.FromArgb(96, 165, 250);

    public const string FontFamily = "Segoe UI";

    public static Font Font(float size, FontStyle style = FontStyle.Regular)
        => new(FontFamily, size, style, GraphicsUnit.Point);

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var d = radius * 2;
        var arc = new Rectangle(bounds.Location, new Size(d, d));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - d;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - d;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color WithAlpha(Color color, int alpha)
        => Color.FromArgb(Math.Clamp(alpha, 0, 255), color.R, color.G, color.B);

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}
