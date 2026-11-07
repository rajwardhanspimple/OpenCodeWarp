using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace OpenCodeWarp.App;

internal static class AppIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(int size = 64)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(Theme.SurfaceAlt);
            graphics.FillEllipse(background, 0, 0, size - 1, size - 1);

            using var ring = new Pen(Theme.Accent, size * 0.055f);
            graphics.DrawArc(ring, size * 0.12f, size * 0.12f, size * 0.76f, size * 0.76f, -40, 285);

            using var font = new Font("Segoe UI", size * 0.4f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawString("W", font, Brushes.White,
                new RectangleF(0, size * 0.015f, size, size), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
