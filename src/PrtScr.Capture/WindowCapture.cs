using System.Drawing;
using System.Drawing.Imaging;
using PrtScr.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;

namespace PrtScr.Capture;

/// <summary>
/// 窗口本体捕获：PrintWindow + PW_RENDERFULLCONTENT，直接取窗口自身合成内容——
/// 抗遮挡（被其他窗口盖住的部分也完整），即“第二张图 → 第三张图”的效果。
/// M2 将升级为 Windows.Graphics.Capture（WGC）完全体。
/// </summary>
public static class WindowCapture
{
    // PW_RENDERFULLCONTENT = 2（Win 8.1+，含 DirectComposition 内容）；SDK 元数据未收录该成员
    private const PRINT_WINDOW_FLAGS PW_RENDERFULLCONTENT = (PRINT_WINDOW_FLAGS)2;
    /// <summary>
    /// 窗口本体捕获总入口：优先 PrintWindow（快、无系统边框），
    /// 失败（硬件加速应用全黑/拒绝）回退 WGC（OBS 同源）。
    /// </summary>
    public static async Task<Bitmap?> CaptureAsync(WindowInfo window, CancellationToken cancellationToken = default)
    {
        // PrintWindow 会强制目标窗口参与合成，放后台线程避免 UI 停顿
        var printed = await Task.Run(() => CaptureWindow(window), cancellationToken);
        if (printed is not null)
            return printed;

        return await WgcWindowCapture.CaptureWindowAsync(window, cancellationToken);
    }

    public static Bitmap? CaptureWindow(WindowInfo window)
    {
        var width = window.FrameBounds.Width;
        var height = window.FrameBounds.Height;
        if (width <= 0 || height <= 0)
            return null;

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var keep = false;
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = new HDC(graphics.GetHdc());
            bool ok;
            try
            {
                ok = PInvoke.PrintWindow((HWND)window.Handle, hdc, PW_RENDERFULLCONTENT);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            if (!ok)
                return null;

            // 部分硬件加速应用 PrintWindow 会返回 true 但内容全黑，采样判定后回退屏幕裁剪
            if (LooksSolidBlack(bitmap))
                return null;

            keep = true;
            return bitmap;
        }
        finally
        {
            if (!keep)
                bitmap.Dispose();
        }
    }

    /// <summary>9×9 网格采样，全部为纯黑则视为捕获失败。</summary>
    private static bool LooksSolidBlack(Bitmap bitmap)
    {
        var stepX = bitmap.Width / 10;
        var stepY = bitmap.Height / 10;
        if (stepX == 0 || stepY == 0)
            return false;

        for (var iy = 1; iy <= 9; iy++)
        {
            for (var ix = 1; ix <= 9; ix++)
            {
                var pixel = bitmap.GetPixel(stepX * ix, stepY * iy);
                if (pixel.R != 0 || pixel.G != 0 || pixel.B != 0)
                    return false;
            }
        }
        return true;
    }
}
