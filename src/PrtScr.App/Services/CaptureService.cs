using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PrtScr.Capture;
using PrtScr.Interop;
using PrtScr.App.Views;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace PrtScr.App.Services;

/// <summary>
/// 截图会话协调者：热键 → 冻结全屏 → 打开取景覆盖窗 →（窗口内交互）→ 剪贴板。
/// v1 一次只取鼠标所在的那块显示器（微信同款行为）；跨显示器选区在 M7 完善。
/// </summary>
public sealed class CaptureService
{
    public static CaptureService Instance { get; } = new();

    private CaptureOverlayWindow? _overlay;
    private bool _isCapturing;
    private nint _currentMonitor;

    private CaptureService()
    {
    }

    public bool IsCapturing => _isCapturing;

    /// <summary>由 MainWindow 注入：用于取景期间接管全局 Esc。</summary>
    public HotkeyService? Hotkey { get; set; }

    /// <summary>启动时预创建取景窗（隐藏），让第一次 F1 也走复用路径。</summary>
    public void EnsureOverlay()
    {
        if (_overlay is not null)
            return;

        try
        {
            _overlay = new CaptureOverlayWindow();
            _overlay.Dismissed += () =>
            {
                _isCapturing = false;
                Hotkey?.EndCaptureMode();
                if (_currentMonitor != 0)
                    WgcWindowCapture.NotifyOverlayClosed(_currentMonitor);
            };
            _overlay.WarmUpHidden();
            App.Log("Capture: overlay pre-created");
        }
        catch (Exception ex)
        {
            App.Log($"Capture: 预创建取景窗失败 {ex}");
        }
    }

    /// <summary>在 UI 线程调用。若已在截图中，热键视为“取消”。</summary>
    public async Task StartCaptureAsync()
    {
        App.Log("Capture: start");
        if (_isCapturing)
        {
            App.Log("Capture: already capturing, cancel");
            _overlay?.Cancel();
            return;
        }

        try
        {
            // 冻结画面走常驻 WGC 池：零会话启停（根治按下瞬间的全屏爆闪），
            // 只需从池里舀最新一帧，~10ms；失败回退 BitBlt。
            var (hMonitor, monitor) = GetCursorMonitor();
            if (monitor.Width <= 0 || monitor.Height <= 0)
            {
                App.Log("Capture: monitor rect invalid, abort");
                return;
            }

            var pool = WgcWindowCapture.EnsureMonitorPool(hMonitor);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (frozen, frozenBlack) = await CaptureMonitorBestAsync(hMonitor, monitor);
            if (frozen is null)
            {
                App.Log("Capture: 所有采集引擎均失败");
                return;
            }
            App.Log($"Capture: frozen {sw.ElapsedMilliseconds}ms pool={pool} black={frozenBlack}");
            _currentMonitor = hMonitor;

            // 窗口枚举不碰 UI，与抓屏并行跑，别占用亮窗前的关键路径
            var windows = await Task.Run(() => WindowEnumerator.EnumerateVisibleWindows());

            EnsureOverlay();
            if (_overlay is null)
            {
                frozen.Dispose();
                return;
            }

            _isCapturing = true;
            _overlay.Prepare(frozen, monitor, windows);
            Hotkey?.BeginCaptureMode();
            App.Log("Capture: overlay shown");
        }
        catch (Exception ex)
        {
            _isCapturing = false;
            App.Log($"Capture: 异常 {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }

    /// <summary>
    /// 显示器采集：WGC 常驻池优先；若拿到疑似黑屏（游戏全屏常见）→ 回退 BitBlt。
    /// </summary>
    private static async Task<(Bitmap? Bitmap, bool Black)> CaptureMonitorBestAsync(nint hMonitor, WindowRect monitor)
    {
        Bitmap? wgc = null;
        try
        {
            wgc = await WgcWindowCapture.GetLatestMonitorFrameAsync(hMonitor);
        }
        catch (Exception ex)
        {
            App.Log($"Capture: WGC 抛异常 {ex.GetType().Name}: {ex.Message}");
        }

        if (wgc is not null)
        {
            if (!IsNearlyBlack(wgc))
            {
                App.Log($"Capture: engine=WGC {wgc.Width}×{wgc.Height}");
                return (wgc, false);
            }

            App.Log("Capture: WGC 帧疑似黑屏，回退 DXGI");
            wgc.Dispose();
        }

        // 第二引擎：DXGI Desktop Duplication（微信 / Snipping Tool 同源，全屏游戏更可靠）
        var dxgi = await Task.Run(() => DesktopDuplicationCapture.Capture(hMonitor));
        if (dxgi is not null)
        {
            if (!IsNearlyBlack(dxgi))
            {
                App.Log($"Capture: engine=DXGI {dxgi.Width}×{dxgi.Height}");
                return (dxgi, false);
            }

            App.Log("Capture: DXGI 帧疑似黑屏，回退 BitBlt");
            dxgi.Dispose();
        }

        var fallback = CaptureMonitorByBitBlt(monitor);
        if (IsNearlyBlack(fallback))
        {
            // 部分全屏游戏对 CAPTUREBLT 返回黑屏，普通 BitBlt 反而能拿到画面
            App.Log("Capture: CAPTUREBLT 黑屏，尝试普通 BitBlt");
            fallback.Dispose();
            fallback = CaptureMonitorByBitBlt(monitor, includeLayered: false);
        }

        if (!IsNearlyBlack(fallback))
        {
            App.Log($"Capture: engine=BitBlt {fallback.Width}×{fallback.Height}");
            return (fallback, false);
        }

        // 最后一招：全屏游戏通常是一个"窗口"，用 WGC 窗口捕获（OBS 游戏/窗口捕获同路线）
        var foregroundResult = await CaptureForegroundWindowAsync(monitor);
        if (foregroundResult is not null)
            return (foregroundResult, IsNearlyBlack(foregroundResult));

        return (fallback, true);
    }

    private static async Task<Bitmap?> CaptureForegroundWindowAsync(WindowRect monitor)
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == 0 || !GetWindowRect(hwnd, out var rect))
                return null;

            // 只接受几乎铺满当前显示器的窗口（全屏游戏/全屏窗口）
            var width = rect.right - rect.left;
            var height = rect.bottom - rect.top;
            if (Math.Abs(width - monitor.Width) > 8 || Math.Abs(height - monitor.Height) > 8)
                return null;
            if (Math.Abs(rect.left - monitor.X) > 8 || Math.Abs(rect.top - monitor.Y) > 8)
                return null;

            var info = new WindowInfo(
                hwnd, "", "", "", 0,
                new WindowRect(rect.left, rect.top, width, height),
                false, false, false, false, false);

            var bitmap = await WindowCapture.CaptureAsync(info);
            if (bitmap is not null)
                App.Log($"Capture: 前景窗口捕获 {bitmap.Width}×{bitmap.Height}");

            return bitmap;
        }
        catch (Exception ex)
        {
            App.Log($"Capture: 前景窗口兜底失败 {ex.Message}");
            return null;
        }
    }

    /// <summary>BitBlt 兜底：把虚拟屏截图裁出显示器区域（显示器本地坐标）。</summary>
    private static Bitmap CaptureMonitorByBitBlt(WindowRect monitor, bool includeLayered = true)
    {
        var (full, _) = ScreenCapturer.CaptureVirtualScreen(includeLayered);
        try
        {
            return full.Clone(
                new Rectangle(monitor.X, monitor.Y, monitor.Width, monitor.Height),
                PixelFormat.Format32bppArgb);
        }
        finally
        {
            full.Dispose();
        }
    }

    /// <summary>
    /// 一键全屏快照：抓光标所在显示器（WGC 常驻池优先，BitBlt 兜底），返回 PNG 字节。
    /// 不打开取景器、不中断用户当前操作。
    /// </summary>
    public sealed record QuickCaptureResult(byte[]? Png, bool LikelyBlack);

    public static async Task<QuickCaptureResult> CaptureFullScreenPngAsync()
    {
        try
        {
            var (hMonitor, monitor) = GetCursorMonitor();
            if (monitor.Width <= 0 || monitor.Height <= 0)
                return new QuickCaptureResult(null, false);

            WgcWindowCapture.EnsureMonitorPool(hMonitor);
            var (bitmap, likelyBlack) = await CaptureMonitorBestAsync(hMonitor, monitor);
            if (bitmap is null)
                return new QuickCaptureResult(null, false);

            try
            {
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                App.Log($"QuickCapture: {bitmap.Width}×{bitmap.Height} black={likelyBlack}");
                return new QuickCaptureResult(ms.ToArray(), likelyBlack);
            }
            finally
            {
                bitmap.Dispose();
            }
        }
        catch (Exception ex)
        {
            App.Log($"QuickCapture 失败: {ex}");
            return new QuickCaptureResult(null, false);
        }
    }

    /// <summary>采样判断是否几乎全黑（独占全屏游戏被系统屏蔽时的典型结果）。</summary>
    private static bool IsNearlyBlack(Bitmap bitmap)
    {
        var stepX = Math.Max(1, bitmap.Width / 16);
        var stepY = Math.Max(1, bitmap.Height / 16);
        var total = 0;
        var bright = 0;

        for (var y = stepY / 2; y < bitmap.Height; y += stepY)
        {
            for (var x = stepX / 2; x < bitmap.Width; x += stepX)
            {
                var pixel = bitmap.GetPixel(x, y);
                total++;
                if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) > 24)
                    bright++;
            }
        }

        return total > 0 && bright * 100 / total < 2;   // 亮像素不足 2% 视为黑屏
    }

    /// <summary>取消当前取景（全局 Esc 入口）。</summary>
    public void CancelCurrent() => _overlay?.Cancel();
    /// <summary>
    /// 启动预热：第一次真实截图不再为 JIT 编译和 GDI/DWM 首次调用买单
    /// （主人实测：刚打开的前几次 F1 有闪屏和焦点失灵，全热之后消失）。
    /// </summary>
    public static void WarmUpNative()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var _ = ScreenCapturer.CaptureVirtualScreen().Image;
            }
            catch
            {
            }

            try
            {
                _ = WindowEnumerator.EnumerateVisibleWindows();
            }
            catch
            {
            }

            // 预热：直接启动常驻捕获池（会话启动的一次性合成器切换发生在应用启动时，
            // 之后每次 F1 都不再启停会话——爆闪根治）
            try
            {
                if (GetCursorPos(out var origin))
                {
                    var hMonitor = MonitorFromPoint(origin, 1);
                    if (!hMonitor.IsNull)
                        WgcWindowCapture.EnsureMonitorPool(hMonitor);
                }
            }
            catch
            {
            }
        });
    }

    // GetCursorPos/MonitorFromPoint 的 POINT 结构在 CsWin32 生成中缺失，自备一份等价声明
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern BOOL GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern HMONITOR MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    /// <summary>鼠标所在显示器的物理矩形（显示器本地坐标系的原点即虚拟屏坐标）。</summary>
    private static (HMONITOR Monitor, WindowRect Rect) GetCursorMonitor()
    {
        if (!GetCursorPos(out var pt))
            return default;

        var hMonitor = MonitorFromPoint(pt, 1 /* MONITOR_DEFAULTTONEAREST */);
        if (hMonitor.IsNull)
            return default;

        var info = new MONITORINFO
        {
            cbSize = (uint)Marshal.SizeOf<MONITORINFO>(),
        };
        unsafe
        {
            if (!PInvoke.GetMonitorInfo(hMonitor, &info))
                return default;
        }

        var rect = new WindowRect(
            info.rcMonitor.left, info.rcMonitor.top,
            info.rcMonitor.right - info.rcMonitor.left,
            info.rcMonitor.bottom - info.rcMonitor.top);
        return (hMonitor, rect);
    }
}
