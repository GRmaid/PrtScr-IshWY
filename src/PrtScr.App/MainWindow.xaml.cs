using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using PrtScr.App.Services;
using System.IO;

namespace PrtScr.App;

public sealed partial class MainWindow : Window
{
    private readonly HotkeyService _hotkey = new();
    private string? _recordingAction;
    private Button? _recordingButton;
    private int _pinHotkeyBusy;
    private int _ocrHotkeyBusy;
    private readonly bool _startHidden;
    private bool _suppressStartupToggle;
    private bool _suppressHistoryToggle;
    private Views.HistoryWindow? _historyWindow;

    public MainWindow(bool startHidden = false)
    {
        _startHidden = startHidden;
        App.Log($"MainWindow: ctor start (startHidden={_startHidden})");
        InitializeComponent();
        App.Log("MainWindow: InitializeComponent done");
        Title = $"{AppInfo.Name} 设置";

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

        // 设置窗 Esc = 收进托盘（与点关闭一致）
        var interceptor = new Services.KeyInterceptor(WinRT.Interop.WindowNative.GetWindowHandle(this));
        interceptor.KeyDown += vk =>
        {
            if (vk == 0x1B && Environment.TickCount64 - Services.OcrUiService.LastWindowClosedTick > 2500)
                AppWindow.Hide();
        };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(780, 900));

        // 托盘常驻：点关闭 = 隐藏到托盘，真正退出走托盘菜单
        AppWindow.Closing += (sender, e) =>
        {
            e.Cancel = true;
            sender.Hide();
        };

        InitTray();
        ((FrameworkElement)Content).Loaded += OnLoaded;

        ApplyBackgroundFromConfig();
        InitHotkey();
        InitFullHotkey();
        InitPinOcrHotkeys();
        AboutText.Text = $"{AppInfo.Name} v{typeof(App).Assembly.GetName().Version} · {AppInfo.Tagline} · 分窗口截屏截图工具";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        OverlayToggle.IsOn = App.Config.Current.CaptureOverlayWindows;
        OcrPreferUmiToggle.IsOn = App.Config.Current.PreferUmiOcr;
        UmiPortBox.Value = App.Config.Current.UmiOcrPort;

        _suppressStartupToggle = true;
        StartupToggle.IsOn = StartupService.IsEnabled();
        _suppressStartupToggle = false;
        StartupStatusText.Text = StartupToggle.IsOn
            ? "已启用：登录 Windows 后静默运行到托盘。"
            : "开机自启动未启用。";

        _suppressHistoryToggle = true;
        HistoryToggle.IsOn = App.Config.Current.SaveHistory;
        _suppressHistoryToggle = false;
        RefreshHistoryStatus();

        // 预热：成像（UI 线程）+ 抓屏/枚举/PrintWindow（后台），让第一次 F1 就是全热状态
        try
        {
            _ = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(1, 1);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var warm = new PrtScr.Interop.WindowInfo(
                hwnd, "warm", "", "", 0, new PrtScr.Interop.WindowRect(0, 0, 16, 16),
                false, false, false, false, false);
            _ = PrtScr.Capture.WindowCapture.CaptureWindow(warm);
        }
        catch
        {
        }
        Services.CaptureService.WarmUpNative();
        // 预创建取景窗：第一次 F1 也走复用路径，杜绝全屏窗口首次呈现的白底爆闪
        Services.CaptureService.Instance.EnsureOverlay();

        _ = RefreshOcrStatusAsync();
    }

    private void HistoryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHistoryToggle)
            return;

        var enabled = HistoryToggle.IsOn;
        if (App.Config.Current.SaveHistory == enabled)
        {
            RefreshHistoryStatus();
            return;
        }

        App.Config.Current.SaveHistory = enabled;
        App.Config.Save();
        RefreshHistoryStatus();
        App.Log($"History: 保存开关 = {enabled}");
    }

    private void RefreshHistoryStatus()
    {
        if (!App.Config.Current.SaveHistory)
        {
            HistoryStatusText.Text = "已关闭：截图只进剪贴板，不写入本机历史。";
            return;
        }

        var count = CaptureHistoryService.Instance.GetItems().Count;
        HistoryStatusText.Text = $"已开启：本机保留最近 {count} 张（data\\history，最多 30 张）。";
    }

    private void OpenHistory()
    {
        try
        {
            if (_historyWindow is null)
            {
                var window = new Views.HistoryWindow();
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_historyWindow, window))
                        _historyWindow = null;
                    RefreshHistoryStatus();
                };
                window.HistoryChanged += RefreshHistoryStatus;
                _historyWindow = window;
                window.Activate();
            }
            else
            {
                _historyWindow.Activate();
                _ = _historyWindow.RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            App.Log($"History: 打开窗口失败 {ex}");
        }
    }

    private void OpenHistory_Click(object sender, RoutedEventArgs e) => OpenHistory();

    private void History_Click(object sender, RoutedEventArgs e) => OpenHistory();

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var cleared = CaptureHistoryService.Instance.Clear();
        RefreshHistoryStatus();
        if (_historyWindow is not null)
            _ = _historyWindow.RefreshAsync();

        HistoryStatusText.Text = cleared > 0 ? $"已清空 {cleared} 张历史截图。" : "历史目录已经是空的。";
    }

    private void UmiPort_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
            return;

        var port = (int)args.NewValue;
        if (port <= 0 || port == App.Config.Current.UmiOcrPort)
            return;

        App.Config.Current.UmiOcrPort = port;
        App.Config.Save();
        _ = RefreshOcrStatusAsync();
    }

    private void TestUmi_Click(object sender, RoutedEventArgs e) => _ = RefreshOcrStatusAsync();

    private void OcrPreferUmi_Toggled(object sender, RoutedEventArgs e)
    {
        if (OcrPreferUmiToggle is null)
            return;

        App.Config.Current.PreferUmiOcr = OcrPreferUmiToggle.IsOn;
        App.Config.Save();
        _ = RefreshOcrStatusAsync();
    }

    private void OverlayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (OverlayToggle is null)
            return;

        App.Config.Current.CaptureOverlayWindows = OverlayToggle.IsOn;
        App.Config.Save();
    }

    private void InitTray()
    {
        try
        {
            TrayIcon.ForceCreate();
            TrayIcon.LeftClickCommand = new DelegateCommand(ShowFromTray);
            TrayIcon.DoubleClickCommand = new DelegateCommand(ShowFromTray);
            TrayIcon.ToolTipText = $"{AppInfo.Name} — 单击/双击打开设置，右键菜单";
            TrayMenu.Opening += (_, _) => App.Log("Tray: menu opening");
            App.Log("Tray: 初始化完成");
        }
        catch (Exception ex)
        {
            App.Log($"Tray: 初始化失败 {ex}");
        }
    }

    /// <summary>--startup 静默启动：不显示设置窗，但把抓屏链路预热好。</summary>
    internal void PrepareHiddenStartup()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                Services.CaptureService.WarmUpNative();
                Services.CaptureService.Instance.EnsureOverlay();
                App.Log("MainWindow: startup 预热完成");
            }
            catch (Exception ex)
            {
                App.Log($"MainWindow: startup 预热失败 {ex.Message}");
            }
        });
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStartupToggle || StartupToggle is null)
            return;

        var enabled = StartupToggle.IsOn;
        if (StartupService.TrySetEnabled(enabled, out var error))
        {
            StartupStatusText.Text = enabled
                ? "已启用：登录 Windows 后静默运行到托盘。"
                : "已关闭开机自启动。";
            return;
        }

        StartupStatusText.Text = $"设置失败：{error}";
        _suppressStartupToggle = true;
        StartupToggle.IsOn = !enabled;
        _suppressStartupToggle = false;
    }

    internal void ShowFromTray()
    {
        try
        {
            var hwnd = new Windows.Win32.Foundation.HWND(
                WinRT.Interop.WindowNative.GetWindowHandle(this));

            // 预热取景窗后主窗口的激活链可能失效，这里走 Win32 强制恢复 + 前台激活
            AppWindow.Show();
            _ = Windows.Win32.PInvoke.ShowWindow(hwnd, Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_SHOW);
            _ = Windows.Win32.PInvoke.SetForegroundWindow(hwnd);
            Activate();
            App.Log($"Tray: main window shown, visible={_ = Windows.Win32.PInvoke.IsWindowVisible(hwnd)}");
        }
        catch (Exception ex)
        {
            App.Log($"Tray: 显示设置窗失败 {ex}");
        }
    }

    private void ShowSettings_Click(object sender, RoutedEventArgs e)
    {
        App.Log("Tray: settings clicked");
        ShowFromTray();
    }

    private async void PastePin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var created = await Services.PinService.PasteFromClipboardAsync();
            if (!created)
                HotkeyStatusText.Text = "剪贴板里没有图片可贴（先截一张图或复制一张图片）";
        }
        catch (Exception ex)
        {
            App.Log($"贴图入口异常: {ex}");
        }
    }

    private async void OcrClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var bytes = await Services.PinService.ReadClipboardImageBytesAsync();
            if (bytes is null)
            {
                HotkeyStatusText.Text = "剪贴板里没有图片可供识别";
                return;
            }
            await Services.OcrUiService.RecognizeAndShowAsync(bytes,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        catch (Exception ex)
        {
            App.Log($"剪贴板 OCR 异常: {ex}");
        }
    }

    private async void OnPinHotkeyPressed()
    {
        if (Services.CaptureService.Instance.IsCapturing)
            return;
        if (Interlocked.Exchange(ref _pinHotkeyBusy, 1) != 0)
            return;

        try
        {
            App.Log("Pin: hotkey pressed");
            var created = await Services.PinService.PasteFromClipboardAsync();
            if (!created)
            {
                var message = "剪贴板里没有图片可贴（先截一张图或复制一张图片）";
                HotkeyStatusText.Text = message;
                TrayIcon.ShowNotification($"{AppInfo.Name} 贴图", message);
            }
        }
        catch (Exception ex)
        {
            App.Log($"贴图热键异常: {ex}");
        }
        finally
        {
            Volatile.Write(ref _pinHotkeyBusy, 0);
        }
    }

    private async void OnOcrHotkeyPressed()
    {
        if (Services.CaptureService.Instance.IsCapturing)
            return;
        if (Interlocked.Exchange(ref _ocrHotkeyBusy, 1) != 0)
            return;

        try
        {
            App.Log("OCR: hotkey pressed");
            var bytes = await Services.PinService.ReadClipboardImageBytesAsync();
            if (bytes is null)
            {
                var message = "剪贴板里没有图片可供识别";
                HotkeyStatusText.Text = message;
                TrayIcon.ShowNotification($"{AppInfo.Name} OCR", message);
                return;
            }

            await Services.OcrUiService.RecognizeAndShowAsync(bytes,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        catch (Exception ex)
        {
            App.Log($"OCR 热键异常: {ex}");
        }
        finally
        {
            Volatile.Write(ref _ocrHotkeyBusy, 0);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        App.Log("Tray: exit clicked");
        TrayIcon.Dispose();
        _hotkey.Dispose();
        // 同 --smoke：Application.Exit 不可靠，直接 Win32 终止
        Windows.Win32.PInvoke.ExitProcess(0);
    }

    private void InitHotkey()
    {
        var config = App.Config.Current;

        // 任意一个热键被占用时，在设置页给出可读提示（日志同时留痕）
        _hotkey.RegistrationFailed += message => DispatcherQueue.TryEnqueue(() =>
        {
            HotkeyStatusText.Text = message;
            App.Log($"Hotkey: {message}");
        });

        Services.CaptureService.Instance.Hotkey = _hotkey;
        _hotkey.HotkeyPressed += () => DispatcherQueue.TryEnqueue(OnHotkeyPressed);
        _hotkey.CancelPressed += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (Services.CaptureService.Instance.IsCapturing)
                Services.CaptureService.Instance.CancelCurrent();
        });

        // 旧配置没有 hotkeyVk 时按 hotkeyKey（F1…F12）回退；新配置支持任意组合键
        var vk = config.HotkeyVk > 0
            ? config.HotkeyVk
            : (int)HotkeyService.KeyNameToVirtualKey(config.HotkeyKey);
        var mods = (int)config.HotkeyModifiers;

        _hotkey.Start(vk, mods);
        ScreenshotHotkeyText.Text = HotkeyService.Describe(vk, mods);
        App.Log($"Hotkey: 截图配置 {HotkeyService.Describe(vk, mods)}");
    }

    private void InitFullHotkey()
    {
        var config = App.Config.Current;

        // 全屏截图热键走全局键盘钩子（和 Esc 同一套）：游戏用 Raw Input/钩子吞键时 RegisterHotKey 会收不到。
        // 但低级钩子会被系统静默摘除（LowLevelHooksTimeout），所以再挂一条 RegisterHotKey 兜底；
        // 两条通道同时命中时用 TryClaimFullScreenHotkey 去重。
        var vk = config.FullHotkeyVk > 0
            ? config.FullHotkeyVk
            : (int)HotkeyService.KeyNameToVirtualKey(config.FullScreenHotkeyKey);
        var mods = config.FullHotkeyModifiers;
        Services.EscOverPinHook.SetFullScreenKey(vk, mods);
        Services.EscOverPinHook.FullScreenPressed += () => DispatcherQueue.TryEnqueue(OnFullScreenPressed);

        _hotkey.FullScreenHotkeyPressed += () =>
        {
            if (!Services.EscOverPinHook.TryClaimFullScreenHotkey())
                return;

            App.Log("QuickCapture: RegisterHotKey 兜底命中");
            // 兜底通道命中说明钩子可能已被系统摘除：请求钩子线程卸载重装，恢复 Esc/热键的钩子通道
            Services.EscOverPinHook.NotifyFallbackHit();
            DispatcherQueue.TryEnqueue(OnFullScreenPressed);
        };
        _hotkey.SetFullscreenKey(vk, mods);

        FullHotkeyText.Text = HotkeyService.Describe(vk, mods);
        App.Log($"QuickCapture: hook vk=0x{vk:X} mods={mods}");
    }

    private void InitPinOcrHotkeys()
    {
        var config = App.Config.Current;

        _hotkey.PinHotkeyPressed += () => DispatcherQueue.TryEnqueue(OnPinHotkeyPressed);
        _hotkey.OcrHotkeyPressed += () => DispatcherQueue.TryEnqueue(OnOcrHotkeyPressed);

        _hotkey.SetPinKey(config.PinHotkeyVk, config.PinHotkeyModifiers);
        _hotkey.SetOcrKey(config.OcrHotkeyVk, config.OcrHotkeyModifiers);

        PinHotkeyText.Text = HotkeyService.Describe(config.PinHotkeyVk, config.PinHotkeyModifiers);
        OcrHotkeyText.Text = HotkeyService.Describe(config.OcrHotkeyVk, config.OcrHotkeyModifiers);
        App.Log($"Hotkey: 贴图配置 {PinHotkeyText.Text} / OCR 配置 {OcrHotkeyText.Text}");
    }

    private int _quickCaptureBusy;

    private async void OnFullScreenPressed()
    {
        // 钩子通道和 RegisterHotKey 兜底可能在几百毫秒内重复命中同一次按键，
        // 上一次采集没结束时直接丢弃，避免并发抢前台/并发写剪贴板
        if (Interlocked.Exchange(ref _quickCaptureBusy, 1) != 0)
        {
            App.Log("QuickCapture: 上一次还没结束，忽略本次触发");
            return;
        }

        App.Log("QuickCapture: hotkey pressed");
        try
        {
            using var bypass = await Services.FullscreenBypass.BeginAsync();

            var result = await Services.CaptureService.CaptureFullScreenPngAsync();
            if (result.LikelyBlack)
            {
                App.Log($"QuickCapture: 首帧疑似黑屏，重试一次 (bypass={bypass.Activated})");
                await Task.Delay(150);
                result = await Services.CaptureService.CaptureFullScreenPngAsync();
            }

            if (result.Png is null)
            {
                HotkeyStatusText.Text = "全屏截图失败";
                App.Log($"QuickCapture: 失败，无图像 (bypass={bypass.Activated})");
                return;
            }

            if (result.LikelyBlack)
            {
                HotkeyStatusText.Text = "当前画面像是独占全屏游戏，Windows 无法截取";
                App.Log($"QuickCapture: 两次都是黑屏 (bypass={bypass.Activated})");
                TrayIcon.ShowNotification($"{AppInfo.Name} 全屏截图",
                    "当前画面疑似独占全屏（游戏绕过了系统合成器），无法截屏。请把游戏改为“无边框窗口”模式再试。");
                return;
            }

            var copyTask = Services.ClipboardService.SetImageAsync(result.Png);
            var historyTask = Services.CaptureHistoryService.Instance.AddAsync(result.Png, fullScreen: true);
            await Task.WhenAll(copyTask, historyTask);

            var ok = copyTask.Result;
            HotkeyStatusText.Text = ok ? "全屏截图已复制到剪贴板" : "全屏截图复制失败（剪贴板被占用）";
            App.Log($"QuickCapture: 完成 {result.Png.Length / 1024}KB copied={ok} bypass={bypass.Activated} history={historyTask.Result is not null}");
            if (historyTask.Result is { } savedPath)
                App.Log($"QuickCapture: history saved {savedPath}");
        }
        catch (Exception ex)
        {
            App.Log($"全屏截图异常: {ex}");
        }
        finally
        {
            Volatile.Write(ref _quickCaptureBusy, 0);
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => OnFullScreenPressed();

    private void HotkeyRecord_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action)
            return;

        // 换一个录制目标时，先把上一个按钮恢复原状
        if (_recordingButton is not null && !ReferenceEquals(_recordingButton, button))
            ResetHotkeyRecording(_recordingButton);

        _recordingAction = action;
        _recordingButton = button;
        button.Content = "请按下组合键…（Esc 取消）";
        _ = button.Focus(FocusState.Programmatic);
    }

    private void HotkeyRecord_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_recordingAction is null || sender is not Button button)
            return;

        e.Handled = true;
        var vk = (int)e.Key;
        if (vk is 0x11 or 0x12 or 0x10 or 0x5B or 0x5C)   // 只按了修饰键
            return;

        if (vk == 0x1B)
        {
            ResetHotkeyRecording(button);
            return;
        }

        var mods = 0;
        if (IsDown(0x11)) mods |= 2;
        if (IsDown(0x12)) mods |= 1;
        if (IsDown(0x10)) mods |= 4;
        if (IsDown(0x5B) || IsDown(0x5C)) mods |= 8;

        // 裸字母/数字会抢走正常输入，只允许裸 F1–F12；其余必须带修饰键
        var isFunctionKey = vk is >= 0x70 and <= 0x7B;
        if (mods == 0 && !isFunctionKey)
        {
            HotkeyStatusText.Text = "请按住 Ctrl / Alt / Shift / Win 之一（F1–F12 可单独使用）";
            return;
        }

        if (TryFindHotkeyConflict(_recordingAction, vk, mods, out var other))
        {
            HotkeyStatusText.Text = $"这个组合键已经给了「{other}」，换一个";
            ResetHotkeyRecording(button);
            return;
        }

        var action = _recordingAction;
        ApplyHotkey(action, vk, mods);
        ResetHotkeyRecording(button);
    }

    private void ResetHotkeyRecording(Button button)
    {
        button.Content = "点击录制组合键…";
        _recordingAction = null;
        _recordingButton = null;
    }

    private void ApplyHotkey(string action, int vk, int mods)
    {
        var config = App.Config.Current;
        var text = HotkeyService.Describe(vk, mods);

        switch (action)
        {
            case "screenshot":
                config.HotkeyVk = vk;
                config.HotkeyModifiers = (uint)mods;
                App.Config.Save();
                _hotkey.Restart(vk, mods);
                ScreenshotHotkeyText.Text = text;
                HotkeyStatusText.Text = $"截图热键已设为 {text}";
                break;

            case "fullscreen":
                config.FullHotkeyVk = vk;
                config.FullHotkeyModifiers = mods;
                App.Config.Save();
                Services.EscOverPinHook.SetFullScreenKey(vk, mods);
                _hotkey.SetFullscreenKey(vk, mods);
                FullHotkeyText.Text = text;
                HotkeyStatusText.Text = $"全屏截图热键已设为 {text}";
                break;

            case "pin":
                config.PinHotkeyVk = vk;
                config.PinHotkeyModifiers = mods;
                App.Config.Save();
                _hotkey.SetPinKey(vk, mods);
                PinHotkeyText.Text = text;
                HotkeyStatusText.Text = $"贴图热键已设为 {text}";
                break;

            case "ocr":
                config.OcrHotkeyVk = vk;
                config.OcrHotkeyModifiers = mods;
                App.Config.Save();
                _hotkey.SetOcrKey(vk, mods);
                OcrHotkeyText.Text = text;
                HotkeyStatusText.Text = $"OCR 热键已设为 {text}";
                break;
        }

        App.Log($"Hotkey: 更新 {action} -> {text}");
    }

    /// <summary>四个热键不允许重复，否则后注册的那个会静默失败。</summary>
    private static bool TryFindHotkeyConflict(string action, int vk, int mods, out string other)
    {
        other = "";
        var c = App.Config.Current;

        var screenshotVk = c.HotkeyVk > 0
            ? c.HotkeyVk
            : (int)HotkeyService.KeyNameToVirtualKey(c.HotkeyKey);
        var screenshotMods = (int)c.HotkeyModifiers;
        var fullVk = c.FullHotkeyVk > 0
            ? c.FullHotkeyVk
            : (int)HotkeyService.KeyNameToVirtualKey(c.FullScreenHotkeyKey);

        if (action != "screenshot" && vk == screenshotVk && mods == screenshotMods)
        {
            other = "截图";
            return true;
        }
        if (action != "fullscreen" && vk == fullVk && mods == c.FullHotkeyModifiers)
        {
            other = "全屏截图";
            return true;
        }
        if (action != "pin" && vk == c.PinHotkeyVk && mods == c.PinHotkeyModifiers)
        {
            other = "贴图";
            return true;
        }
        if (action != "ocr" && vk == c.OcrHotkeyVk && mods == c.OcrHotkeyModifiers)
        {
            other = "OCR";
            return true;
        }

        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;


    private async void OnHotkeyPressed()
    {
        // M1：唤起冻屏取景器。Dispatcher 回调里的异常会变成“暂存异常”直接杀进程，
        // 必须在这里兜住并写日志
        try
        {
            await Services.CaptureService.Instance.StartCaptureAsync();
        }
        catch (Exception ex)
        {
            App.Log($"热键处理异常: {ex}");
        }
    }

    private async void PickBackground_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickImageAsync();
        if (path is null)
            return;

        var config = App.Config.Current;
        config.BackgroundImagePath = path;
        App.Config.Save();
        ApplyBackgroundFromConfig();
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        var config = App.Config.Current;
        config.BackgroundImagePath = "";
        App.Config.Save();
        ApplyBackgroundFromConfig();
    }

    private void BackgroundOpacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (BackgroundImage is null)
            return;

        BackgroundImage.Opacity = e.NewValue;
        if (App.Config.Current.BackgroundOpacity != e.NewValue)
        {
            App.Config.Current.BackgroundOpacity = e.NewValue;
            App.Config.Save();
        }
    }

    private void ApplyBackgroundFromConfig()
    {
        var config = App.Config.Current;
        var path = config.BackgroundImagePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            BackgroundImage.Source = new BitmapImage(new Uri(path));
            BackgroundImage.Opacity = config.BackgroundOpacity;
            BackgroundOpacitySlider.Value = config.BackgroundOpacity;
        }
        else
        {
            BackgroundImage.Source = null;
            BackgroundImage.Opacity = 0;
        }
    }

    private async Task<string?> PickImageAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".webp");

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async Task RefreshOcrStatusAsync()
    {
        var windows = PrtScr.Ocr.WindowsOcrEngine.GetStatus();
        var umi = await new PrtScr.Ocr.UmiOcrClient(App.Config.Current.UmiOcrPort).ProbeAsync();

        OcrStatusText.Text =
            $"Windows OCR：{(windows.Available ? $"可用（{windows.LanguageCount} 种语言）" : "不可用（系统缺少 OCR 语言包）")}\n" +
            $"Umi-OCR：{(umi.Online ? $"在线 ✓ 自动启用（端口 {umi.Port}）" : $"未运行（可选引擎，端口 {umi.Port} 未响应；未安装也能正常使用本软件）")}";
    }
}
