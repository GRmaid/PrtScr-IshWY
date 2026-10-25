using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;

namespace PrtScr.App.Services;

/// <summary>
/// 把剪贴板文字渲染成一张带圆角背景的 PNG，再交给原生贴图窗口显示。
/// 文字贴图本质仍是图片贴图，因此标注、裁剪、另存为和 OCR 都可以直接复用。
/// </summary>
internal static class PinTextCardRenderer
{
    private const float FontSizeDips = 16f;
    private const float MaxTextWidthDips = 360f;
    private const float MinCardWidthDips = 150f;
    private const float PaddingDips = 22f;
    private const float CornerRadiusDips = 12f;
    private const int MaxCharacters = 4000;
    private const int MaxLines = 32;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    public static byte[]? Render(string text, string backgroundHex, out int width, out int height)
    {
        width = 0;
        height = 0;

        var normalized = Normalize(text);
        if (normalized.Length == 0)
            return null;

        var scale = GetDpiScale();
        var fontSize = FontSizeDips * scale;
        var maxTextWidth = MaxTextWidthDips * scale;
        var padding = PaddingDips * scale;
        var corner = CornerRadiusDips * scale;

        using var font = new Font("Microsoft YaHei UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using var measure = Graphics.FromImage(measureBitmap);
        measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var measureFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
        measureFormat.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.NoWrap;

        var lines = Wrap(normalized, font, maxTextWidth, measure, measureFormat);
        if (lines.Count == 0)
            return null;

        if (lines.Count > MaxLines)
        {
            lines = lines.Take(MaxLines).ToList();
            lines[^1] = TrimToWidth(lines[^1] + "…", font, maxTextWidth, measure, measureFormat);
        }

        var lineHeight = Math.Max(fontSize * 1.45f, font.GetHeight(measure));
        var textWidth = 0f;
        foreach (var line in lines)
            textWidth = Math.Max(textWidth, measure.MeasureString(line, font, int.MaxValue, measureFormat).Width);

        var cardWidth = Math.Max(MinCardWidthDips * scale, Math.Min(maxTextWidth, textWidth) + padding * 2);
        var cardHeight = padding * 2 + lineHeight * lines.Count;
        width = Math.Max(1, (int)Math.Ceiling(cardWidth));
        height = Math.Max(1, (int)Math.Ceiling(cardHeight));

        var background = ParseColor(backgroundHex) ?? Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F);
        var lum = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
        var textColor = lum > 0.56
            ? Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)
            : Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5);
        var borderColor = lum > 0.56
            ? Color.FromArgb(52, 0, 0, 0)
            : Color.FromArgb(64, 0xFF, 0xFF, 0xFF);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        using var path = RoundedRectangle(new RectangleF(0.5f, 0.5f, width - 1.5f, height - 1.5f), corner);
        using (var fill = new SolidBrush(Color.FromArgb(248, background)))
            graphics.FillPath(fill, path);
        using (var border = new Pen(borderColor, Math.Max(1f, scale)))
            graphics.DrawPath(border, path);

        using var textBrush = new SolidBrush(textColor);
        using var drawFormat = (StringFormat)StringFormat.GenericTypographic.Clone();
        drawFormat.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
        drawFormat.Trimming = StringTrimming.None;

        var y = padding;
        foreach (var line in lines)
        {
            if (line.Length > 0)
                graphics.DrawString(line, font, textBrush, padding, y, drawFormat);
            y += lineHeight;
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static List<string> Wrap(string text, Font font, float maxWidth, Graphics graphics, StringFormat format)
    {
        var result = new List<string>();
        var paragraphs = text.Split('\n');

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
            {
                result.Add("");
                continue;
            }

            var line = new StringBuilder();
            foreach (var ch in paragraph)
            {
                line.Append(ch);
                var measured = graphics.MeasureString(line.ToString(), font, int.MaxValue, format).Width;
                if (measured <= maxWidth || line.Length <= 1)
                    continue;

                var current = line.ToString();
                var cut = FindLastWhitespace(current);
                if (cut > 0 && cut < current.Length - 1)
                {
                    result.Add(current[..cut].TrimEnd());
                    line.Clear();
                    line.Append(current[(cut + 1)..]);
                }
                else
                {
                    result.Add(current[..^1]);
                    line.Clear();
                    line.Append(ch);
                }
            }

            if (line.Length > 0)
                result.Add(line.ToString());
        }

        return result;
    }

    private static int FindLastWhitespace(string value)
    {
        for (var i = value.Length - 2; i > 0; i--)
        {
            if (char.IsWhiteSpace(value[i]))
                return i;
        }
        return -1;
    }

    private static string TrimToWidth(string value, Font font, float maxWidth, Graphics graphics, StringFormat format)
    {
        if (graphics.MeasureString(value, font, int.MaxValue, format).Width <= maxWidth)
            return value;

        for (var length = value.Length - 1; length > 0; length--)
        {
            var candidate = value[..length] + "…";
            if (graphics.MeasureString(candidate, font, int.MaxValue, format).Width <= maxWidth)
                return candidate;
        }
        return "…";
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var value = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Trim();
        if (value.Length > MaxCharacters)
            value = value[..MaxCharacters].TrimEnd() + "…";

        var lines = value.Split('\n');
        var output = new StringBuilder(value.Length);
        var blank = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                blank++;
                if (blank > 1)
                    continue;
            }
            else
            {
                blank = 0;
            }

            if (output.Length > 0)
                output.Append('\n');
            output.Append(line);
        }
        return output.ToString().Trim();
    }

    private static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
    {
        var diameter = Math.Max(1f, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color? ParseColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim();
        if (!value.StartsWith('#'))
            value = "#" + value;
        if (value.Length != 7 || !uint.TryParse(value[1..],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            return null;
        return Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static float GetDpiScale()
    {
        try
        {
            var dpi = GetDpiForSystem();
            return Math.Clamp(dpi / 96f, 1f, 3f);
        }
        catch
        {
            return 1f;
        }
    }
}
