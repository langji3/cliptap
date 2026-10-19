using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClipTap.Branding;

public static class IconArtwork
{
    public static Color DefaultAccent => Color.FromArgb(42, 45, 52);
    public static Bitmap Render(int size, Color accent)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.ScaleTransform(size / 64f, size / 64f);
        using var background = new SolidBrush(accent);
        using var outer = Rounded(1, 1, 62, 62, 17);
        graphics.FillPath(background, outer);
        using var ink = new SolidBrush(Color.White);
        // Two offset tiles suggest saved content, with a deliberately spare silhouette.
        using var back = Rounded(14, 14, 27, 29, 5);
        using var outline = new Pen(Color.FromArgb(190, 195, 205), 3.5f);
        graphics.DrawPath(outline, back);
        using var front = Rounded(25, 24, 25, 28, 5);
        graphics.FillPath(ink, front);
        return bitmap;
    }

    private static GraphicsPath Rounded(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath(); var diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
}
