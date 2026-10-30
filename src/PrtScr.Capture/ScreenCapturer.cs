using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PrtScr.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.Capture;

/// <summary>
/// GDI 冻屏截图：热键触发瞬间抓取整个虚拟屏幕（全部显示器），供取景器冻结显示。
/// 物理像素；不含鼠标光标（取景器会实时叠加光标位置信息）。
/// M2 将为“分窗口截屏”引入 Windows.Graphics.Capture（WGC）。
/// </summary>
public static class ScreenCapturer
{
    private const uint ROP_SRCCOPY = 0x00CC0020;
    private const uint ROP_CAPTUREBLT = 0x40000000;

    /// <summary>整个虚拟屏幕（全部显示器）的物理矩形。</summary>
    public static (int X, int Y, int Width, int Height) GetVirtualScreenRect()
    {
        return (
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));
    }

    public static (Bitmap Image, WindowRect VirtualScreen) CaptureVirtualScreen(bool includeLayered = true)
    {
        var (x, y, width, height) = GetVirtualScreenRect();

        // 先取屏幕 DC：失败时不应先建 bitmap（否则 throw 前漏一张 2560×1600 的位图）
        var hdcScreen = PInvoke.GetDC(default);
        if (hdcScreen.IsNull)
            throw new InvalidOperationException("GetDC(null) 失败，无法抓屏");

        try
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                // CAPTUREBLT：把分层窗口（桌面歌词、桌宠等 per-pixel alpha 窗口）也抓进画面。
                // 注意不能走 Graphics.CopyFromScreen——它校验枚举值、拒绝 CaptureBlt 组合
                // （InvalidEnumArgumentException，这正是上一版 F1 崩溃的元凶），必须原生 BitBlt。
                using var graphics = Graphics.FromImage(bitmap);
                var hdcDest = new HDC(graphics.GetHdc());
                try
                {
                    var rop = includeLayered
                        ? (ROP_CODE)(ROP_SRCCOPY | ROP_CAPTUREBLT)
                        : (ROP_CODE)ROP_SRCCOPY;
                    var succeeded = PInvoke.BitBlt(
                        hdcDest, 0, 0, width, height, hdcScreen, x, y, rop);
                    if (!succeeded)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            $"BitBlt 失败（{width}×{height} @ {x},{y}，includeLayered={includeLayered}）");
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(hdcDest);
                }
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }

            return (bitmap, new WindowRect(x, y, width, height));
        }
        finally
        {
            _ = PInvoke.ReleaseDC(default, hdcScreen);
        }
    }
}
