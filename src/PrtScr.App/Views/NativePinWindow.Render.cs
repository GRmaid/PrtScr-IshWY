using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using PrtScr.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Views;

/// <summary>贴图窗口：分层窗口渲染、DIB 与 GDI 资源。</summary>
public sealed unsafe partial class NativePinWindow
{
    /// <summary>交互期：GDI StretchBlt 快速缩放；停手后 GDI+ 双三次高清精修。</summary>
    private void RenderAndPresent(bool highQuality, Image? sourceOverride = null)
    {
        if (_hwnd.IsNull || _winW <= 0 || _winH <= 0)
            return;
        if (_renderedW == _winW && _renderedH == _winH && highQuality && !_needsRedrawAfterZoom)
            return;

        try
        {
            EnsureScreenDc();

            if (highQuality)
                RenderHighQuality(sourceOverride);
            else
                RenderPlaceholder();

            Present();
            _renderedW = _winW;
            _renderedH = _winH;
            _needsRedrawAfterZoom = !highQuality;
        }
        catch (Exception ex)
        {
            App.Log($"Pin 渲染失败: {ex}");
        }
    }

    private void EnsureScreenDc()
    {
        if (_screenDc == 0)
            _screenDc = Gdi.GetDC(0);
    }

    /// <summary>源图按当前不透明度预乘进常驻源 DIB（只有透明度变化时才重建）。</summary>

    private void EnsureDestDib(int width, int height)
    {
        if (_destDib != 0 && width <= _destAllocW && height <= _destAllocH)
            return;

        ReleaseDestResources();

        _destAllocW = Math.Min(8192, Math.Max(width, 256));
        _destAllocH = Math.Max(height, 256);
        _destDib = CreateDib(_destAllocW, _destAllocH, out _destBits);
        if (_destDib == 0)
            return;

        _destDc = Gdi.CreateCompatibleDC(_screenDc);
        _ = Gdi.SelectObject(_destDc, _destDib);
    }

    /// <summary>
    /// 滚轮把窗口缩回去后，DIB 仍按历史最大尺寸常驻（2560×1600 约 16MB）。
    /// 只在窗口已小于分配面积的一半时重建，避免轻微缩放就反复申请 GDI 资源。
    /// </summary>
    private void MaybeShrinkDestDib()
    {
        if (_destDib == 0 || _winW <= 0 || _winH <= 0)
            return;

        var needW = Math.Min(8192, Math.Max(_winW, 256));
        var needH = Math.Max(_winH, 256);
        if (needW >= _destAllocW && needH >= _destAllocH)
            return;

        var used = (long)_winW * _winH;
        var allocated = (long)_destAllocW * _destAllocH;
        if (used * 2 > allocated)
            return;

        App.Log($"Pin zoom: 收缩 DIB {_destAllocW}×{_destAllocH} -> {needW}×{needH}");
        ReleaseDestResources();
        EnsureDestDib(_winW, _winH);
    }

    /// <summary>
    /// 交互期占位：只画目标尺寸的半透明边框（+极淡填充），不缩放假图——
    /// 既省资源，又避免 GDI StretchBlt 不保留 Alpha 导致的“背景透明”与逐帧闪烁。
    /// </summary>
    private void RenderPlaceholder()
    {
        EnsureDestDib(_winW, _winH);
        if (_destBits == 0)
            return;

        const int Thickness = 2;
        // 预乘 alpha：边框青色 0x00C8FF（A=0x90），内部极淡白（A=0x14）
        const uint BorderPixel = 0x90007190u;
        const uint Fill = 0x14141414u;

        var stride = _destAllocW;
        var basePtr = (uint*)_destBits;

        // 逐行向量化填充（Span.Fill 走 SIMD，大窗口也不慢）
        for (var y = 0; y < _winH; y++)
            new Span<uint>(basePtr + y * stride, _winW).Fill(Fill);

        for (var y = 0; y < _winH; y++)
        {
            var row = basePtr + y * stride;
            if (y < Thickness || y >= _winH - Thickness)
            {
                new Span<uint>(row, _winW).Fill(BorderPixel);
                continue;
            }

            for (var x = 0; x < Thickness; x++)
            {
                row[x] = BorderPixel;
                row[_winW - 1 - x] = BorderPixel;
            }
        }
    }

    /// <summary>
    /// 直接从 PNG 解码绘制到常驻 DIB，不再常驻一份全分辨率 Bitmap。
    /// 贴图闲置时只保留压缩 PNG + 目标 DIB（各一张），内存从 ~70MB 降到 ~25MB。
    /// </summary>
    private void RenderHighQuality(Image? sourceOverride = null)
    {
        EnsureScreenDc();
        EnsureDestDib(_winW, _winH);
        if (_destBits == 0)
        {
            RenderPlaceholder();
            return;
        }

        using var decoded = sourceOverride is null ? DecodeSource() : null;
        var image = sourceOverride ?? decoded?.Image;
        if (image is null)
        {
            RenderPlaceholder();
            return;
        }

        using var bitmap = CreateDestBitmap();
        if (bitmap is null)
            return;

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;

            if (_opacity >= 0.999)
            {
                // 不透明度 100% 是最常见路径：SourceCopy 不做逐像素混合，也不建 ColorMatrix 临时面，
                // 滚动缩放时的瞬时分配明显少于 SourceOver + ImageAttributes。
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(image, new Rectangle(0, 0, _winW, _winH),
                    0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
            }
            else
            {
                using var attributes = new ImageAttributes();
                graphics.CompositingMode = CompositingMode.SourceOver;
                var matrix = new ColorMatrix { Matrix33 = (float)_opacity };
                attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                graphics.DrawImage(image, new Rectangle(0, 0, _winW, _winH),
                    0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }

            DrawPinOverlays(graphics);
        }
    }

    /// <summary>锁定 / 点击穿透徽标、切换截图索引提示、管理器聚焦时的闪烁边框。</summary>
    private void DrawPinOverlays(Graphics graphics)
    {
        var scale = _hwnd.IsNull ? 1.0 : Math.Max(1.0, PInvoke.GetDpiForWindow(_hwnd) / 96.0);
        var size = 22 * scale;
        var gap = 5 * scale;
        var x = 8 * scale;
        var y = 8 * scale;

        using var badgeBrush = new SolidBrush(Color.FromArgb(0xCC, 0x1E, 0x1E, 0x20));
        using var badgeText = new SolidBrush(Color.White);
        using var badgeFont = new Font("Microsoft YaHei UI", 10 * (float)scale, FontStyle.Bold, GraphicsUnit.Pixel);
        if (_locked)
        {
            var rect = new RectangleF((float)x, (float)y, (float)size, (float)size);
            FillRoundedRectangle(graphics, badgeBrush, rect, 4 * (float)scale);
            DrawCenteredText(graphics, "锁", badgeFont, badgeText, rect);
            x += size + gap;
        }
        if (_clickThrough)
        {
            var rect = new RectangleF((float)x, (float)y, (float)size, (float)size);
            FillRoundedRectangle(graphics, badgeBrush, rect, 4 * (float)scale);
            DrawCenteredText(graphics, "穿", badgeFont, badgeText, rect);
        }

        if (_flashUntil > Environment.TickCount64)
        {
            using var pen = new Pen(Color.FromArgb(0xFF, 0x00, 0xC8, 0xFF), 3 * (float)scale);
            DrawRoundedBorder(graphics, pen, new RectangleF(1.5f, 1.5f, _winW - 3f, _winH - 3f), 4 * (float)scale);
        }

        if (_navHint is not null && _navHintUntil > Environment.TickCount64)
            DrawNavHintChip(graphics, scale);
    }

    /// <summary>底部提示条（导航索引 / 出口失败）。标注态贴在图片底边之上，避免被工具栏挡住。</summary>
    private void DrawNavHintChip(Graphics graphics, double scale)
    {
        if (_navHint is null)
            return;

        using var chipBrush = new SolidBrush(Color.FromArgb(0xD8, 0x1E, 0x1E, 0x20));
        using var chipText = new SolidBrush(Color.White);
        using var chipFont = new Font("Microsoft YaHei UI", 11 * (float)scale, FontStyle.Regular, GraphicsUnit.Pixel);
        var measured = graphics.MeasureString(_navHint, chipFont);
        var chipW = Math.Max(150 * (float)scale, measured.Width + 26 * (float)scale);
        var chipH = 28 * (float)scale;
        var bottom = _editMode ? _editOffsetY + _imgH : _winH;
        var chip = new RectangleF(
            (float)(_winW - chipW) / 2f,
            (float)(bottom - chipH) - 14 * (float)scale,
            chipW, chipH);
        FillRoundedRectangle(graphics, chipBrush, chip, 6 * (float)scale);
        DrawCenteredText(graphics, _navHint, chipFont, chipText, chip);
    }

    /// <summary>按需解码 PNG，不再复制一份全分辨率位图（省约 16 MB 瞬时占用）。调用方负责 Dispose。</summary>
    private DecodedSource? DecodeSource()
    {
        MemoryStream? stream = null;
        try
        {
            stream = new MemoryStream(_pngBytes, writable: false);
            var image = Image.FromStream(stream);
            return new DecodedSource(image, stream);
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            App.Log($"Pin 解码源图失败: {ex}");
            return null;
        }
    }

    /// <summary>把常驻 DIB 包装成 Bitmap 直接绘制，避免每帧再分配一张全窗口位图。</summary>
    private Bitmap? CreateDestBitmap()
    {
        EnsureScreenDc();
        EnsureDestDib(_winW, _winH);
        if (_destBits == 0 || _destAllocW <= 0 || _destAllocH <= 0)
            return null;

        return new Bitmap(_winW, _winH, _destAllocW * 4, PixelFormat.Format32bppPArgb, _destBits);
    }

    private void Present()
    {
        if (_hwnd.IsNull || _destDc == 0)
            return;

        var dst = new NativePoint { x = _winX, y = _winY };
        var size = new NativeSize { cx = _winW, cy = _winH };
        var src = new NativePoint();
        var blend = new BlendFunction
        {
            BlendOp = 0,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1,
        };

        _ = UpdateLayeredWindow(
            (nint)_hwnd.Value, _screenDc, ref dst, ref size, _destDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
    }

    private nint CreateDib(int width, int height, out nint bits)
    {
        var info = new BitmapInfo
        {
            bmiHeader = new BitmapInfoHeader
            {
                biSize = Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height,   // 负数 = top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,    // BI_RGB
            },
        };
        return Gdi.CreateDIBSection(_screenDc, ref info, 0, out bits, 0, 0);
    }


    private void ReleaseDestResources()
    {
        if (_destDc != 0) _ = Gdi.DeleteDC(_destDc);
        if (_destDib != 0) _ = Gdi.DeleteObject(_destDib);
        _destDc = 0;
        _destDib = 0;
        _destBits = 0;
    }

    private void ReleaseGdiResources()
    {
        ReleaseDestResources();
        if (_screenDc != 0) _ = Gdi.ReleaseDC(0, _screenDc);
        _screenDc = 0;
    }
}
