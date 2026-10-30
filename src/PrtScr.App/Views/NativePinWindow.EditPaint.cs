using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PrtScr.App.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Views;

/// <summary>贴图标注：图标、调色盘、马赛克与辅助函数。</summary>
public sealed unsafe partial class NativePinWindow
{
    private static void DrawIconArrowHead(Graphics graphics, Brush brush, float tipX, float tipY)
    {
        var s = Math.Abs(tipX) * 0 + 6f;
        graphics.FillPolygon(brush, new[]
        {
            new PointF(tipX, tipY),
            new PointF(tipX - s, tipY - s * 0.15f),
            new PointF(tipX - s * 0.25f, tipY + s),
        });
    }

    private void DrawPalette(Graphics graphics)
    {
        if (!_paletteOpen)
            return;

        var scale = (float)_dpiScale;
        var svW = 170 * scale;
        var svH = 100 * scale;
        var hueH = 14 * scale;
        var pad = 6 * scale;
        var panelW = svW + pad * 2;
        var panelH = svH + hueH + pad * 3;

        var x = pad;
        var y = _toolbarAtTop ? _toolbarHeight + 8 * scale + pad : _winH - _toolbarHeight - 8 * scale - panelH - pad;
        x = Math.Clamp(x, pad, Math.Max(pad, _winW - panelW - pad));
        y = Math.Clamp(y, pad, Math.Max(pad, _winH - panelH - pad));

        _paletteRect = new RectangleF(x, y, panelW, panelH);
        _svRect = new RectangleF(x + pad, y + pad, svW, svH);
        _hueRect = new RectangleF(x + pad, y + pad + svH + pad, svW, hueH);

        using var bg = new SolidBrush(Color.FromArgb(0xE8, 0x1E, 0x1E, 0x1E));
        graphics.FillRectangle(bg, _paletteRect);

        // SV 方块
        var hueIndex = (int)Math.Round(_hue) % 360;
        if (_svBitmap is null || _svHue != hueIndex)
        {
            _svBitmap?.Dispose();
            _svBitmap = BuildSvBitmap((int)svW, (int)svH, hueIndex);
            _svHue = hueIndex;
        }
        if (_svBitmap is not null)
            graphics.DrawImage(_svBitmap, _svRect);

        // 色相条
        for (var i = 0; i < _hueRect.Width; i++)
        {
            using var pen = new Pen(HsvToColor(i / _hueRect.Width * 360f, 1, 1));
            graphics.DrawLine(pen, _hueRect.X + i, _hueRect.Y, _hueRect.X + i, _hueRect.Y + _hueRect.Height);
        }

        // 标记
        using var white = new Pen(Color.White, Math.Max(1, scale));
        var mx = _svRect.X + _sat * _svRect.Width;
        var my = _svRect.Y + (1 - _val) * _svRect.Height;
        graphics.DrawEllipse(white, mx - 4 * scale, my - 4 * scale, 8 * scale, 8 * scale);

        var hx = _hueRect.X + _hue / 360f * _hueRect.Width;
        graphics.DrawEllipse(white, hx - 4 * scale, _hueRect.Y - 2 * scale, 8 * scale, _hueRect.Height + 4 * scale);
    }

    private void UpdatePaletteFromPoint(int x, int y)
    {
        if (_paletteDrag == 0)
            return;

        if (_paletteDrag == 1)
        {
            _sat = Math.Clamp((x - _svRect.X) / _svRect.Width, 0, 1);
            _val = 1 - Math.Clamp((y - _svRect.Y) / _svRect.Height, 0, 1);
        }
        else if (_paletteDrag == 2)
        {
            _hue = Math.Clamp((x - _hueRect.X) / _hueRect.Width, 0, 1) * 360f;
        }

        var color = HsvToColor(_hue, _sat, _val);
        _colorArgb = (uint)(0xFF << 24 | color.R << 16 | color.G << 8 | color.B);
        RenderEditMode();
    }

    private static Bitmap? BuildSvBitmap(int width, int height, int hue)
    {
        if (width <= 0 || height <= 0)
            return null;

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var ptr = (byte*)data.Scan0;
            for (var y = 0; y < height; y++)
            {
                var v = 1 - y / (float)height;
                var row = ptr + y * data.Stride;
                for (var x = 0; x < width; x++)
                {
                    var color = HsvToColor(hue, x / (float)width, v);
                    row[x * 4] = color.B;
                    row[x * 4 + 1] = color.G;
                    row[x * 4 + 2] = color.R;
                    row[x * 4 + 3] = 255;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private static Color HsvToColor(float h, float s, float v)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = v - c;
        (float r, float g, float b) = h switch
        {
            < 60 => (c, x, 0f),
            < 120 => (x, c, 0f),
            < 180 => (0f, c, x),
            < 240 => (0f, x, c),
            < 300 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return Color.FromArgb(255,
            Math.Clamp((int)((r + m) * 255), 0, 255),
            Math.Clamp((int)((g + m) * 255), 0, 255),
            Math.Clamp((int)((b + m) * 255), 0, 255));
    }

    private int _colorArgbColorId()
    {
        for (var i = 0; i < QuickColors.Length; i++)
        {
            if (QuickColors[i] == _colorArgb)
                return 10 + i;
        }

        // 调色盘选出的自定义颜色不落在任何快速色上，避免误高亮黑色。
        return -1;
    }

    private static RectangleF RectFrom(PointD a, PointD b) => new(
        (float)Math.Min(a.X, b.X), (float)Math.Min(a.Y, b.Y),
        (float)Math.Abs(a.X - b.X), (float)Math.Abs(a.Y - b.Y));

    private void ApplyMosaicToBitmap(Bitmap bitmap, Annotation a, float scale, int offsetY)
    {
        var rect = RectFrom(a.Start, a.End);
        var x0 = Math.Max(0, (int)(rect.X * scale));
        var y0 = Math.Max(0, (int)(rect.Y * scale) + offsetY);
        var x1 = Math.Min(bitmap.Width, (int)((rect.X + rect.Width) * scale));
        var y1 = Math.Min(bitmap.Height, (int)((rect.Y + rect.Height) * scale) + offsetY);
        if (x1 - x0 < 2 || y1 - y0 < 2)
            return;

        var block = Math.Max(4, (int)(a.Size * scale));
        var data = bitmap.LockBits(new Rectangle(x0, y0, x1 - x0, y1 - y0),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
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
                    long sb = 0, sg = 0, sr = 0, sa = 0;
                    for (var yy = 0; yy < bh; yy++)
                    {
                        var row = basePtr + (by + yy) * stride + bx * 4;
                        for (var xx = 0; xx < bw; xx++)
                        {
                            sb += row[xx * 4]; sg += row[xx * 4 + 1]; sr += row[xx * 4 + 2]; sa += row[xx * 4 + 3];
                        }
                    }
                    var count = bw * bh;
                    var nb = (byte)(sb / count); var ng = (byte)(sg / count); var nr = (byte)(sr / count); var na = (byte)(sa / count);
                    for (var yy = 0; yy < bh; yy++)
                    {
                        var row = basePtr + (by + yy) * stride + bx * 4;
                        for (var xx = 0; xx < bw; xx++)
                        {
                            row[xx * 4] = nb; row[xx * 4 + 1] = ng; row[xx * 4 + 2] = nr; row[xx * 4 + 3] = na;
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

    private static (int X, int Y) ClientPoint(nint lParam)
    {
        var value = (long)lParam;
        var x = (short)(value & 0xFFFF);
        var y = (short)((value >> 16) & 0xFFFF);
        return (x, y);
    }
}
