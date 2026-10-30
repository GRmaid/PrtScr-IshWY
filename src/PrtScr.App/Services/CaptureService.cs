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
    private readonly object _sessionGate = new();
    private Guid _captureSessionId;

    private CaptureService()
    {
    }

    public bool IsCapturing => _isCapturing;

    /// <summary>当前这次取景的会话身份；新截图开始后旧身份自动失效（A05）。</summary>
    public Guid CurrentCaptureSessionId
    {
        get
        {
            lock (_sessionGate)
                return _captureSessionId;
        }
    }

    /// <summary>await 之后写内容/历史/剪贴板/OCR 之前先校验：不是最新会话就丢弃（A05）。</summary>
    public bool IsCurrentCaptureSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return false;

        lock (_sessionGate)
            return _captureSessionId == sessionId;
    }

    /// <summary>取消/放弃一次取景：让该会话所有晚到结果失效（A03/A05）。</summary>
    public void InvalidateCaptureSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return;

        lock (_sessionGate)
        {
            if (_captureSessionId != sessionId)
                return;
            _captureSessionId = Guid.Empty;
        }

        App.Log($"Capture: session {sessionId:N} invalidated");
    }

    /// <summary>
    /// 取景收尾的唯一漏斗（P-04b）：清 _isCapturing、注销取景期全局 Esc、通知 WGC 池。
    /// 幂等；session 为 Guid.Empty 表示启动期失败（尚未发会话号）。不清 _captureSessionId：
    /// 确认成功后的编码/剪贴板/历史仍要用它校验晚到（A05）。
    /// </summary>
    internal void EndCaptureSession(Guid sessionId, string reason)
    {
        bool wasCapturing;
        lock (_sessionGate)
        {
            wasCapturing = _isCapturing;
            _isCapturing = false;
        }

        var sessionText = sessionId == Guid.Empty ? "<none>" : sessionId.ToString("N");
        if (!wasCapturing)
        {
            App.Log($"Capture: session {sessionText} finish ignored (not capturing, reason={reason})");
            return;
        }

        Hotkey?.EndCaptureMode();
        if (_currentMonitor != 0)
            WgcWindowCapture.NotifyOverlayClosed(_currentMonitor);
        App.Log($"Capture: session {sessionText} finished reason={reason}");
    }

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
            // 收尾统一走 CaptureOverlayWindow.FinishCaptureSession → EndCaptureSession（P-04b）；
            // Dismissed 只作为窗口层通知，不再在这里另设清零点。
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
            var activeSession = CurrentCaptureSessionId;
            var activeText = activeSession == Guid.Empty ? "<none>" : activeSession.ToString("N");
            App.Log($"Capture: already capturing, cancel session={activeText}");
            _overlay?.Cancel();
            return;
        }

        // 冻结图的 owner：交给取景窗（Prepare）之前归本方法，交接后归 overlay（A10/P-09b）
        Bitmap? frozen = null;
        var frozenHandedOff = false;

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
            var (captured, frozenBlack) = await CaptureMonitorBestAsync(hMonitor, monitor);
            frozen = captured;
            if (frozen is null)
            {
                App.Log("Capture: 所有采集引擎均失败");
                return;
            }
            DrawCursorIfEnabled(frozen, monitor);
            App.Log($"Capture: frozen {sw.ElapsedMilliseconds}ms pool={pool} black={frozenBlack}");
            _currentMonitor = hMonitor;

            // 窗口枚举不碰 UI，与抓屏并行跑，别占用亮窗前的关键路径
            var windows = await Task.Run(() => WindowEnumerator.EnumerateVisibleWindows());

            EnsureOverlay();
            if (_overlay is null)
                return;   // frozen 由 finally 释放

            // 每次真正开始取景发一个会话身份：之后所有 await 回来的写入都拿它比对（A05）
            var sessionId = Guid.NewGuid();
            _isCapturing = true;
            lock (_sessionGate)
                _captureSessionId = sessionId;
            _overlay.Prepare(frozen, monitor, windows);
            // Prepare 第一件事就是把 frozen 挂成 overlay 的 _full（CaptureOverlayWindow.xaml.cs:226-227），
            // 从这一步起释放责任归取景窗，本方法不得再 Dispose。
            frozenHandedOff = true;
            Hotkey?.BeginCaptureMode();
            App.Log($"Capture: overlay shown session={sessionId:N}");
        }
        catch (Exception ex)
        {
            // 启动期失败也走同一漏斗：清 _isCapturing 并注销可能已注册的取景期 Esc。
            EndCaptureSession(CurrentCaptureSessionId, "start-failed");
            App.Log($"Capture: 异常 {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
        finally
        {
            // 未交接给取景窗的 frozen 一律在本方法释放；已交接的由 overlay 的 Dismiss/FinishCaptureSession
            // 释放，避免与 P-04 的取消路径重复 Dispose。
            if (!frozenHandedOff)
                frozen?.Dispose();
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
        // fallback 的 owner 只在本方法内：作为返回值交出前，任何出口都不能丢下它（A10/P-09b）
        var fallbackOwned = true;
        try
        {
            if (IsNearlyBlack(fallback))
            {
                // 部分全屏游戏对 CAPTUREBLT 返回黑屏，普通 BitBlt 反而能拿到画面
                App.Log("Capture: CAPTUREBLT 黑屏，尝试普通 BitBlt");
                fallback.Dispose();
                fallbackOwned = false;
                fallback = CaptureMonitorByBitBlt(monitor, includeLayered: false);
                fallbackOwned = true;
            }

            if (!IsNearlyBlack(fallback))
            {
                App.Log($"Capture: engine=BitBlt {fallback.Width}×{fallback.Height}");
                fallbackOwned = false;
                return (fallback, false);
            }

            // 最后一招：全屏游戏通常是一个"窗口"，用 WGC 窗口捕获（OBS 游戏/窗口捕获同路线）
            var foregroundResult = await CaptureForegroundWindowAsync(monitor);
            if (foregroundResult is not null)
            {
                // 前台 WGC 帧接管：BitBlt 兜底图不再需要，由本函数 finally 释放 (A10/P-09b)
                try
                {
                    return (foregroundResult, IsNearlyBlack(foregroundResult));
                }
                catch
                {
                    foregroundResult.Dispose();
                    throw;
                }
            }

            fallbackOwned = false;
            return (fallback, true);
        }
        finally
        {
            if (fallbackOwned)
                fallback.Dispose();
        }
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
                DrawCursorIfEnabled(bitmap, monitor);
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

    /// <summary>按设置把当前鼠标指针画到冻结图上（GDI 截屏本身不含光标）。</summary>
    private static void DrawCursorIfEnabled(Bitmap bitmap, WindowRect monitor)
    {
        if (!App.Config.Current.CaptureCursor)
            return;

        try
        {
            var cursor = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref cursor) || (cursor.flags & CursorShowing) == 0 || cursor.hCursor == 0)
                return;

            var x = cursor.ptScreenPos.X - monitor.X;
            var y = cursor.ptScreenPos.Y - monitor.Y;

            // 图标原点不是热点：先减去热点偏移，箭头/文本光标/I 型光标才会落在真实位置
            if (GetIconInfo(cursor.hCursor, out var icon))
            {
                x -= icon.xHotspot;
                y -= icon.yHotspot;
                if (icon.hbmMask != 0)
                    _ = DeleteObject(icon.hbmMask);
                if (icon.hbmColor != 0)
                    _ = DeleteObject(icon.hbmColor);
            }

            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            try
            {
                _ = DrawIconEx(hdc, x, y, cursor.hCursor, 0, 0, 0, 0, DiNormal);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            App.Log($"Capture: cursor included at {x},{y}");
        }
        catch (Exception ex)
        {
            // 光标只是锦上添花，失败绝不能影响截图主体
            App.Log($"Capture: 光标合成失败 {ex.Message}");
        }
    }

    private const uint CursorShowing = 0x0001;
    private const uint DiNormal = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int cbSize;
        public uint flags;
        public nint hCursor;
        public NativePoint ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(nint hIcon, out IconInfo iconInfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(nint hdc, int x, int y, nint hIcon, int cx, int cy, uint istep, nint hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

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
