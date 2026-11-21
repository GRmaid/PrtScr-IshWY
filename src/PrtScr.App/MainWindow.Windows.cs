using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using PrtScr.App.Services;
using System.Diagnostics;
using System.IO;
using Windows.ApplicationModel.DataTransfer;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App;

/// <summary>MainWindow 部分实现：见文件名。</summary>
public sealed partial class MainWindow
{
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
                    RestorePinsAfterHistory();
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

    /// <summary>贴图右键菜单请求历史窗：临时把贴图降为非置顶，历史窗关闭后恢复。</summary>
    private void ShowHistoryAbovePins(nint _)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var pins = Views.NativePinWindow.SnapshotHandles();
            foreach (var pin in pins)
            {
                _ = PInvoke.SetWindowPos(new HWND(pin), new HWND(-2) /* HWND_NOTOPMOST */,
                    0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
            }
            _historyLoweredPins = pins;

            OpenHistory();
            if (_historyWindow is not null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_historyWindow);
                _ = PInvoke.SetWindowPos(new HWND(hwnd), new HWND(-1) /* HWND_TOPMOST */,
                    0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            }
        });
    }

    private void RestorePinsAfterHistory()
    {
        if (_historyLoweredPins.Length == 0)
            return;
        foreach (var pin in _historyLoweredPins)
        {
            _ = PInvoke.SetWindowPos(new HWND(pin), new HWND(-1) /* HWND_TOPMOST */,
                0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
        }
        _historyLoweredPins = [];
    }

    private void OpenHistory_Click(object sender, RoutedEventArgs e) => OpenHistory();

    private void History_Click(object sender, RoutedEventArgs e) => OpenHistory();

    /// <summary>打开贴图管理器：临时把贴图降为非置顶，管理器关闭后恢复。</summary>
    private void OpenPinManager()
    {
        try
        {
            if (_pinManagerWindow is null)
            {
                var pins = Views.NativePinWindow.SnapshotHandles();
                foreach (var pin in pins)
                {
                    _ = PInvoke.SetWindowPos(new HWND(pin), new HWND(-2) /* HWND_NOTOPMOST */,
                        0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
                }
                _pinManagerLoweredPins = pins;

                var window = new Views.PinManagerWindow();
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_pinManagerWindow, window))
                        _pinManagerWindow = null;
                    RestorePinsAfterPinManager();
                };
                _pinManagerWindow = window;
                window.Activate();
            }
            else
            {
                _pinManagerWindow.Activate();
            }

            if (_pinManagerWindow is not null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_pinManagerWindow);
                _ = PInvoke.SetWindowPos(new HWND(hwnd), new HWND(-1) /* HWND_TOPMOST */,
                    0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Pin manager: 打开失败 {ex}");
        }
    }

    private void OpenPinManager_Click(object sender, RoutedEventArgs e) => OpenPinManager();

    private void RestorePinsAfterPinManager()
    {
        if (_pinManagerLoweredPins.Length == 0)
            return;
        foreach (var pin in _pinManagerLoweredPins)
        {
            _ = PInvoke.SetWindowPos(new HWND(pin), new HWND(-1) /* HWND_TOPMOST */,
                0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
        }
        _pinManagerLoweredPins = [];
    }

    private bool _clearHistoryBusy;

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        // 连点只弹一个确认框；Esc 收托盘的逻辑也会避开确认期间。
        if (_clearHistoryBusy)
            return;

        // 先固定要删的文件：确认期间新截图写进来也不会被一起删掉。
        var targets = CaptureHistoryService.Instance.GetItems().ToList();
        if (targets.Count == 0)
        {
            HistoryStatusText.Text = "历史目录已经是空的。";
            return;
        }

        var totalBytes = targets.Sum(i => i.SizeBytes);
        var size = totalBytes >= 1024L * 1024 ? $"{totalBytes / 1024.0 / 1024.0:0.0} MB" : $"{totalBytes / 1024.0:0} KB";
        var categories = targets.Select(i => i.CategoryKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        _clearHistoryBusy = true;
        try
        {
            var confirmed = await Views.ConfirmDialog.ShowAsync(
                (UIElement)Content,
                "清空全部截图历史",
                $"将永久删除 data\\history 里全部 {categories} 个分类的 {targets.Count} 张截图（共 {size}），包括历史窗列表里没显示出来的较早截图。\n\n删除后无法恢复；确认期间新产生的截图不会被删除。",
                $"删除 {targets.Count} 张");
            if (!confirmed)
            {
                HistoryStatusText.Text = "已取消清空。";
                return;
            }

            var cleared = CaptureHistoryService.Instance.DeleteMany(targets);
            RefreshHistoryStatus();
            if (_historyWindow is not null)
                _ = _historyWindow.RefreshAsync();

            HistoryStatusText.Text = cleared == targets.Count
                ? $"已清空 {cleared} 张历史截图。"
                : $"已删除 {cleared} 张历史截图，另有 {targets.Count - cleared} 张已不存在或删除失败（详见日志）。";
        }
        finally
        {
            _clearHistoryBusy = false;
        }
    }


    // 冒烟（--smoke）绝不创建托盘对象：H.NotifyIcon 的 TaskbarIcon 自己在 Loaded 里 ForceCreate，
    // 只把元素留在可视树里挡不住；所以原 XAML 元素改为这里按需创建，冒烟根本不构造它。
    private TaskbarIcon TrayIcon = null!;
    private MenuFlyout TrayMenu = null!;
    private MenuFlyoutItem TrayPinItem = null!;
    private MenuFlyoutItem DelayCaptureItem = null!;
    private MenuFlyoutItem CancelDelayItem = null!;

    private void InitTray()
    {
        // 托盘对象本身的创建与入树属于 MainWindow 构造的一部分：失败必须抛给构造调用者，
        // 由 App.OnLaunched 的既有关闭漏斗（ShowFatal + Exit）失败关闭，不能吞异常后继续启动。
        // 否则五个托盘字段会停在 null!：OnLoaded 的 UpdateCaptureOptions 必 NRE（中断启动
        // 初始化链），或延时截图在 try 外 NRE 让 _delayCts 永久非空、截图热键楔死。
        // HEAD 由 InitializeComponent 构造托盘元素，同类失败也是从构造函数抛出的失败关闭。
        CreateTrayIcon();

        // 以下沿用 HEAD 的既有容错边界（原 try 内只有这五步）：ForceCreate、左右键命令、
        // 提示复位与 Opening 日志失败只记日志，保留“托盘不可用但程序仍可启动”的降级。
        try
        {
            TrayIcon.ForceCreate();
            TrayIcon.LeftClickCommand = new DelegateCommand(ShowFromTray);
            TrayIcon.DoubleClickCommand = new DelegateCommand(ShowFromTray);
            ResetTrayToolTip();
            TrayMenu.Opening += (_, _) => App.Log("Tray: menu opening");
            App.Log("Tray: 初始化完成");
        }
        catch (Exception ex)
        {
            App.Log($"Tray: 初始化失败 {ex}");
        }
    }

    /// <summary>
    /// 按原 XAML 的文本、顺序、事件与初始可见性重建托盘图标和右键菜单；只在正常模式调用。
    /// 加进根 Grid 末尾，与原 XAML 的树位置一致（TaskbarIcon 的 Loaded 仍会再 ForceCreate）。
    /// 菜单、菜单项、图标全部先在局部构造，入树成功后一次性发布到字段：任一环节抛异常时
    /// 五个字段都还没发布，不会留下被后续代码当成有效值的半构造状态；异常不在此捕获，
    /// 交由 MainWindow 的构造调用者按启动失败处理。
    /// </summary>
    private void CreateTrayIcon()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(TrayMenuItem("设置", ShowSettings_Click));
        var pinItem = TrayMenuItem("从剪贴板贴图", PastePin_Click);
        menu.Items.Add(pinItem);
        menu.Items.Add(TrayMenuItem("从剪贴板识别文字", OcrClipboard_Click));
        menu.Items.Add(TrayMenuItem("全屏截图到剪贴板", FullScreen_Click));
        var delayItem = TrayMenuItem("延时截图", DelayCapture_Click);
        menu.Items.Add(delayItem);
        var cancelItem = TrayMenuItem("取消延时截图", CancelDelay_Click);
        cancelItem.Visibility = Visibility.Collapsed;
        menu.Items.Add(cancelItem);
        menu.Items.Add(TrayMenuItem("截图历史…", History_Click));
        menu.Items.Add(TrayMenuItem("屏幕贴图管理…", OpenPinManager_Click));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(TrayMenuItem("退出", Exit_Click));

        var icon = new TaskbarIcon
        {
            ToolTipText = "实践小梅IshWY — 截图工具",
            ContextMenuMode = ContextMenuMode.SecondWindow,
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/app.ico")),
            ContextFlyout = menu,
        };

        RootGrid.Children.Add(icon);

        // 入树也成功后才发布字段；字段赋值本身不会抛异常，发布是原子的。
        TrayMenu = menu;
        TrayPinItem = pinItem;
        DelayCaptureItem = delayItem;
        CancelDelayItem = cancelItem;
        TrayIcon = icon;
    }

    private static MenuFlyoutItem TrayMenuItem(string text, RoutedEventHandler click)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += click;
        return item;
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
            var result = await Services.PinService.PasteByBehaviorAsync();
            SetStatus(result.Message);
            if (!result.Success)
                Services.ToastService.ShowInfo(result.Message);
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
                SetStatus("剪贴板里没有图片可供识别");
                return;
            }
            // 不把设置窗设为 OCR 的 owner：从后台/托盘触发 OCR 时，
            // 设置窗会被从属窗口的激活链一并拉起。让结果窗独立置顶即可。
            await Services.OcrUiService.RecognizeAndShowAsync(bytes);
        }
        catch (Exception ex)
        {
            App.Log($"剪贴板 OCR 异常: {ex}");
        }
    }

    private async void OnPinHotkeyPressed()
    {
        if (Services.CaptureService.Instance.IsOverlayCapturing)
            return;
        if (Interlocked.Exchange(ref _pinHotkeyBusy, 1) != 0)
            return;

        try
        {
            App.Log("Pin: hotkey pressed");
            var result = await Services.PinService.PasteByBehaviorAsync();
            if (!result.Success)
            {
                SetStatus(result.Message);
                Services.ToastService.ShowInfo(result.Message);
            }
            else
            {
                SetStatus(result.Message);
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
        if (Services.CaptureService.Instance.IsOverlayCapturing)
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
                SetStatus(message);
                Services.ToastService.ShowInfo(message);
                return;
            }

            // 同上：全局 OCR 热键不应把设置窗带到前台。
            await Services.OcrUiService.RecognizeAndShowAsync(bytes);
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

    private async void Exit_Click(object sender, RoutedEventArgs e)
    {
        // 退出漏斗只进入一次；第二次点击直接返回，避免重复 Dispose/ExitProcess。
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
            return;

        App.Log("Tray: exit clicked");
        try
        {
            // 首个 await 前同步封口：服务开始拒绝新会话，并取消正在等待的延时截图。
            Services.CaptureService.Instance.BeginShutdown();
            _delayCts?.Cancel();
            // 只等待服务自己的收尾；不额外加超时或重试。
            await Services.CaptureService.Instance.ShutdownAsync();
        }
        catch (Exception ex)
        {
            App.Log($"Tray: exit shutdown failed {ex}");
        }
        finally
        {
            TrayIcon.Dispose();
            _hotkey.Dispose();
            // 同 --smoke：Application.Exit 不可靠，直接 Win32 终止
            Windows.Win32.PInvoke.ExitProcess(0);
        }
    }

}
