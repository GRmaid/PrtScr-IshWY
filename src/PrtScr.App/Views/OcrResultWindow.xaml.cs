using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PrtScr.App.Services;
using PrtScr.Ocr;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace PrtScr.App.Views;

/// <summary>OCR 结果窗：文本可编辑、可整段复制。</summary>
public sealed partial class OcrResultWindow : Window
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    /// <summary>
    /// 强制把前台还给 owner：Windows 有前台锁，直接 SetForegroundWindow 常被拒绝，
    /// 必须临时把当前前台线程与目标线程的输入队列挂接后再设置。
    /// </summary>
    private static void ForceForeground(nint hwnd)
    {
        if (hwnd == 0)
            return;

        var foreground = GetForegroundWindow();
        var foregroundThread = foreground != 0 ? GetWindowThreadProcessId(foreground, out _) : 0;
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var attached = false;

        try
        {
            if (foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread)
                attached = AttachThreadInput(foregroundThread, targetThread, true);

            _ = Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND(hwnd));
            _ = Windows.Win32.PInvoke.SetFocus(new Windows.Win32.Foundation.HWND(hwnd));
        }
        finally
        {
            if (attached)
                _ = AttachThreadInput(foregroundThread, targetThread, false);
        }
    }

    private const int EscapeHotkeyId = 201;
    private readonly PrtScr.App.Services.KeyInterceptor _keys;
    private readonly nint _ownerHwnd;
    private bool _escapeRegistered;
    public OcrResultWindow(OcrResult result, long elapsedMs, nint ownerHwnd = 0)
    {
        _ownerHwnd = ownerHwnd;
        InitializeComponent();
        Title = $"{PrtScr.App.AppInfo.Name} OCR 结果";

        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(620, area.Width - 80);
        var height = Math.Min(500, area.Height - 80);
        var x = area.X + (area.Width - width) / 2;
        var y = area.Y + (area.Height - height) / 2;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));

        // 贴图是 TOPMOST 窗口，结果窗必须置顶并主动抢前台，否则会被压住/像"最小化"
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = true;

        // 结果窗可能拿不到前台焦点，Esc 用“窗口内拦截 + 全局热键”双保险
        var hwnd = WindowNative.GetWindowHandle(this);

        // 关键：把结果窗设为 owner（贴图）的从属窗口——Windows 会在关闭时自动把激活还给 owner
        if (ownerHwnd != 0)
        {
            _ = SetWindowLongPtr(hwnd, -8 /* GWLP_HWNDPARENT */, ownerHwnd);
            App.Log($"OCR: owner linked = 0x{ownerHwnd:X}");
        }
        _keys = new PrtScr.App.Services.KeyInterceptor(hwnd);
        _keys.KeyDown += vk =>
        {
            if (vk == 0x1B)
                EscRouter.Dispatch();
        };
        _keys.HotKey += id =>
        {
            if (id == EscapeHotkeyId)
                EscRouter.Dispatch();
        };

        // 交给统一仲裁器：与贴图重叠时关最上层，不重叠时固定先关 OCR
        EscRouter.RegisterOcr(hwnd, () => DispatcherQueue.TryEnqueue(() =>
        {
            UnregisterEscapeHotkey();
            Close();
        }));

        _escapeRegistered = Windows.Win32.PInvoke.RegisterHotKey(
            new Windows.Win32.Foundation.HWND(hwnd), EscapeHotkeyId,
            (Windows.Win32.UI.Input.KeyboardAndMouse.HOT_KEY_MODIFIERS)0x4000 /* MOD_NOREPEAT */, 0x1B);
        App.Log($"OCR: register esc hotkey = {_escapeRegistered}");

        Closed += (_, _) =>
        {
            EscRouter.Unregister(hwnd);
            UnregisterEscapeHotkey();

            // 主动丢掉识别文本，让大段结果随窗口一起回收，而不是等 GC 猜
            TextOutput.Text = "";
            StatusText.Text = "";
            EngineText.Text = "";

            // 把焦点还给打开 OCR 的窗口（通常是贴图），否则 Esc 会"失灵"
            ForceForeground(_ownerHwnd);

            App.Log("OCR: window closed");
            if (_ownerHwnd != 0)
            {
                // 窗口关闭后系统可能再次改焦点，延迟补一次
                DispatcherQueue.TryEnqueue(() => ForceForeground(_ownerHwnd));
            }
        };

        if (result.Success)
        {
            StatusText.Text = $"识别完成 · 用时 {elapsedMs} ms";
            EngineText.Text = $"引擎：{result.Engine}";
            TextOutput.Text = result.Text;
        }
        else
        {
            StatusText.Text = "识别失败";
            EngineText.Text = result.Error ?? "未知错误";
            TextOutput.Text = "";
            TextOutput.IsEnabled = false;
        }
    }

    private void UnregisterEscapeHotkey()
    {
        if (!_escapeRegistered)
            return;
        _escapeRegistered = false;
        _ = Windows.Win32.PInvoke.UnregisterHotKey(
            new Windows.Win32.Foundation.HWND(WinRT.Interop.WindowNative.GetWindowHandle(this)), EscapeHotkeyId);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TextOutput.Text))
            return;

        _ = PrtScr.App.Services.ClipboardService.SetTextAsync(TextOutput.Text);
        StatusText.Text = "已复制到剪贴板";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        UnregisterEscapeHotkey();
        Close();
    }
}