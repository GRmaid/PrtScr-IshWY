using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Services;

/// <summary>
/// 全局低级键盘钩子（独立线程 + 独立消息泵），只负责全屏截图组合键。
/// 贴图 Esc 关闭已改为 <see cref="Views.NativePinWindow"/> 自己轮询，不再依赖本钩子。
///
/// 可靠性要点（2026-10-19 实测）：
/// 1) 回调里绝不写日志文件。低级钩子回调超过 LowLevelHooksTimeout 时系统会跳过这次按键，
///    连续超时还会把钩子静默摘掉——一次 2560×1600 截图期间就可能发生。日志改为无锁队列 +
///    专用写线程，回调只做记账和投递。
/// 2) RegisterHotKey 兜底通道先命中，说明钩子漏了这次按键：请求钩子线程重装，自愈。
/// 3) 三个热键事件一律经 <see cref="RaiseDeferred"/> 投到线程池，订阅者永远不在钩子回调线程上执行。
/// </summary>
internal static unsafe class EscOverPinHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const uint WM_APP_REINSTALL_HOOK = 0x8000 + 21;

    private static bool _ctrlDown;
    private static bool _altDown;
    private static bool _shiftDown;
    private static bool _winDown;

    private static int _fullScreenVk = 0x71;   // F2
    private static int _fullScreenMods;
    private static int _screenshotVk = 0x70;   // F1
    private static int _screenshotMods;
    private static int _modeVk = 0x43;         // C
    private static int _modeMods = 5;          // Alt + Shift
    private static long _lastFullScreenTick;
    private static long _lastScreenshotTick;
    private static long _lastModeTick;
    private static long _lastDiagTick;
    private static long _lastReinstallTick;
    private const int FullScreenDebounceMs = 300;

    private static nint _hook;
    private static uint _hookThreadId;
    private static int _hookManagedThreadId;
    private static Thread? _hookThread;
    private static readonly object InstallLock = new();
    private static readonly ManualResetEventSlim InstallDone = new(false);

    // 取景窗可见期间的键盘直通：窗口不一定拿得到前台焦点，靠消息层拦截会偶发漏键。
    // 钩子回调只把按键交给 UI 线程（TryEnqueue），不在这里做任何 UI/写盘工作。
    private static Action<int, bool>? _overlayKeyHandler;
    private static int _overlayKeysActive; // 0=关闭，1=取景器完整按键，2=只保留 Esc
    private static int _windowPrevVk = 0xDB; // [
    private static int _windowPrevMods;
    private static int _windowNextVk = 0xDD; // ]
    private static int _windowNextMods;

    // 钩子回调里只入队，写盘交给专用线程：回调必须在 LowLevelHooksTimeout 内返回。
    private static readonly ConcurrentQueue<string> PendingLogs = new();
    private static readonly AutoResetEvent LogSignal = new(false);
    private static Thread? _logThread;

    /// <summary>全局键盘钩子捕获到全屏截图热键（经 RaiseDeferred 投递到线程池，订阅方自行切线程）。</summary>
    public static event Action? FullScreenPressed;

    /// <summary>主截图热键的钩子通道（游戏吞键时 RegisterHotKey 收不到）；同样经 RaiseDeferred 投递。</summary>
    public static event Action? ScreenshotPressed;

    /// <summary>切换截图模式热键的钩子通道；同样经 RaiseDeferred 投递。</summary>
    public static event Action? ModeSwitchPressed;

    public static void SetFullScreenKey(int virtualKey, int modifiers = 0)
    {
        _fullScreenVk = virtualKey;
        _fullScreenMods = modifiers;
    }

    public static void SetScreenshotKey(int virtualKey, int modifiers = 0)
    {
        _screenshotVk = virtualKey;
        _screenshotMods = modifiers;
    }

    public static void SetModeSwitchKey(int virtualKey, int modifiers = 0)
    {
        _modeVk = virtualKey;
        _modeMods = modifiers;
    }

    /// <summary>
    /// 取景窗的键盘直达入口。回调在钩子线程上触发，实现方必须立即切回 UI 线程，
    /// 不得在这里等待、写文件或做布局。
    /// </summary>
    public static void SetOverlayKeyHandler(Action<int, bool>? handler)
        => Volatile.Write(ref _overlayKeyHandler, handler);

    /// <summary>只有取景窗真正亮着时才吞 Tab/Esc/Enter/Ctrl+C/Ctrl+S，隐藏后立即放行。</summary>
    public static void SetOverlayKeysActive(bool active)
        => Volatile.Write(ref _overlayKeysActive, active ? 1 : 0);

    /// <summary>
    /// 取景窗失焦（例如鼠标点了别的窗口）时只保留 Esc：仍可一键退出取景，
    /// 其余 Tab/Ctrl+C 等必须交还前台程序；取景窗重新激活后恢复完整按键。
    /// </summary>
    public static void SetOverlayKeysEscOnly(bool escOnly)
        => Volatile.Write(ref _overlayKeysActive, escOnly ? 2 : 0);

    /// <summary>取景器内上一个/下一个窗口的可配置按键（默认 [ 和 ]）。</summary>
    public static void SetWindowSwitchKeys(int prevVk, int prevMods, int nextVk, int nextMods)
    {
        _windowPrevVk = prevVk;
        _windowPrevMods = prevMods;
        _windowNextVk = nextVk;
        _windowNextMods = nextMods;
    }

    /// <summary>
    /// 全屏热键去抖/去重：钩子路径与 RegisterHotKey 兜底路径共用，
    /// 同一物理按键只允许触发一次（300ms 内视为同一次）。
    /// </summary>
    public static bool TryClaimFullScreenHotkey() => TryClaim(ref _lastFullScreenTick);

    /// <summary>主截图热键去重：钩子与 RegisterHotKey 兜底只允许一次生效。</summary>
    public static bool TryClaimScreenshotHotkey() => TryClaim(ref _lastScreenshotTick);

    /// <summary>切换截图模式热键去重。</summary>
    public static bool TryClaimModeSwitchHotkey() => TryClaim(ref _lastModeTick);

    private static bool TryClaim(ref long tick)
    {
        var now = Environment.TickCount64;
        while (true)
        {
            var last = Interlocked.Read(ref tick);
            if (now - last < FullScreenDebounceMs)
                return false;
            if (Interlocked.CompareExchange(ref tick, now, last) == last)
                return true;
        }
    }

    /// <summary>
    /// RegisterHotKey 兜底通道抢到了这次按键 —— 正常顺序下钩子总是先于 WM_HOTKEY 命中，
    /// 因此这意味着钩子漏键（被系统跳过或已摘除）。请求钩子线程重装，避免组合键永久退化到兜底。
    /// </summary>
    public static void NotifyFallbackHit()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastReinstallTick);
        if (now - last < 2000)
            return;
        if (Interlocked.CompareExchange(ref _lastReinstallTick, now, last) != last)
            return;

        var tid = _hookThreadId;
        if (tid == 0)
            return;

        if (PostThreadMessage(tid, WM_APP_REINSTALL_HOOK, 0, 0))
            HookLog("Hook: 兜底通道先命中，请求重装键盘钩子");
    }

    /// <summary>
    /// 取景窗每次亮起时重建一次低级键盘钩子：钩子可能被系统静默跳过/摘除，
    /// 不重建的话 Esc 只能等 XAML 焦点或 RegisterHotKey 兜底。重建是安静操作。
    /// </summary>
    public static void EnsureOverlayHook()
    {
        if (_hookThread is not { IsAlive: true })
        {
            Install();
            return;
        }

        var tid = _hookThreadId;
        if (tid != 0)
            _ = PostThreadMessage(tid, WM_APP_REINSTALL_HOOK, 1, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, nint lpfn, nint hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    /// <summary>启动钩子专用线程（幂等）。UI 线程只等安装完成，不承担回调。</summary>
    public static void Install()
    {
        StartLogWriter();

        lock (InstallLock)
        {
            if (_hookThread is { IsAlive: true })
                return;

            InstallDone.Reset();
            var thread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "PrtScr.LLHook",
                Priority = ThreadPriority.AboveNormal,
            };
            thread.SetApartmentState(ApartmentState.STA);
            _hookThread = thread;
            thread.Start();
        }

        // 安装是毫秒级；万一卡住也不拖死启动流程
        _ = InstallDone.Wait(2000);
    }

    private static void StartLogWriter()
    {
        lock (InstallLock)
        {
            if (_logThread is { IsAlive: true })
                return;

            _logThread = new Thread(() =>
            {
                while (true)
                {
                    LogSignal.WaitOne();
                    while (PendingLogs.TryDequeue(out var line))
                        App.Log(line);
                }
            })
            {
                IsBackground = true,
                Name = "PrtScr.HookLog",
                Priority = ThreadPriority.BelowNormal,
            };
            _logThread.Start();
        }
    }

    /// <summary>当前线程是否为低级键盘钩子线程：该线程上不允许写盘、抢锁或等 UI。</summary>
    internal static bool IsHookThread =>
        Volatile.Read(ref _hookManagedThreadId) == Environment.CurrentManagedThreadId;

    /// <summary>给 <see cref="App.Log"/> 在钩子线程上兜底：只入无锁队列，由 HookLog 线程落盘。</summary>
    internal static void LogDeferred(string line) => HookLog(line);

    /// <summary>
    /// 把热键事件投递到线程池：低级钩子回调只做判定与投递，订阅者的日志/UI 调度都不占回调时间。
    /// </summary>
    private static void RaiseDeferred(Action? handler)
    {
        if (handler is null)
            return;

        ThreadPool.QueueUserWorkItem(static state =>
        {
            try
            {
                ((Action)state!).Invoke();
            }
            catch (Exception ex)
            {
                // 已在普通线程池线程上，可以安全入日志队列；绝不能把异常带回钩子线程
                HookLog($"Hook: 热键订阅者异常 {ex.GetType().Name}: {ex.Message}");
            }
        }, handler);
    }

    private static void HookLog(string line)
    {
        // 极端情况下宁可丢日志，也不能让回调阻塞在写盘/锁上
        if (PendingLogs.Count > 128)
            return;

        PendingLogs.Enqueue(line);
        LogSignal.Set();
    }

    private static void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        _hookManagedThreadId = Environment.CurrentManagedThreadId;
        _hook = InstallHookRaw();

        HookLog($"Hook: install esc-over-pin = {_hook != 0} (tid={Environment.CurrentManagedThreadId})");
        InstallDone.Set();

        if (_hook == 0)
            return;

        // 低级钩子的回调靠本线程的消息泵投递；这里只泵消息，不做任何业务。
        while (PInvoke.GetMessage(out var msg, default, 0, 0).Value > 0)
        {
            if (msg.hwnd.IsNull && msg.message == WM_APP_REINSTALL_HOOK)
            {
                // wParam=1：安静重装（取景器每次亮起的自愈），不刷日志
                ReinstallHook(verbose: msg.wParam == 0);
                continue;
            }

            _ = PInvoke.TranslateMessage(msg);
            _ = PInvoke.DispatchMessage(msg);
        }
    }

    private static nint InstallHookRaw() => SetWindowsHookEx(
        WH_KEYBOARD_LL,
        (nint)(delegate* unmanaged[Stdcall]<int, nint, nint, nint>)&HookProc,
        PInvoke.GetModuleHandle(default).DangerousGetHandle(),
        0);

    private static void ReinstallHook(bool verbose = true)
    {
        if (_hook != 0)
        {
            _ = UnhookWindowsHookEx(_hook);
            _hook = 0;
        }

        _hook = InstallHookRaw();

        // 钩子线程可能已经被系统判超时；重装后继续泵消息
        if (verbose || _hook == 0)
            HookLog($"Hook: 重装 esc-over-pin = {_hook != 0}");
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void TrackModifier(uint vk, bool down)
    {
        switch (vk)
        {
            case 0x11: case 0xA2: case 0xA3: _ctrlDown = down; break;   // Ctrl / L / R
            case 0x12: case 0xA4: case 0xA5: _altDown = down; break;    // Alt / L / R
            case 0x10: case 0xA0: case 0xA1: _shiftDown = down; break;  // Shift / L / R
            case 0x5B: case 0x5C: _winDown = down; break;               // Win L / R
        }
    }

    /// <summary>
    /// 要求的修饰键必须都按住；**允许多按**（例如 Win 键状态卡住、用户顺手带 Alt），
    /// 精确相等会让一次卡键永久废掉热键。
    /// </summary>
    private static bool ModifiersDown(int mods)
    {
        var ctrl = _ctrlDown || Down(0x11);
        var alt = _altDown || Down(0x12);
        var shift = _shiftDown || Down(0x10);
        var win = _winDown || Down(0x5B) || Down(0x5C);

        return ((mods & 2) == 0 || ctrl)
            && ((mods & 1) == 0 || alt)
            && ((mods & 4) == 0 || shift)
            && ((mods & 8) == 0 || win);
    }

    private static bool RequiredModifiersDown() => ModifiersDown(_fullScreenMods);

    private static string DescribeModifiers()
    {
        var ctrl = _ctrlDown || Down(0x11);
        var alt = _altDown || Down(0x12);
        var shift = _shiftDown || Down(0x10);
        var win = _winDown || Down(0x5B) || Down(0x5C);
        return $"ctrl={ctrl} shift={shift} alt={alt} win={win}";
    }

    /// <summary>
    /// 取景窗按键白名单。返回 true 表示这次按键已被取景窗消费，必须阻止它继续下发，
    /// 否则底下的 Chrome/编辑器还会收到同一个 Tab/Ctrl+C。
    /// </summary>
    private static bool TryHandleOverlayKey(int vk)
    {
        var handler = Volatile.Read(ref _overlayKeyHandler);
        var mode = Volatile.Read(ref _overlayKeysActive);
        if (handler is null || mode == 0)
            return false;

        var ctrl = _ctrlDown || Down(0x11);
        var alt = _altDown || Down(0x12);
        var shift = _shiftDown || Down(0x10);
        var win = _winDown || Down(0x5B) || Down(0x5C);

        // Alt+Tab / Win+Tab / Ctrl+Tab 等系统组合键必须放行，否则取景时会切不走窗口。
        if (alt || win)
            return false;

        // 失焦兜底：吞 Esc 与主人自选的切窗键，其余按键原样交给当前前台程序。
        // 取景窗全屏常驻，即使被别的窗口抢了前台，切窗键也应该继续生效。
        if (mode == 2)
        {
            if (MatchesSwitchKey(_windowPrevVk, _windowPrevMods, vk, ctrl, shift))
                return DispatchOverlayKey(handler, 0x1F0, false);
            if (MatchesSwitchKey(_windowNextVk, _windowNextMods, vk, ctrl, shift))
                return DispatchOverlayKey(handler, 0x1F1, false);
            if (vk != 0x1B || ctrl || shift)
                return false;
            return DispatchOverlayKey(handler, vk, false);
        }

        // 取景器内可配置的“上一个/下一个窗口”优先于固定白名单。
        if (MatchesSwitchKey(_windowPrevVk, _windowPrevMods, vk, ctrl, shift))
            return DispatchOverlayKey(handler, 0x1F0, false);
        if (MatchesSwitchKey(_windowNextVk, _windowNextMods, vk, ctrl, shift))
            return DispatchOverlayKey(handler, 0x1F1, false);

        switch (vk)
        {
            case 0x09: // Tab：只消费无修饰的 Tab，Ctrl+Tab / Shift+Tab 交给系统与焦点导航
                if (ctrl || shift)
                    return false;
                break;
            case 0x1B: // Esc
            case 0x0D: // Enter
            case 0x4F: // O：对选区做 OCR
                if (ctrl || shift)
                    return false;
                break;
            case 0x43: // Ctrl+C：有选区复制图片，无选区复制色值
            case 0x53: // Ctrl+S：选区另存为
                if (!ctrl || shift)
                    return false;
                break;
            default:
                return false;
        }

        return DispatchOverlayKey(handler, vk, ctrl);
    }

    private static bool DispatchOverlayKey(Action<int, bool> handler, int vk, bool ctrl)
    {
        try
        {
            handler(vk, ctrl);
            HookLog($"Hook: overlay key vk=0x{vk:X} ctrl={ctrl}");
            return true;
        }
        catch
        {
            // 钩子回调绝不能把异常抛回系统；消费掉这次按键也比让底层应用误收安全。
            return true;
        }
    }

    private static bool MatchesSwitchKey(int wantedVk, int mods, int vk, bool ctrl, bool shift)
    {
        if (wantedVk == 0 || vk != wantedVk)
            return false;
        // 取景器切窗键只允许 Ctrl/Shift 组合；Alt/Win 保留给系统。
        if ((mods & 1) != 0 || (mods & 8) != 0)
            return false;
        return ctrl == ((mods & 2) != 0) && shift == ((mods & 4) != 0);
    }

    private static void EvaluateScreenshotHotkey()
    {
        if (Volatile.Read(ref _overlayKeysActive) != 0)
            return;

        if (!ModifiersDown(_screenshotMods))
            return;

        if (TryClaimScreenshotHotkey())
        {
            HookLog($"Hook: screenshot key vk=0x{_screenshotVk:X} mods={_screenshotMods} pressed");
            RaiseDeferred(ScreenshotPressed);
        }
    }

    private static void EvaluateModeHotkey()
    {
        if (Volatile.Read(ref _overlayKeysActive) != 0)
            return;

        if (!ModifiersDown(_modeMods))
            return;

        if (TryClaimModeSwitchHotkey())
        {
            HookLog($"Hook: mode key vk=0x{_modeVk:X} mods={_modeMods} pressed");
            RaiseDeferred(ModeSwitchPressed);
        }
    }

    private static void EvaluateFullScreenHotkey()
    {
        if (RequiredModifiersDown())
        {
            if (TryClaimFullScreenHotkey())
            {
                HookLog($"Hook: fullscreen key vk=0x{_fullScreenVk:X} mods={_fullScreenMods} pressed");
                RaiseDeferred(FullScreenPressed);
            }

            return;
        }

        // 未命中也要留痕（限流 1s）：否则“钩子死了”和“修饰键没配对”在日志里无法区分
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastDiagTick) < 1000)
            return;

        Interlocked.Exchange(ref _lastDiagTick, now);
        HookLog($"Hook: fullscreen key vk=0x{_fullScreenVk:X} ignored, need mods={_fullScreenMods} {DescribeModifiers()}");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint HookProc(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && lParam != 0)
            {
                var data = (KbdLlHookStruct*)lParam;
                var msg = (uint)wParam;
                var isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;   // Alt 组合键走 SYSKEYDOWN
                var isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (isDown || isUp)
                {
                    // 修饰键按下/抬起都要记账：LL 钩子里 GetAsyncKeyState 对合成按键不可靠
                    TrackModifier(data->vkCode, isDown);

                    // 取景窗可见时优先走这条：窗口没拿到前台焦点也能收到 Tab/Esc/Enter/Ctrl+C。
                    if (isDown && TryHandleOverlayKey((int)data->vkCode))
                        return 1;

                    if (isDown && data->vkCode == (uint)_fullScreenVk)
                        EvaluateFullScreenHotkey();
                    if (isDown && data->vkCode == (uint)_screenshotVk)
                        EvaluateScreenshotHotkey();
                    if (isDown && data->vkCode == (uint)_modeVk)
                        EvaluateModeHotkey();
                }
            }
        }
        catch
        {
            // 钩子回调绝不能抛异常
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
