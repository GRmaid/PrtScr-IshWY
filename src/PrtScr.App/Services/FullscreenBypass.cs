using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace PrtScr.App.Services;

/// <summary>
/// 全屏绕过：全屏热键按下时创建一个 1×1 的透明置顶窗口并强行切前台，让独占全屏的游戏收到
/// “失去激活”从而退出独占/恢复 DWM 合成；等待 500ms 后再截屏，截完把前台还原给原窗口。
/// 这是微信截图能在全屏游戏下工作的原因（它同样先抢前台），也是本程序全屏热键的默认路径。
/// 带 owner/token 的重载在每次 await 之后、创建 helper 之前，以及每次显式
/// SetForegroundWindow/SetFocus/ShowWindow 之前检查取消与会话所有权，失效即中止。
/// helper 只在创建线程销毁；异线程 Dispose 派发回创建线程的 DispatcherQueue 执行。
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

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private const int SW_RESTORE = 9;

    /// <summary>旧重载使用的恒真所有权检查：不绑定 owner 时保持原有行为。</summary>
    private static readonly Func<bool> AlwaysCurrent = () => true;

    /// <summary>
    /// 当前前台是否处于全屏游戏/全屏应用状态。独占全屏下弹取景器会触发模式切换，
    /// 导致短暂黑屏和游戏音频中断；该状态用于改走不抢焦点的全屏快照。
    /// </summary>
    public static bool IsForegroundFullscreenGame()
    {
        try
        {
            // QUNS_BUSY=2（全屏应用）、QUNS_RUNNING_D3D_FULL_SCREEN=3（D3D 独占全屏）、QUNS_PRESENTATION_MODE=4
            return SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;
        }
        catch
        {
            return false;
        }
    }

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

    private readonly Guid _ownerSessionId;
    private readonly Func<bool> _isOwnerCurrent;
    private readonly int _creationThreadId;
    private readonly DispatcherQueue _creationDispatcherQueue;
    private nint _previousForeground;
    private nint _helperHwnd;
    private int _disposeStarted;
    private volatile bool _canceled;
    private volatile bool _invalidated;

    /// <summary>是否因取消（token）而中止；服务据此区分「取消」与「失败」。</summary>
    public bool IsCanceled => _canceled;

    /// <summary>是否真的抢到了前台（抢到后 500ms 的等待才有意义）。</summary>
    public bool Activated { get; private set; }

    private FullscreenBypass(Guid ownerSessionId, Func<bool> isOwnerCurrent)
    {
        _ownerSessionId = ownerSessionId;
        _isOwnerCurrent = isOwnerCurrent;
        _creationThreadId = Environment.CurrentManagedThreadId;
        try
        {
            // 必须在创建 helper 的线程上取；取不到时异线程 Dispose 只记录残余，不做跨线程销毁。
            _creationDispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }
        catch (Exception ex)
        {
            App.Log($"FullscreenBypass: 获取创建线程 DispatcherQueue 失败 {ex.GetType().Name}: {ex.Message}");
            // 空表示无法派发回创建线程，Cleanup 按“无创建队列”处理。
            _creationDispatcherQueue = null!;
        }
    }

    /// <summary>
    /// 旧入口：不绑定 owner/token，行为与既往一致；安全清理（helper 必须在创建线程销毁）同样生效。
    /// <paramref name="force"/> 为 true 时不要求前景窗口“铺满显示器”——全屏热键默认走这条路：
    /// 检测满屏不可靠（部分独占全屏窗口的 GetWindowRect 不等于显示器矩形），宁可多闪一下。
    /// </summary>
    public static Task<FullscreenBypass> BeginAsync(bool force = true)
        => BeginAsync(Guid.Empty, AlwaysCurrent, CancellationToken.None, force);

    /// <summary>
    /// 抢前台并等待合成器恢复，带会话所有权与取消。
    /// <paramref name="ct"/> 被取消时不抛异常：返回的实例 <see cref="IsCanceled"/> 为 true，
    /// 服务据此区分「取消」与「失败」；取消只清理 helper，不归还原前台。
    /// </summary>
    public static async Task<FullscreenBypass> BeginAsync(
        Guid ownerSessionId,
        Func<bool> isOwnerCurrent,
        CancellationToken ct,
        bool force = true)
    {
        var bypass = new FullscreenBypass(ownerSessionId, isOwnerCurrent);
        try
        {
            if (!bypass.TryContinue(ct, "入口"))
                return bypass;

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

            // helper 一旦创建就必须由创建线程负责销毁，所以创建前再查一次取消与所有权。
            if (!bypass.TryContinue(ct, "创建 helper 前"))
                return bypass;

            bypass._previousForeground = foreground;
            bypass._helperHwnd = CreateHelperWindow();
            if (bypass._helperHwnd == 0)
            {
                App.Log("FullscreenBypass: 创建辅助窗口失败");
                bypass._previousForeground = 0;
                return bypass;
            }

            if (!bypass.TryForceForeground(bypass._helperHwnd, ct))
            {
                bypass.Invalidate();
                return bypass;
            }

            await Task.Delay(60, ct);

            if (!bypass.TryContinue(ct, "首次等待 60ms 后"))
            {
                bypass.Invalidate();
                return bypass;
            }

            var now = GetForegroundWindow();
            if (now != bypass._helperHwnd)
            {
                // 前台锁偶尔会拒绝第一次 SetForegroundWindow，再试一次
                if (!bypass.TryForceForeground(bypass._helperHwnd, ct))
                {
                    bypass.Invalidate();
                    return bypass;
                }

                await Task.Delay(60, ct);

                if (!bypass.TryContinue(ct, "二次等待 60ms 后"))
                {
                    bypass.Invalidate();
                    return bypass;
                }

                now = GetForegroundWindow();
            }

            bypass.Activated = now == bypass._helperHwnd;
            App.Log($"FullscreenBypass: helper=0x{bypass._helperHwnd:X} now=0x{now:X} activated={bypass.Activated}");

            // 独占全屏退出/合成器恢复需要时间，固定等足 500ms（主人实测：320ms 不够稳定）
            await Task.Delay(500, ct);

            if (!bypass.TryContinue(ct, "合成器恢复等待后"))
            {
                bypass.Invalidate();
                return bypass;
            }

            return bypass;
        }
        catch (OperationCanceledException)
        {
            bypass.MarkCanceled("等待被取消");
            bypass.Invalidate();
            return bypass;
        }
        catch (Exception ex)
        {
            App.Log($"FullscreenBypass: 失败 {ex.GetType().Name}: {ex.Message}");
            bypass.Invalidate();
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

    private static bool ForceForeground(HWND target, Func<bool> canProceed)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground != 0 ? GetWindowThreadProcessId(foreground, out _) : 0;
        var targetThread = GetWindowThreadProcessId(target, out _);
        var attached = false;

        try
        {
            if (foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread)
            {
                if (!canProceed())
                    return false;
                attached = AttachThreadInput(foregroundThread, targetThread, true);
            }

            // 每次显式焦点变更前都复查，防止取消/所有权在同步序列中途变化。
            if (!canProceed())
                return false;
            _ = PInvoke.SetForegroundWindow(target);

            if (!canProceed())
                return false;
            _ = PInvoke.SetFocus(target);
            return true;
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

    private bool TryForceForeground(nint helperHwnd, CancellationToken ct)
        => ForceForeground(ToHwnd(helperHwnd), () => TryContinue(ct, "前台切换前"));

    private bool IsOwnerStillCurrent()
    {
        try
        {
            return _isOwnerCurrent();
        }
        catch (Exception ex)
        {
            App.Log($"FullscreenBypass: owner 检查回调异常，保守失效 {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool TryContinue(CancellationToken ct, string stage)
    {
        if (ct.IsCancellationRequested)
        {
            MarkCanceled(stage);
            return false;
        }

        if (!IsOwnerStillCurrent())
        {
            _invalidated = true;
            App.Log($"FullscreenBypass: owner 已失效（{stage}）owner={_ownerSessionId}");
            return false;
        }

        return true;
    }

    private void MarkCanceled(string stage)
    {
        _canceled = true;
        App.Log($"FullscreenBypass: 已取消（{stage}）owner={_ownerSessionId}");
    }

    /// <summary>失效：只清理 helper，不归还旧前台（系统自动激活不属于本类的显式恢复）。</summary>
    private void Invalidate()
    {
        try
        {
            _invalidated = true;
            Activated = false;
            _previousForeground = 0;
            Cleanup(allowRestore: false);
        }
        catch (Exception ex)
        {
            App.Log($"FullscreenBypass: 失效清理异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Cleanup(bool allowRestore)
    {
        try
        {
            if (_helperHwnd == 0 && !Activated)
            {
                _previousForeground = 0;
                return;
            }

            if (Environment.CurrentManagedThreadId == _creationThreadId)
            {
                CleanupOnCreationThread(allowRestore, Activated);
                return;
            }

            // 异线程：绝不在这里 DestroyWindow，派发回创建线程再执行。
            DispatchCleanup(allowRestore);
        }
        catch (Exception ex)
        {
            App.Log($"FullscreenBypass: 清理失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CleanupOnCreationThread(bool allowRestore, bool wasActivated)
    {
        try
        {
            if (!DestroyHelperWindowIfAny())
            {
                App.Log($"FullscreenBypass: helper 未能销毁（残余 0x{_helperHwnd:X}），跳过前台恢复");
                return;
            }

            if (allowRestore && wasActivated && !_canceled && !_invalidated && IsOwnerStillCurrent())
                RestorePreviousForeground();
            else if (allowRestore && wasActivated)
                App.Log("FullscreenBypass: 已失效或非当前 owner，跳过归还原前台");
        }
        catch (Exception ex)
        {
            App.Log($"FullscreenBypass: 清理异常 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _previousForeground = 0;
            Activated = false;
        }
    }

    private bool DestroyHelperWindowIfAny()
    {
        var helper = _helperHwnd;
        if (helper == 0)
            return true;

        if (PInvoke.DestroyWindow(ToHwnd(helper)))
        {
            _helperHwnd = 0;
            return true;
        }

        App.Log($"FullscreenBypass: DestroyWindow 失败 err={Marshal.GetLastWin32Error()}，保留 helper=0x{helper:X}（残余）");
        return false;
    }

    private void DispatchCleanup(bool allowRestore)
    {
        var queue = _creationDispatcherQueue;
        if (queue is null)
        {
            App.Log($"FullscreenBypass: 异线程清理但创建线程无 DispatcherQueue，helper=0x{_helperHwnd:X} 保留未销毁（残余），前台恢复未执行");
            return;
        }

        var wasActivated = Activated;
        var enqueued = false;
        try
        {
            enqueued = queue.TryEnqueue(() =>
            {
                try
                {
                    CleanupOnCreationThread(allowRestore, wasActivated);
                }
                catch (Exception ex)
                {
                    App.Log($"FullscreenBypass: 派发清理异常 {ex.GetType().Name}: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            App.Log($"FullscreenBypass: 派发清理失败 {ex.GetType().Name}: {ex.Message}");
        }

        if (!enqueued)
            App.Log($"FullscreenBypass: 异线程清理派发失败，helper=0x{_helperHwnd:X} 保留未销毁（残余）");
    }

    private void RestorePreviousForeground()
    {
        if (_previousForeground == 0)
            return;

        // 抢前台会让部分独占全屏游戏最小化；SetForegroundWindow 不会取消最小化，需显式还原。
        // 只在 IsIconic 为真时还原，避免把最大化窗口「还原」成普通尺寸。
        if (IsIconic(_previousForeground) && IsOwnerStillCurrent())
        {
            _ = ShowWindow(_previousForeground, SW_RESTORE);
            App.Log("FullscreenBypass: 原前台被最小化，已 SW_RESTORE");
        }

        if (ForceForeground(ToHwnd(_previousForeground), IsOwnerStillCurrent))
            App.Log("FullscreenBypass: 已归还原前台");
        else
            App.Log("FullscreenBypass: 已失效或非当前 owner，未归还原前台");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        try
        {
            Cleanup(allowRestore: true);
        }
        catch (Exception ex)
        {
            App.Log($"FullscreenBypass: Dispose 异常 {ex.GetType().Name}: {ex.Message}");
        }
    }
}
