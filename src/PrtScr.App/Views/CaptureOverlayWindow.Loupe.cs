using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using PrtScr.App.Services;
using PrtScr.Capture;
using PrtScr.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;

namespace PrtScr.App.Views;

/// <summary>冻屏取景覆盖窗：取色放大镜。</summary>
public sealed partial class CaptureOverlayWindow
{
    // ---------- 取色放大镜 ----------

    /// <summary>
    /// 跟随鼠标更新 19×19 取色器：中心像素放大 10 倍，显示 HEX 与 RGB。
    /// 直接锁 19×19 小块再展开，避免 361 次 GetPixel；缓冲区全部复用，不产生每帧分配。
    /// </summary>
    private void UpdateColorLoupe(Windows.Foundation.Point dipPoint)
    {
        if (_closed || _hasSelection || _dragging || _scale <= 0 ||
            _full.Width < LoupeSampleSize || _full.Height < LoupeSampleSize ||
            _loupeBitmap is null)
        {
            HideColorLoupe();
            return;
        }

        try
        {
            // 用 Root 的实际 DIP 尺寸映射到冻结帧，而不是直接用 RasterizationScale：
            // 无边框窗曾有 1px 非客户区，客户区比屏幕小 2px，靠边时取色坐标会偏。
            var rootW = Root.ActualWidth > 0 ? Root.ActualWidth : _monitor.Width / _scale;
            var rootH = Root.ActualHeight > 0 ? Root.ActualHeight : _monitor.Height / _scale;
            if (rootW <= 0 || rootH <= 0)
            {
                HideColorLoupe();
                return;
            }

            var px = (int)Math.Round(dipPoint.X * _full.Width / rootW);
            var py = (int)Math.Round(dipPoint.Y * _full.Height / rootH);
            if (px < 0 || py < 0)
            {
                HideColorLoupe();
                return;
            }
            px = Math.Min(px, _full.Width - 1);
            py = Math.Min(py, _full.Height - 1);

            var left = Math.Clamp(px - LoupeSampleSize / 2, 0, _full.Width - LoupeSampleSize);
            var top = Math.Clamp(py - LoupeSampleSize / 2, 0, _full.Height - LoupeSampleSize);

            var data = _full.LockBits(
                new System.Drawing.Rectangle(left, top, LoupeSampleSize, LoupeSampleSize),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes = LoupeSampleSize * 4;
                for (var row = 0; row < LoupeSampleSize; row++)
                {
                    Marshal.Copy(
                        IntPtr.Add(data.Scan0, row * data.Stride),
                        _loupeSample,
                        row * rowBytes,
                        rowBytes);
                }
            }
            finally
            {
                _full.UnlockBits(data);
            }

            var center = (py - top) * LoupeSampleSize + (px - left);
            var centerOffset = center * 4;
            var b = _loupeSample[centerOffset];
            var g = _loupeSample[centerOffset + 1];
            var r = _loupeSample[centerOffset + 2];
            _loupeHex = $"#{r:X2}{g:X2}{b:X2}";
            _loupeRgb = $"R {r}  G {g}  B {b}";

            // 每个采样像素铺成 10×10 色块，整张 190×190
            for (var sy = 0; sy < LoupeSampleSize; sy++)
            {
                for (var sx = 0; sx < LoupeSampleSize; sx++)
                {
                    var so = (sy * LoupeSampleSize + sx) * 4;
                    var sb = _loupeSample[so];
                    var sg = _loupeSample[so + 1];
                    var sr = _loupeSample[so + 2];
                    var bx = sx * LoupeCell;
                    var by = sy * LoupeCell;
                    for (var dy = 0; dy < LoupeCell; dy++)
                    {
                        var rowBase = ((by + dy) * LoupePixelSize + bx) * 4;
                        for (var dx = 0; dx < LoupeCell; dx++)
                        {
                            var di = rowBase + dx * 4;
                            _loupePixels[di] = sb;
                            _loupePixels[di + 1] = sg;
                            _loupePixels[di + 2] = sr;
                            _loupePixels[di + 3] = 255;
                        }
                    }
                }
            }

            // 中心像素框：黑白双线，深色/浅色背景上都看得见
            var ccx = (px - left) * LoupeCell;
            var ccy = (py - top) * LoupeCell;
            for (var i = -1; i <= LoupeCell; i++)
            {
                SetLoupePixel(ccx + i, ccy - 1, 255, 255, 255);
                SetLoupePixel(ccx + i, ccy + LoupeCell, 255, 255, 255);
                SetLoupePixel(ccx - 1, ccy + i, 255, 255, 255);
                SetLoupePixel(ccx + LoupeCell, ccy + i, 255, 255, 255);
            }
            for (var i = -2; i <= LoupeCell + 1; i++)
            {
                SetLoupePixel(ccx + i, ccy - 2, 0, 0, 0);
                SetLoupePixel(ccx + i, ccy + LoupeCell + 1, 0, 0, 0);
                SetLoupePixel(ccx - 2, ccy + i, 0, 0, 0);
                SetLoupePixel(ccx + LoupeCell + 1, ccy + i, 0, 0, 0);
            }

            using (var stream = _loupeBitmap.PixelBuffer.AsStream())
            {
                stream.Position = 0;
                stream.Write(_loupePixels, 0, _loupePixels.Length);
            }
            _loupeBitmap.Invalidate();

            LoupeHex.Text = _loupeHex;
            LoupeRgb.Text = _loupeRgb;
            LoupePanel.Visibility = Visibility.Visible;

            var panelW = LoupePanel.ActualWidth > 0 ? LoupePanel.ActualWidth : 202;
            var panelH = LoupePanel.ActualHeight > 0 ? LoupePanel.ActualHeight : 256;
            // 底部 HUD 常驻，放大镜不能压住它：把可用高度收窄后再决定翻到光标上方
            var hudReserve = HudBar.Visibility == Visibility.Visible
                ? Math.Max(32, HudBar.ActualHeight) + 20
                : 0;
            var bottomLimit = rootH - hudReserve;
            var x = dipPoint.X + 18;
            if (x + panelW > rootW - 4)
                x = dipPoint.X - panelW - 18;
            var y = dipPoint.Y + 18;
            if (y + panelH > bottomLimit - 4)
                y = dipPoint.Y - panelH - 18;
            Canvas.SetLeft(LoupePanel, Math.Clamp(x, 4, Math.Max(4, rootW - panelW - 4)));
            Canvas.SetTop(LoupePanel, Math.Clamp(y, 4, Math.Max(4, bottomLimit - panelH - 4)));
            _loupeErrorLogged = false;
        }
        catch (Exception ex)
        {
            HideColorLoupe();
            if (!_loupeErrorLogged)
            {
                _loupeErrorLogged = true;
                App.Log($"Overlay 取色器异常: {ex.Message}");
            }
        }
    }

    private void HideColorLoupe()
    {
        if (LoupePanel is not null)
            LoupePanel.Visibility = Visibility.Collapsed;
    }

    private void SetLoupePixel(int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= LoupePixelSize || y >= LoupePixelSize)
            return;
        var o = (y * LoupePixelSize + x) * 4;
        _loupePixels[o] = b;
        _loupePixels[o + 1] = g;
        _loupePixels[o + 2] = r;
        _loupePixels[o + 3] = 255;
    }

    private void OnCopyColorShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_hasSelection)
        {
            // 有选区时 Ctrl+C 仍然是“复制这张截图”，和常见截图工具一致
            _ = ConfirmAsync();
            return;
        }
        _ = CopyLoupeColorAsync();
    }

    private async Task CopyLoupeColorAsync()
    {
        try
        {
            if (_loupeHex.Length == 0)
            {
                RefreshHoverFromCursor();
                if (_loupeHex.Length == 0)
                    return;
            }

            var text = _loupeHex;
            var ok = await PrtScr.App.Services.ClipboardService.SetTextAsync(text);
            App.Log(ok ? $"Overlay: color copied {text}" : $"Overlay: color copy failed {text}");
            ShowCopyToast(ok ? $"已复制 {text}" : "复制色值失败");
        }
        catch (Exception ex)
        {
            App.Log($"Overlay 复制色值异常: {ex.Message}");
        }
    }

}
