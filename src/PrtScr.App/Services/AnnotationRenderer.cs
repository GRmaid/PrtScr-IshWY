using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PrtScr.App.Services;

/// <summary>
/// 把标注烘焙进原图（输出与原图同分辨率）。坐标全部按图片像素处理。
/// </summary>
public static class AnnotationRenderer
{
    public static Bitmap Render(Image source, IReadOnlyList<Annotation> annotations)
    {
        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawImageUnscaled(source, 0, 0);
        }

        foreach (var annotation in annotations)
        {
            if (annotation.Tool == AnnotationTool.Mosaic)
                ApplyMosaic(bitmap, annotation);
        }

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            foreach (var annotation in annotations)
            {
                if (annotation.Tool == AnnotationTool.Mosaic)
                    continue;
                DrawAnnotation(graphics, annotation);
            }
        }

        return bitmap;
    }

    private static void DrawAnnotation(Graphics graphics, Annotation a)
    {
        var color = Color.FromArgb((int)a.Color);
        using var pen = new Pen(color, Math.Max(1, a.Size))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var brush = new SolidBrush(color);

        switch (a.Tool)
        {
            case AnnotationTool.Rectangle:
            {
                var rect = ToRect(a.Start, a.End);
                graphics.DrawRectangle(pen, rect);
                break;
            }

            case AnnotationTool.Arrow:
            {
                var start = ToPointF(a.Start);
                var end = ToPointF(a.End);
                graphics.DrawLine(pen, start, end);
                DrawArrowHead(graphics, brush, start, end, Math.Max(8, a.Size * 4));
                break;
            }

            case AnnotationTool.Pen:
            {
                if (a.Points.Count >= 2)
                {
                    var points = a.Points.Select(ToPointF).ToArray();
                    graphics.DrawLines(pen, points);
                }
                else if (a.Points.Count == 1)
                {
                    var p = ToPointF(a.Points[0]);
                    graphics.FillEllipse(brush, p.X - a.Size / 2, p.Y - a.Size / 2, a.Size, a.Size);
                }
                break;
            }

            case AnnotationTool.Text:
            {
                using var font = new Font("Microsoft YaHei UI", a.Size, FontStyle.Regular, GraphicsUnit.Pixel);
                graphics.DrawString(a.Text, font, brush, ToPointF(a.Start));
                break;
            }

            case AnnotationTool.Number:
            {
                var center = ToPointF(a.Start);
                var radius = a.Size;
                graphics.FillEllipse(brush, center.X - radius, center.Y - radius, radius * 2, radius * 2);
                using var font = new Font("Segoe UI", radius * 1.2f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var white = new SolidBrush(Color.White);
                var text = a.Number.ToString();
                var size = graphics.MeasureString(text, font);
                graphics.DrawString(text, font, white,
                    center.X - size.Width / 2, center.Y - size.Height / 2);
                break;
            }
        }
    }

    private static void DrawArrowHead(Graphics graphics, Brush brush, PointF start, PointF end, float headSize)
    {
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        const double spread = Math.PI / 7;
        var p1 = new PointF(
            end.X - (float)(headSize * Math.Cos(angle - spread)),
            end.Y - (float)(headSize * Math.Sin(angle - spread)));
        var p2 = new PointF(
            end.X - (float)(headSize * Math.Cos(angle + spread)),
            end.Y - (float)(headSize * Math.Sin(angle + spread)));
        graphics.FillPolygon(brush, new[] { end, p1, p2 });
    }

    /// <summary>对当前位图区域做块平均，形成马赛克。按顺序应用，后画的标注不受影响。</summary>
    private static void ApplyMosaic(Bitmap bitmap, Annotation a)
    {
        var rect = ToRect(a.Start, a.End);
        var x0 = Math.Max(0, (int)rect.X);
        var y0 = Math.Max(0, (int)rect.Y);
        var x1 = Math.Min(bitmap.Width, (int)(rect.X + rect.Width));
        var y1 = Math.Min(bitmap.Height, (int)(rect.Y + rect.Height));
        if (x1 - x0 < 2 || y1 - y0 < 2)
            return;

        var block = Math.Max(4, (int)a.Size);
        var data = bitmap.LockBits(
            new Rectangle(x0, y0, x1 - x0, y1 - y0),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var basePtr = (byte*)data.Scan0;
                var stride = data.Stride;
                var width = x1 - x0;
                var height = y1 - y0;

                for (var by = 0; by < height; by += block)
                {
                    for (var bx = 0; bx < width; bx += block)
                    {
                        var bw = Math.Min(block, width - bx);
                        var bh = Math.Min(block, height - by);
                        long b = 0, g = 0, r = 0, alpha = 0;
                        for (var y = 0; y < bh; y++)
                        {
                            var row = basePtr + (by + y) * stride + bx * 4;
                            for (var x = 0; x < bw; x++)
                            {
                                b += row[x * 4];
                                g += row[x * 4 + 1];
                                r += row[x * 4 + 2];
                                alpha += row[x * 4 + 3];
                            }
                        }

                        var count = bw * bh;
                        var ab = (byte)(b / count);
                        var ag = (byte)(g / count);
                        var ar = (byte)(r / count);
                        var aa = (byte)(alpha / count);

                        for (var y = 0; y < bh; y++)
                        {
                            var row = basePtr + (by + y) * stride + bx * 4;
                            for (var x = 0; x < bw; x++)
                            {
                                row[x * 4] = ab;
                                row[x * 4 + 1] = ag;
                                row[x * 4 + 2] = ar;
                                row[x * 4 + 3] = aa;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static RectangleF ToRect(PointD a, PointD b) => new(
        (float)Math.Min(a.X, b.X),
        (float)Math.Min(a.Y, b.Y),
        (float)Math.Abs(a.X - b.X),
        (float)Math.Abs(a.Y - b.Y));

    private static PointF ToPointF(PointD p) => new((float)p.X, (float)p.Y);
}
