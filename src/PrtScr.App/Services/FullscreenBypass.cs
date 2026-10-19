using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace PrtScr.App.Services;

/// <summary>
/// 全屏绕过：全屏热键按下时创建一个 1×1 的透明置顶窗口并强行切前台，让独占全屏的游戏收到
/// “失去激活”从而退出独占/恢复 DWM 合成；等待 500ms 后再截屏，截完把前台还原给原窗口。
/// 这是微信截图能在全屏游戏下工作的原因（它同样先抢前台），也是本程序全屏热键的默认路径。
/// </summary>
internal sealed class FullscreenBypass : IDisposable
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint LWA_ALPHA = 2;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, [Out] char[] name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hwnd, [Out] char[] text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int cmd);

    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    private static unsafe nint HandleOf(HWND hwnd) => (nint)hwnd.Value;
    private static unsafe HWND ToHwnd(nint value) => new HWND((void*)value);

    private nint _previousForeground;
    private nint _helperHwnd;

    /// <summary>是否真的抢到了前台（抢到后 500ms 的等待才有意义）。</summary>
    public bool Activated { get; private set; }

    /// <summary>
    /// 抢前台并等待合成器恢复。
    /// <paramref name="force"/> 为 true 时不要求前景窗口“铺满显示器”——全屏热键默认走这条路：
    /// 检测满屏不可靠（部分独占全屏窗口的 GetWindowRect 不等于显示器矩形），宁可多闪一下。
    /// </summary>
    public static async Task<FullscreenBypass> BeginAsync(bool force = true)
    {
        var bypass = new FullscreenBypass();
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == 0)
            {
                App.Log("FullscreenBypass: 无前景窗口，跳过");
                return bypass;
            }

            var monitor = MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
            var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
            var hasMonitor = monitor != 0 && GetMonitorInfo(monitor, ref info);
            var hasRect = GetWindowRect(foreground, out var rect);

            var monW = hasMonitor ? info.rcMonitor.right - info.rcMonitor.left : 0;
            var monH = hasMonitor ? info.rcMonitor.bottom - info.rcMonitor.top : 0;
            var winW = hasRect ? rect.right - rect.left : 0;
            var winH = hasRect ? rect.bottom - rect.top : 0;
            var full = hasMonitor && hasRect
                       && Math.Abs(winW - monW) <= 8 && Math.Abs(winH - monH) <= 8
                       && Math.Abs(rect.left - info.rcMonitor.left) <= 8
                       && Math.Abs(rect.top - info.rcMonitor.top) <= 8;

            _ = GetWindowThreadProcessId(foreground, out var pid);
            var own = pid == (uint)Environment.ProcessId;

            App.Log($"FullscreenBypass: fg=0x{foreground:X} {DescribeWindow(foreground)} "
                    + $"rect=({rect.left},{rect.top},{winW}×{winH}) "
                    + $"monitor=({info.rcMonitor.left},{info.rcMonitor.top},{monW}×{monH}) full={full} own={own}");

            if (own)
            {
                App.Log("FullscreenBypass: 前景是本程序窗口，跳过抢前台");
                return bypass;
            }

            if (!force && !full)
            {
                App.Log("FullscreenBypass: 前景非满屏，跳过（force=false）");
                return bypass;
            }

            bypass._previousForeground = foreground;
            bypass._helperHwnd = CreateHelperWindow();
            if (bypass._helperHwnd == 0)
            {
                App.Log("FullscreenBypass: 创建辅助窗口失败");
                bypass._previousForeground = 0;
                return bypass;
            }

            ForceForeground(ToHwnd(bypass._helperHwnd));
            await Task.Delay(60);
            var now = GetForegroundWindow();
            if (now != bypass._helperHwnd)
            {
                // 前台锁偶尔会拒绝第一次 SetForegroundWindow，再试一次
                ForceForeground(ToHwnd(bypass._helperHwnd));
                await Task.Delay(60);
                now = GetForegroundWindow();
            }

            bypass.Activated = now == bypass._helperHwnd;
            App.Log($"FullscreenBypass: helper=0x{bypass._helperHwnd:X} now=0x{now:X} activated={bypass.Activated}");

            // 独占全屏退出/合成器恢复需要时间，固定等足 500ms（主人实测：320ms 不够稳定）
            await Task.Delay(500);
            return bypass;
        }
        catch (Exception ex)
        {
            App.Log($"FullscreenBypass: 失败 {ex.GetType().Name}: {ex.Message}");
            return bypass;
        }
    }

    private static unsafe nint CreateHelperWindow()
    {
        fixed (char* cls = "STATIC")
        fixed (char* title = AppInfo.ShortName)
        {
            var hwnd = PInvoke.CreateWindowEx(
                Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_TOPMOST |
                Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_TOOLWINDOW |
                Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_LAYERED,
                new PCWSTR(cls), new PCWSTR(title),
                Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_POPUP |
                Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_VISIBLE,
                0, 0, 1, 1,
                default, default, default, default);

            var value = (nint)hwnd.Value;
            if (value != 0)
            {
                // 全透明：占住前台但不污染截图（左上角不留像素）
                _ = SetLayeredWindowAttributes(value, 0, 0, LWA_ALPHA);
            }

            return value;
        }
    }

    private static void ForceForeground(HWND target)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground != 0 ? GetWindowThreadProcessId(foreground, out _) : 0;
        var targetThread = GetWindowThreadProcessId(target, out _);
        var attached = false;

        try
        {
            if (foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread)
                attached = AttachThreadInput(foregroundThread, targetThread, true);

            _ = PInvoke.SetForegroundWindow(target);
            _ = PInvoke.SetFocus(target);
        }
        finally
        {
            if (attached)
                _ = AttachThreadInput(foregroundThread, targetThread, false);
        }
    }

    private static string DescribeWindow(nint hwnd)
    {
        try
        {
            var cls = new char[128];
            var title = new char[256];
            var clsLen = GetClassNameW(hwnd, cls, cls.Length);
            var titleLen = GetWindowTextW(hwnd, title, title.Length);
            var c = clsLen > 0 ? new string(cls, 0, Math.Min(clsLen, cls.Length)) : "?";
            var t = titleLen > 0 ? new string(title, 0, Math.Min(titleLen, title.Length)) : "";
            if (t.Length > 60)
                t = t[..60];
            return string.IsNullOrEmpty(t) ? c : $"{c} \"{t}\"";
        }
        catch
        {
            return "?";
        }
    }

    public void Dispose()
    {
        try
        {
            if (_helperHwnd != 0)
                _ = PInvoke.DestroyWindow(ToHwnd(_helperHwnd));
            _helperHwnd = 0;

            if (_previousForeground != 0 && Activated)
            {
                // 抢前台会让部分独占全屏游戏最小化；SetForegroundWindow 不会取消最小化，需显式还原。
                // 只在 IsIconic 为真时还原，避免把最大化窗口「还原」成普通尺寸。
                if (IsIconic(_previousForeground))
                {
                    _ = ShowWindow(_previousForeground, SW_RESTORE);
                    App.Log("FullscreenBypass: 原前台被最小化，已 SW_RESTORE");
                }

                ForceForeground(ToHwnd(_previousForeground));
                App.Log("FullscreenBypass: 已归还原前台");
            }
        }
        catch (Exception ex)
        {
            App.Log($"FullscreenBypass: 还原失败 {ex.Message}");
        }
        finally
        {
            Activated = false;
        }
    }
}
