using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using GdiLinearGradientBrush = System.Drawing.Drawing2D.LinearGradientBrush;
using MediaBrush = Microsoft.UI.Xaml.Media.Brush;

namespace PrtScr.App.Services;

/// <summary>
/// 提示窗的“真实背景”：先截取提示框所在屏幕区域，再做降采样模糊与染色。
/// WinUI 的 SystemBackdrop 在不可激活/离屏小窗上不生效，这里用屏幕快照自行合成，
/// 这样材质、透明度与背景内容都可见，也不再依赖 DWM 的亚克力支持。
/// </summary>
public static class ToastBackdrop
{
    private const int Downsample = 4;

    /// <summary>截取屏幕矩形（物理像素）。失败返回 null。</summary>
    public static Bitmap? Capture(RectInt32 rect)
    {
        if (rect.Width < 2 || rect.Height < 2)
            return null;
        try
        {
            var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.X, rect.Y, 0, 0,
                    new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
            }

            // GDI 的屏幕拷贝通常不写 alpha，全部按不透明处理，否则 ImageBrush 会整张透明。
            ForceOpaque(bitmap);
            return bitmap;
        }
        catch (Exception ex)
        {
            App.Log($"Toast: 截取背景失败 {rect.X},{rect.Y} {rect.Width}x{rect.Height} {ex.Message}");
            return null;
        }
    }
    /// <summary>
    /// 一次截屏同时得到“原始桌面像素”和“材质像素”（可在后台线程调用）。
    /// 原始像素用于提示窗自身的桌面底色：淡入/淡出时露出的就是提示框下面的真实画面，
    /// 不会因为窗口底层是黑色而出现「淡出变黑」。
    /// </summary>
    public static (BackdropPixels Raw, BackdropPixels? Blurred)? CreatePixelsWithRaw(
        RectInt32 rect, string? material, double opacity)
    {
        using var capture = Capture(rect);
        if (capture is null)
            return null;

        var raw = CopyPixels(capture);
        var normalized = ToastAppearance.NormalizeMaterial(material);
        BackdropPixels? blurred = normalized == "solid"
            ? null
            : CreatePixelsFrom(capture,
                new Rectangle(0, 0, capture.Width, capture.Height), normalized, opacity);
        return (raw, blurred);
    }

    /// <summary>截取屏幕并生成纯像素背景（可在后台线程调用）；纯色返回 null。</summary>
    public static BackdropPixels? CreatePixels(RectInt32 rect, string? material, double opacity)
    {
        if (ToastAppearance.NormalizeMaterial(material) == "solid")
            return null;
        using var capture = Capture(rect);
        if (capture is null)
            return null;
        return CreatePixelsFrom(capture,
            new Rectangle(0, 0, capture.Width, capture.Height), material, opacity);
    }

    /// <summary>从整块屏幕图裁一块并生成纯像素背景（可在后台线程调用）。</summary>
    public static BackdropPixels? CreatePixelsFrom(Bitmap source, Rectangle crop, string? material, double opacity)
    {
        if (ToastAppearance.NormalizeMaterial(material) == "solid")
            return null;

        try
        {
            using var rendered = Render(source, crop, material, opacity);
            return rendered is null ? null : CopyPixels(rendered);
        }
        catch (Exception ex)
        {
            App.Log($"Toast: 合成背景失败 {ex.Message}");
            return null;
        }
    }

    /// <summary>从已截好的整块屏幕图裁一块，生成提示框背景刷（必须在 UI 线程调用）。</summary>
    public static MediaBrush? CreateBrushFrom(Bitmap source, Rectangle crop, string? material, double opacity)
    {
        var image = CreatePixelsFrom(source, crop, material, opacity);
        return image is { } value ? CreateBrushFromPixels(value) : null;
    }

    /// <summary>用后台算好的像素在 UI 线程创建 ImageBrush（WriteableBitmap 有线程亲和性）。</summary>
    public static MediaBrush? CreateBrushFromPixels(BackdropPixels image)
    {
        try
        {
            var writeable = new WriteableBitmap(image.Width, image.Height);
            using (var stream = writeable.PixelBuffer.AsStream())
            {
                stream.Write(image.Pixels, 0, image.Pixels.Length);
            }

            writeable.Invalidate();
            return new ImageBrush
            {
                ImageSource = writeable,
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
        }
        catch (Exception ex)
        {
            App.Log($"Toast: 创建背景刷失败 {ex.Message}");
            return null;
        }
    }

    private static BackdropPixels CopyPixels(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var rowBytes = width * 4;
        var bytes = new byte[rowBytes * height];
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            }
            else
            {
                for (var y = 0; y < height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return new BackdropPixels(bytes, width, height);
    }

    public readonly record struct BackdropPixels(byte[] Pixels, int Width, int Height);
    private static Bitmap? Render(Bitmap source, Rectangle crop, string? material, double opacity)
    {
        var normalized = ToastAppearance.NormalizeMaterial(material);
        var o = ToastAppearance.ClampOpacity(opacity);
        var strength = (o - ToastAppearance.MinOpacity) /
                       (ToastAppearance.MaxOpacity - ToastAppearance.MinOpacity);
        strength = Math.Clamp(strength, 0, 1);

        crop = Rectangle.Intersect(crop, new Rectangle(0, 0, source.Width, source.Height));
        if (crop.Width < 2 || crop.Height < 2)
            return null;

        using var cropped = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(cropped))
        {
            graphics.DrawImage(source,
                new Rectangle(0, 0, crop.Width, crop.Height),
                crop, GraphicsUnit.Pixel);
        }

        var blurPixels = normalized switch
        {
            "mica" => 34.0,
            "liquid" => 18.0,
            _ => 26.0,
        };
        blurPixels *= 0.60 + 0.40 * strength;

        var smallWidth = Math.Max(2, cropped.Width / Downsample);
        var smallHeight = Math.Max(2, cropped.Height / Downsample);
        using var small = new Bitmap(smallWidth, smallHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(cropped, 0, 0, smallWidth, smallHeight);
        }

        BoxBlur(small, Math.Max(1, (int)Math.Round(blurPixels / Downsample)));

        var result = new Bitmap(cropped.Width, cropped.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(result))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(small, 0, 0, result.Width, result.Height);
        }

        ApplyTint(result, normalized, strength);
        return result;
    }

    private static void ApplyTint(Bitmap bitmap, string material, double strength)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width < 1 || height < 1)
            return;

        var alpha = material switch
        {
            "mica" => 0.48 + 0.22 * strength,
            "liquid" => 0.24 + 0.18 * strength,
            _ => 0.36 + 0.22 * strength,
        };
        var tintAlpha = (int)Math.Round(255 * alpha);
        var start = material switch
        {
            "mica" => Color.FromArgb(tintAlpha, 0x1A, 0x1E, 0x24),
            "liquid" => Color.FromArgb(tintAlpha, 0x0A, 0x16, 0x30),
            _ => Color.FromArgb(tintAlpha, 0x1A, 0x1A, 0x1A),
        };
        var end = material switch
        {
            "mica" => Color.FromArgb(tintAlpha, 0x10, 0x13, 0x18),
            "liquid" => Color.FromArgb(tintAlpha, 0x06, 0x10, 0x1E),
            _ => Color.FromArgb(tintAlpha, 0x10, 0x12, 0x16),
        };

        using var graphics = Graphics.FromImage(bitmap);
        using (var brush = new GdiLinearGradientBrush(
                   new Rectangle(0, 0, width, height), start, end, 45f))
        {
            graphics.FillRectangle(brush, 0, 0, width, height);
        }

        if (material == "liquid")
        {
            // 液态玻璃：斜向的冷色高光，让材质和纯色/亚克力区分开。
            var highlight = (int)Math.Round(255 * (0.10 + 0.10 * strength));
            using var brush = new GdiLinearGradientBrush(
                new Rectangle(0, 0, width, height),
                Color.FromArgb(0, 0x9A, 0xC8, 0xFF),
                Color.FromArgb(highlight, 0x9A, 0xC8, 0xFF),
                35f);
            var blend = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(0, 0x9A, 0xC8, 0xFF),
                    Color.FromArgb(highlight, 0x9A, 0xC8, 0xFF),
                    Color.FromArgb(0, 0x9A, 0xC8, 0xFF),
                },
                Positions = new[] { 0f, 0.52f, 1f },
            };
            brush.InterpolationColors = blend;
            graphics.FillRectangle(brush, 0, 0, width, height);
        }
    }

    private static void ForceOpaque(Bitmap bitmap)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var pointer = (byte*)data.Scan0;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var row = pointer + y * data.Stride;
                    for (var x = 0; x < bitmap.Width; x++)
                        row[x * 4 + 3] = 0xFF;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>可分离盒式模糊，只在小图上跑；半径按降采样比例折算。</summary>
    private static void BoxBlur(Bitmap bitmap, int radius)
    {
        if (radius < 1 || bitmap.Width < 2 || bitmap.Height < 2)
            return;

        var width = bitmap.Width;
        var height = bitmap.Height;
        var pixels = new int[width * height];
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var pointer = (byte*)data.Scan0;
                for (var y = 0; y < height; y++)
                {
                    var row = (int*)(pointer + y * data.Stride);
                    for (var x = 0; x < width; x++)
                        pixels[y * width + x] = row[x];
                }

                var temp = new int[width * height];
                var window = radius * 2 + 1;
                for (var y = 0; y < height; y++)
                {
                    long b = 0, g = 0, r = 0, a = 0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var sample = pixels[y * width + Math.Clamp(k, 0, width - 1)];
                        b += sample & 0xFF;
                        g += (sample >> 8) & 0xFF;
                        r += (sample >> 16) & 0xFF;
                        a += (sample >> 24) & 0xFF;
                    }

                    for (var x = 0; x < width; x++)
                    {
                        temp[y * width + x] = (int)(b / window) |
                                             (int)(g / window) << 8 |
                                             (int)(r / window) << 16 |
                                             (int)(a / window) << 24;
                        var remove = pixels[y * width + Math.Clamp(x - radius, 0, width - 1)];
                        var add = pixels[y * width + Math.Clamp(x + radius + 1, 0, width - 1)];
                        b += (add & 0xFF) - (remove & 0xFF);
                        g += ((add >> 8) & 0xFF) - ((remove >> 8) & 0xFF);
                        r += ((add >> 16) & 0xFF) - ((remove >> 16) & 0xFF);
                        a += ((add >> 24) & 0xFF) - ((remove >> 24) & 0xFF);
                    }
                }

                for (var x = 0; x < width; x++)
                {
                    long b = 0, g = 0, r = 0, a = 0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var sample = temp[Math.Clamp(k, 0, height - 1) * width + x];
                        b += sample & 0xFF;
                        g += (sample >> 8) & 0xFF;
                        r += (sample >> 16) & 0xFF;
                        a += (sample >> 24) & 0xFF;
                    }

                    for (var y = 0; y < height; y++)
                    {
                        var index = y * width + x;
                        var value = (int)(b / window) |
                                    (int)(g / window) << 8 |
                                    (int)(r / window) << 16 |
                                    (int)(a / window) << 24;
                        var row = (int*)(pointer + y * data.Stride);
                        row[x] = value;

                        var remove = temp[Math.Clamp(y - radius, 0, height - 1) * width + x];
                        var add = temp[Math.Clamp(y + radius + 1, 0, height - 1) * width + x];
                        b += (add & 0xFF) - (remove & 0xFF);
                        g += ((add >> 8) & 0xFF) - ((remove >> 8) & 0xFF);
                        r += ((add >> 16) & 0xFF) - ((remove >> 16) & 0xFF);
                        a += ((add >> 24) & 0xFF) - ((remove >> 24) & 0xFF);
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
