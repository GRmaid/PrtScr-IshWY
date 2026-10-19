using System.Diagnostics;
using PrtScr.App.Views;
using PrtScr.Ocr;

namespace PrtScr.App.Services;

/// <summary>
/// OCR 的 UI 门面：调用引擎（Windows OCR 秒出，Umi-OCR 可用时兜底）并弹出结果窗。
/// 必须在 UI 线程调用（WinRT OCR 与窗口创建都依赖）。
/// </summary>
public static class OcrUiService
{

    private static readonly List<OcrResultWindow> OpenWindows = new();

    /// <summary>最近一次 OCR 结果窗关闭时刻（用于让设置窗在 Esc 宽限期内让路）。</summary>
    public static long LastWindowClosedTick;

    public static async Task RecognizeAndShowAsync(byte[] pngBytes, nint ownerHwnd = 0)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var service = new OcrService(App.Config.Current.PreferUmiOcr, App.Config.Current.UmiOcrPort);
            var result = await service.RecognizeAsync(pngBytes);
            sw.Stop();

            var window = new OcrResultWindow(result, sw.ElapsedMilliseconds, ownerHwnd);
            OpenWindows.Add(window);
            window.Closed += (_, _) =>
            {
                LastWindowClosedTick = Environment.TickCount64;
                OpenWindows.Remove(window);
            };
            window.Activate();

            var hwnd = new Windows.Win32.Foundation.HWND(WinRT.Interop.WindowNative.GetWindowHandle(window));
            _ = Windows.Win32.PInvoke.ShowWindow(hwnd, Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_RESTORE);
            _ = Windows.Win32.PInvoke.SetForegroundWindow(hwnd);
        }
        catch (Exception ex)
        {
            App.Log($"OCR 入口失败: {ex}");
        }
    }
}
