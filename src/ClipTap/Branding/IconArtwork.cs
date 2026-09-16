using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClipTap.Branding;

public static class IconArtwork
{
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
        using var line = new Pen(Color.White, 3.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var paper = Rounded(17, 18, 32, 37, 5);
        graphics.DrawPath(line, paper);
        using var clip = Rounded(25, 11, 16, 13, 4);
        graphics.FillPath(background, clip);
        graphics.DrawPath(line, clip);
        graphics.DrawLine(line, 26, 35, 40, 35);
        graphics.DrawLine(line, 26, 44, 35, 44);
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
