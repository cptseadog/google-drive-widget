using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Original cloud-and-drive mark. Shared by the header and generated Windows icons.
public static class WidgetLogo
{
    static GraphicsPath Rounded(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Draw(Graphics graphics, RectangleF bounds)
    {
        var state = graphics.Save();
        try
        {
            graphics.TranslateTransform(bounds.X, bounds.Y);
            graphics.ScaleTransform(bounds.Width / 64f, bounds.Height / 64f);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var tile = Rounded(3, 3, 58, 58, 16))
            using (var fill = new LinearGradientBrush(new PointF(10, 5), new PointF(52, 61),
                Color.FromArgb(51, 165, 255), Color.FromArgb(39, 80, 211)))
                graphics.FillPath(fill, tile);

            using (var cloud = new GraphicsPath())
            {
                cloud.StartFigure();
                cloud.AddBezier(21, 37, 11, 37, 11, 24, 21, 23);
                cloud.AddBezier(21, 23, 23, 11, 39, 11, 42, 24);
                cloud.AddBezier(42, 24, 53, 22, 55, 37, 44, 37);
                cloud.AddLine(44, 37, 21, 37);
                cloud.CloseFigure();
                graphics.FillPath(Brushes.White, cloud);
            }
            using (var drive = Rounded(14, 43, 36, 11, 4))
                graphics.FillPath(Brushes.White, drive);
            using (var slot = new Pen(Color.FromArgb(48, 105, 215), 2.4f))
            {
                slot.StartCap = slot.EndCap = LineCap.Round;
                graphics.DrawLine(slot, 20, 48.5f, 32, 48.5f);
            }
            using (var light = new SolidBrush(Color.FromArgb(34, 190, 142)))
                graphics.FillEllipse(light, 41, 46, 5, 5);
        }
        finally { graphics.Restore(state); }
    }

    public static Bitmap Render(int size)
    {
        // Supersampling keeps the cloud and rounded tile smooth at small icon sizes.
        using (var large = new Bitmap(size * 4, size * 4, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(large))
                Draw(graphics, new RectangleF(0, 0, large.Width, large.Height));
            var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(result))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(large, new Rectangle(0, 0, size, size), 0, 0,
                    large.Width, large.Height, GraphicsUnit.Pixel);
            }
            return result;
        }
    }
}