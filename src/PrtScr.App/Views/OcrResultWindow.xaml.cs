using System.IO;
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
    private string _baseStatus = "";
    private string _originalText = "";
    private bool _copying;
    private bool _closed;

    public OcrResultWindow(OcrResult result, long elapsedMs, nint ownerHwnd = 0)
    {
        _ownerHwnd = ownerHwnd;
        InitializeComponent();
        Title = $"{PrtScr.App.AppInfo.Name} OCR 结果";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

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
            // 先标记关闭：下面清空文本会触发 TextChanged，复制若还在重试也不再回写界面。
            _closed = true;
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

        if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
        {
            _baseStatus = $"识别完成 · 用时 {elapsedMs} ms";
            EngineText.Text = $"引擎：{result.Engine}";
            TextOutput.Text = result.Text;
        }
        else if (result.Success)
        {
            // 识别成功但没有文字：保留输入框，用户手动输入后仍可复制。
            _baseStatus = $"未识别到文字 · 用时 {elapsedMs} ms";
            EngineText.Text = $"引擎：{result.Engine} · 图片里可能没有可识别的文字";
            TextOutput.Text = "";
        }
        else
        {
            _baseStatus = "识别失败";
            EngineText.Text = result.Error ?? "未知错误";
            TextOutput.Text = "";
            TextOutput.IsEnabled = false;
        }

        _originalText = TextOutput.Text;
        StatusText.Text = _baseStatus;
        UpdateCopyButton();
    }

    /// <summary>只有输入框可用、内容非空且没有正在进行的复制时，复制按钮才可点。</summary>
    private void UpdateCopyButton() =>
        CopyButton.IsEnabled = !_copying && TextOutput.IsEnabled && !string.IsNullOrWhiteSpace(TextOutput.Text);

    /// <summary>复制结果是异步反馈，通知读屏软件播报状态变化。</summary>
    private void AnnounceStatus(string text)
    {
        StatusText.Text = text;
        Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(StatusText)
            ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void TextOutput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_closed)
            return;

        UpdateCopyButton();
        // 复制进行中由复制结果给出准确提示（会比对点击时与当前文本）。
        if (_copying)
            return;

        var status = TextOutput.Text == _originalText
            ? _baseStatus
            : $"{_baseStatus} · 已编辑，复制会使用当前文本";
        if (StatusText.Text != status)
            StatusText.Text = status;
    }

    private void UnregisterEscapeHotkey()
    {
        if (!_escapeRegistered)
            return;
        _escapeRegistered = false;
        _ = Windows.Win32.PInvoke.UnregisterHotKey(
            new Windows.Win32.Foundation.HWND(WinRT.Interop.WindowNative.GetWindowHandle(this)), EscapeHotkeyId);
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        // 复制的是点击这一刻的文本快照；重试期间按钮禁用，避免重复写剪贴板。
        var text = TextOutput.Text;
        if (_copying || _closed || string.IsNullOrWhiteSpace(text))
            return;

        _copying = true;
        UpdateCopyButton();
        AnnounceStatus("正在复制…");

        bool ok;
        try
        {
            ok = await PrtScr.App.Services.ClipboardService.SetTextAsync(text);
        }
        finally
        {
            _copying = false;
        }

        App.Log($"OCR: copy text {(ok ? "ok" : "failed")} chars={text.Length} closed={_closed}");
        if (_closed)
            return;

        UpdateCopyButton();
        if (!ok)
            AnnounceStatus("复制失败：剪贴板可能被其他程序占用，请稍后再试");
        else if (TextOutput.Text != text)
            AnnounceStatus("已复制点击时的文本；之后的修改还没有复制");
        else
            AnnounceStatus($"已复制到剪贴板（{text.Length} 个字符）");
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        UnregisterEscapeHotkey();
        Close();
    }
}