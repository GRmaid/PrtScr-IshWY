using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Services;

/// <summary>
/// 全局热键：独立消息循环线程 + RegisterHotKey。
/// 截图 / 全屏截图 / 从剪贴板贴图 / 从剪贴板 OCR 各占一个 id，均可注册任意组合键。
/// 窗口类名每次启动唯一化，支持 Restart 换键而不受类重复注册影响。
/// </summary>
public sealed unsafe class HotkeyService : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_ESCAPE = 0x1B;

    // WM_APP 段自定义消息：在热键线程上注册/注销
    private const uint WM_APP_REGISTER_CANCEL = 0x8001;   // WM_APP + 1
    private const uint WM_APP_UNREGISTER_CANCEL = 0x8002; // WM_APP + 2
    private const uint WM_APP_SET_FULLSCREEN = 0x8003;    // WM_APP + 3
    private const uint WM_APP_SET_PIN = 0x8004;           // WM_APP + 4
    private const uint WM_APP_SET_OCR = 0x8005;           // WM_APP + 5
    private const uint WM_APP_SET_MODE = 0x8006;          // WM_APP + 6

    private const int ScreenshotHotkeyId = 1;
    private const int CancelHotkeyId = 2;
    private const int FullScreenHotkeyId = 3;
    private const int PinHotkeyId = 4;
    private const int OcrHotkeyId = 5;
    private const int ModeHotkeyId = 6;

    /// <summary>截图（冻屏取景）热键。</summary>
    public event Action? HotkeyPressed;

    /// <summary>
    /// 全屏截图热键的 RegisterHotKey 兜底通道。
    /// 低级键盘钩子可能被系统静默摘除（见 EscOverPinHook），这条通道不依赖钩子；
    /// 两条通道同时命中时由 EscOverPinHook.TryClaimFullScreenHotkey 去重。
    /// </summary>
    public event Action? FullScreenHotkeyPressed;

    /// <summary>从剪贴板贴图热键。</summary>
    public event Action? PinHotkeyPressed;

    /// <summary>从剪贴板识别文字热键。</summary>
    public event Action? OcrHotkeyPressed;

    /// <summary>切换截图模式（常规取景 / 快捷全屏）。</summary>
    public event Action? ModeHotkeyPressed;

    /// <summary>取景期间按下全局 Esc（无视焦点，微信同款）。</summary>
    public event Action? CancelPressed;

    /// <summary>某个热键注册失败（通常是被别的程序占用），附带可直接展示的提示。</summary>
    public event Action<string>? RegistrationFailed;

    private static readonly object MapLock = new();
    private static readonly Dictionary<nint, HotkeyService> Map = new();

    private HWND _hwnd;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile int _screenshotVk;
    private volatile int _screenshotMods;
    private volatile int _fullScreenVk;
    private volatile int _fullScreenMods;
    private volatile int _pinVk;
    private volatile int _pinMods;
    private volatile int _ocrVk;
    private volatile int _ocrMods;
    private volatile int _modeVk;
    private volatile int _modeMods;

    public HotkeyService() { }

    /// <summary>启动热键线程并注册截图热键。</summary>
    public void Start(int virtualKey, int modifiers)
    {
        _screenshotVk = virtualKey;
        _screenshotMods = modifiers;
        _stop = false;

        _thread = new Thread(Run) { IsBackground = true, Name = "PrtScr.Hotkey" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>改截图热键：线程带着所有已配置热键重启。</summary>
    public void Restart(int virtualKey, int modifiers)
    {
        Dispose();
        Start(virtualKey, modifiers);
    }

    private void Run()
    {
        var className = $"PrtScrHotkey_{Guid.NewGuid():N}";
        var wndClass = new WNDCLASSW
        {
            hInstance = new HINSTANCE(PInvoke.GetModuleHandle(default).DangerousGetHandle()),
        };

        unsafe
        {
            fixed (char* classNamePtr = className)
            {
                wndClass.lpszClassName = new PCWSTR(classNamePtr);
                wndClass.lpfnWndProc = &WndProc;
                _ = PInvoke.RegisterClass(wndClass);

                _hwnd = PInvoke.CreateWindowEx(
                    0,
                    new PCWSTR(classNamePtr),
                    new PCWSTR(classNamePtr),
                    0,
                    0, 0, 0, 0,
                    new HWND((nint)(-3)),   // HWND_MESSAGE：消息-only 窗口
                    default,
                    wndClass.hInstance,
                    default);
            }
        }

        if (_hwnd.IsNull)
        {
            RegistrationFailed?.Invoke("热键服务启动失败（无法创建消息窗口），请重启程序");
            return;
        }

        lock (MapLock)
            Map[(nint)_hwnd.Value] = this;

        RegisterScreenshotHotkey(_hwnd);

        // Set*Key 可能早于线程创建（配置在设置窗加载），这里补注册
        if (_fullScreenVk != 0)
            RegisterFullScreenHotkey(_hwnd);
        if (_pinVk != 0)
            RegisterPinHotkey(_hwnd);
        if (_ocrVk != 0)
            RegisterOcrHotkey(_hwnd);
        if (_modeVk != 0)
            RegisterModeHotkey(_hwnd);

        while (!_stop)
        {
            var ret = PInvoke.GetMessage(out var msg, default, 0, 0);
            if (ret.Value == 0 || ret.Value == -1)
                break;

            _ = PInvoke.TranslateMessage(msg);
            _ = PInvoke.DispatchMessage(msg);
        }
    }

    /// <summary>取景开始：注册全局 Esc（线程安全，投递到热键线程执行）。</summary>
    public void BeginCaptureMode()
    {
        if (!_hwnd.IsNull)
            _ = PInvoke.PostMessage(_hwnd, WM_APP_REGISTER_CANCEL, default, default);
    }

    /// <summary>取景结束：注销全局 Esc。</summary>
    public void EndCaptureMode()
    {
        if (!_hwnd.IsNull)
            _ = PInvoke.PostMessage(_hwnd, WM_APP_UNREGISTER_CANCEL, default, default);
    }

    /// <summary>设置/更换全屏截图热键（线程安全，投递到热键线程注册）。</summary>
    public void SetFullscreenKey(int virtualKey, int modifiers)
    {
        _fullScreenVk = virtualKey;
        _fullScreenMods = modifiers;

        if (!_hwnd.IsNull)
            _ = PInvoke.PostMessage(_hwnd, WM_APP_SET_FULLSCREEN, default, default);
    }

    /// <summary>设置/更换「从剪贴板贴图」热键。</summary>
    public void SetPinKey(int virtualKey, int modifiers)
    {
        _pinVk = virtualKey;
        _pinMods = modifiers;

        if (!_hwnd.IsNull)
            _ = PInvoke.PostMessage(_hwnd, WM_APP_SET_PIN, default, default);
    }

    /// <summary>设置/更换「从剪贴板识别文字」热键。</summary>
    public void SetOcrKey(int virtualKey, int modifiers)
    {
        _ocrVk = virtualKey;
        _ocrMods = modifiers;

        if (!_hwnd.IsNull)
            _ = PInvoke.PostMessage(_hwnd, WM_APP_SET_OCR, default, default);
    }

    /// <summary>设置/更换「切换截图模式」热键。</summary>
    public void SetModeKey(int virtualKey, int modifiers)
    {
        _modeVk = virtualKey;
        _modeMods = modifiers;

        if (!_hwnd.IsNull)
            _ = PInvoke.PostMessage(_hwnd, WM_APP_SET_MODE, default, default);
    }

    private void RegisterScreenshotHotkey(HWND hwnd)
    {
        _ = PInvoke.UnregisterHotKey(hwnd, ScreenshotHotkeyId);

        if (_screenshotVk == 0)
            return;

        var ok = PInvoke.RegisterHotKey(
            hwnd,
            ScreenshotHotkeyId,
            (HOT_KEY_MODIFIERS)((uint)_screenshotMods | MOD_NOREPEAT),
            (uint)_screenshotVk);

        App.Log($"Hotkey: screenshot vk=0x{_screenshotVk:X} mods={_screenshotMods} register={ok}");
        if (!ok)
            ReportFailure("截图", _screenshotVk, _screenshotMods);
    }

    private void RegisterFullScreenHotkey(HWND hwnd)
    {
        _ = PInvoke.UnregisterHotKey(hwnd, FullScreenHotkeyId);

        if (_fullScreenVk == 0)
            return;

        var ok = PInvoke.RegisterHotKey(
            hwnd,
            FullScreenHotkeyId,
            (HOT_KEY_MODIFIERS)((uint)_fullScreenMods | MOD_NOREPEAT),
            (uint)_fullScreenVk);

        App.Log($"Hotkey: fullscreen vk=0x{_fullScreenVk:X} mods={_fullScreenMods} register={ok}");
        if (!ok)
            ReportFailure("全屏截图", _fullScreenVk, _fullScreenMods);
    }

    private void RegisterPinHotkey(HWND hwnd)
    {
        _ = PInvoke.UnregisterHotKey(hwnd, PinHotkeyId);

        if (_pinVk == 0)
            return;

        var ok = PInvoke.RegisterHotKey(
            hwnd,
            PinHotkeyId,
            (HOT_KEY_MODIFIERS)((uint)_pinMods | MOD_NOREPEAT),
            (uint)_pinVk);

        App.Log($"Hotkey: pin vk=0x{_pinVk:X} mods={_pinMods} register={ok}");
        if (!ok)
            ReportFailure("贴图", _pinVk, _pinMods);
    }

    private void RegisterOcrHotkey(HWND hwnd)
    {
        _ = PInvoke.UnregisterHotKey(hwnd, OcrHotkeyId);

        if (_ocrVk == 0)
            return;

        var ok = PInvoke.RegisterHotKey(
            hwnd,
            OcrHotkeyId,
            (HOT_KEY_MODIFIERS)((uint)_ocrMods | MOD_NOREPEAT),
            (uint)_ocrVk);

        App.Log($"Hotkey: ocr vk=0x{_ocrVk:X} mods={_ocrMods} register={ok}");
        if (!ok)
            ReportFailure("OCR", _ocrVk, _ocrMods);
    }

    private void RegisterModeHotkey(HWND hwnd)
    {
        _ = PInvoke.UnregisterHotKey(hwnd, ModeHotkeyId);

        if (_modeVk == 0)
            return;

        var ok = PInvoke.RegisterHotKey(
            hwnd,
            ModeHotkeyId,
            (HOT_KEY_MODIFIERS)((uint)_modeMods | MOD_NOREPEAT),
            (uint)_modeVk);

        App.Log($"Hotkey: mode vk=0x{_modeVk:X} mods={_modeMods} register={ok}");
        if (!ok)
            ReportFailure("切换截图模式", _modeVk, _modeMods);
    }

    private void ReportFailure(string name, int vk, int mods) =>
        RegistrationFailed?.Invoke($"{name}热键 {Describe(vk, mods)} 注册失败（可能被其他程序占用，换一个组合键）");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        HotkeyService? self = null;
        if (!hwnd.IsNull)
        {
            lock (MapLock)
                Map.TryGetValue((nint)hwnd.Value, out self);
        }

        if (self is null)
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

        switch (msg)
        {
            case WM_HOTKEY when (int)wParam.Value == ScreenshotHotkeyId:
                self.HotkeyPressed?.Invoke();
                break;

            case WM_HOTKEY when (int)wParam.Value == FullScreenHotkeyId:
                self.FullScreenHotkeyPressed?.Invoke();
                break;

            case WM_HOTKEY when (int)wParam.Value == PinHotkeyId:
                self.PinHotkeyPressed?.Invoke();
                break;

            case WM_HOTKEY when (int)wParam.Value == OcrHotkeyId:
                self.OcrHotkeyPressed?.Invoke();
                break;

            case WM_HOTKEY when (int)wParam.Value == ModeHotkeyId:
                self.ModeHotkeyPressed?.Invoke();
                break;

            case WM_HOTKEY when (int)wParam.Value == CancelHotkeyId:
                self.CancelPressed?.Invoke();
                break;

            case WM_APP_SET_FULLSCREEN:
                self.RegisterFullScreenHotkey(hwnd);
                return default;

            case WM_APP_SET_PIN:
                self.RegisterPinHotkey(hwnd);
                return default;

            case WM_APP_SET_OCR:
                self.RegisterOcrHotkey(hwnd);
                return default;

            case WM_APP_SET_MODE:
                self.RegisterModeHotkey(hwnd);
                return default;

            case WM_APP_REGISTER_CANCEL:
                _ = PInvoke.RegisterHotKey(hwnd, CancelHotkeyId, (HOT_KEY_MODIFIERS)MOD_NOREPEAT, VK_ESCAPE);
                return default;

            case WM_APP_UNREGISTER_CANCEL:
                _ = PInvoke.UnregisterHotKey(hwnd, CancelHotkeyId);
                return default;

            case WM_CLOSE:
                _ = PInvoke.UnregisterHotKey(hwnd, ScreenshotHotkeyId);
                _ = PInvoke.UnregisterHotKey(hwnd, CancelHotkeyId);
                _ = PInvoke.UnregisterHotKey(hwnd, FullScreenHotkeyId);
                _ = PInvoke.UnregisterHotKey(hwnd, PinHotkeyId);
                _ = PInvoke.UnregisterHotKey(hwnd, OcrHotkeyId);
                _ = PInvoke.UnregisterHotKey(hwnd, ModeHotkeyId);
                _ = PInvoke.DestroyWindow(hwnd);
                return default;

            case WM_DESTROY:
                PInvoke.PostQuitMessage(0);
                return default;
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _stop = true;
        if (!_hwnd.IsNull)
        {
            lock (MapLock)
                Map.Remove((nint)_hwnd.Value);
        }
        if (!_hwnd.IsNull)
            _ = PInvoke.PostMessage(_hwnd, WM_CLOSE, default, default);

        _thread?.Join(1500);
        _thread = null;
        _hwnd = default;
    }

    /// <summary>M0 用键名 → 虚拟键码；旧配置 hotkeyVk 为 0 时按 hotkeyKey 回退。</summary>
    public static uint KeyNameToVirtualKey(string name) => name.ToUpperInvariant() switch
    {
        "F1" => 0x70,
        "F2" => 0x71,
        "F3" => 0x72,
        "F4" => 0x73,
        "F5" => 0x74,
        "F6" => 0x75,
        "F7" => 0x76,
        "F8" => 0x77,
        "F9" => 0x78,
        "F10" => 0x79,
        "F11" => 0x7A,
        "F12" => 0x7B,
        "PAUSE" => 0x13,
        "SCROLL" => 0x91,
        _ => 0x70,
    };

    /// <summary>把 vk + 修饰键位掩码转成可展示文本，例如 "Ctrl + Alt + P"。</summary>
    public static string Describe(int vk, int modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        if (vk != 0)
            parts.Add(((Windows.System.VirtualKey)vk).ToString());
        return string.Join(" + ", parts);
    }
}
