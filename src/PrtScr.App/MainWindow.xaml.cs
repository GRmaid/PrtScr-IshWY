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
    private bool _suppressHistoryLimit;
    private bool _suppressPinInitialScale;
    private bool _suppressPinBehavior;
    private bool _suppressPinTextColor;
    private bool _suppressCaptureOptions;
    private bool _suppressProfile;
    private bool _suppressSelectionColor;
    private bool _suppressToastSettings;
    private bool _suppressPinCropToggle;
    private string? _runtimeCaptureProfile;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _memoryTimer;
    private nint[] _historyLoweredPins = [];
    private nint[] _pinManagerLoweredPins = [];
    private CancellationTokenSource? _delayCts;
    private int _ocrProbeBusy;
    private Views.HistoryWindow? _historyWindow;
    private Views.PinManagerWindow? _pinManagerWindow;

    public MainWindow(bool startHidden = false)
    {
        _startHidden = startHidden;
        App.Log($"MainWindow: ctor start (startHidden={_startHidden})");
        InitializeComponent();
        App.Log("MainWindow: InitializeComponent done");
        Title = $"{AppInfo.Name} 设置";

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        WindowAppearance.ApplyTitleBar(this, (FrameworkElement)Content);

        // 设置窗 Esc = 收进托盘（与点关闭一致）
        var interceptor = new Services.KeyInterceptor(WinRT.Interop.WindowNative.GetWindowHandle(this));
        interceptor.KeyDown += vk =>
        {
            if (vk == 0x1B && !_clearHistoryBusy &&
                Environment.TickCount64 - Services.OcrUiService.LastWindowClosedTick > 2500)
                AppWindow.Hide();
        };
        // 860×990 是主人验收过的默认尺寸；小屏（如 1366×768）上夹到工作区内，避免底部被任务栏挡住。
        var workArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min(860, workArea.Width - 40), Math.Min(990, workArea.Height - 40)));

        // 托盘常驻：点关闭 = 隐藏到托盘，真正退出走托盘菜单
        AppWindow.Closing += (sender, e) =>
        {
            e.Cancel = true;
            sender.Hide();
        };

        InitTray();
        SettingsNav.SelectedItem = HomeNavItem;
        ((FrameworkElement)Content).Loaded += OnLoaded;

        ApplyBackgroundFromConfig();
        InitHotkey();
        InitFullHotkey();
        InitPinOcrHotkeys();
        InitModeHotkey();
        InitWindowSwitchHotkeys();
        SyncHotkeyTexts();
        CaptureHistoryService.Instance.HistoryChanged += OnHistoryChanged;
        PinService.HistoryRequested = ShowHistoryAbovePins;
        AboutText.Text = $"{AppInfo.Name} v{typeof(App).Assembly.GetName().Version} · {AppInfo.Tagline} · 分窗口截屏截图工具";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
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

        _suppressHistoryLimit = true;
        HistoryLimitBox.Value = CaptureHistoryService.PerCategoryLimit;
        _suppressHistoryLimit = false;

        _suppressPinCropToggle = true;
        HistoryPinCropToggle.IsOn = App.Config.Current.HistoryPinCrop;
        _suppressPinCropToggle = false;
        RefreshHistoryStatus();

        _suppressCaptureOptions = true;
        DelayCombo.SelectedIndex = DelayToIndex(App.Config.Current.CaptureDelaySeconds);
        CaptureCursorToggle.IsOn = App.Config.Current.CaptureCursor;
        GameFullscreenQuickToggle.IsOn = App.Config.Current.GameFullscreenQuickCapture;
        DefaultModeCombo.SelectedIndex = IsNormalDefaultMode() ? 1 : 0;
        _suppressCaptureOptions = false;

        _suppressPinInitialScale = true;
        PinInitialScaleBox.Value = Math.Clamp(App.Config.Current.PinInitialScale, 80, 100);
        _suppressPinInitialScale = false;
        UpdatePinInitialScaleHint();
        LoadPinBehaviorSettings();

        _suppressProfile = true;
        CaptureProfileCombo.SelectedIndex = IsQuickProfile() ? 1 : 0;
        _suppressProfile = false;
        UpdateCaptureOptions();
        LoadSelectionColorSettings();
        LoadToastSettings();

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
        StartMemoryTimer();
    }

    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
            ShowSection(tag);
    }

    /// <summary>设置窗一次只显示一个分组；所有控件仍在同一棵 XAML 树里，初始化与事件不受影响。</summary>
    private void ShowSection(string tag)
    {
        // 录制中的热键按钮离开页面后收不到按键，先恢复成「修改」，避免停在「按键…」。
        if (_recordingButton is not null)
            ResetHotkeyRecording(_recordingButton);

        FrameworkElement[] sections =
        [
            HomeSection, HotkeysSection, CaptureSection, PinSection, ToastSection,
            HistorySection, StartupSection, AppearanceSection, OcrSection, AboutSection,
        ];
        string[] tags = ["home", "hotkeys", "capture", "pin", "toast", "history", "startup", "appearance", "ocr", "about"];
        for (var i = 0; i < sections.Length; i++)
            sections[i].Visibility = tags[i] == tag ? Visibility.Visible : Visibility.Collapsed;

        ContentScroll.ChangeView(null, 0, null, true);
    }

    /// <summary>每 60 秒记一次工作集/私有内存/托管堆，主人长测后可对照操作时间线。</summary>
    private void StartMemoryTimer()
    {
        if (_memoryTimer is not null)
            return;
        _memoryTimer = DispatcherQueue.CreateTimer();
        _memoryTimer.Interval = TimeSpan.FromSeconds(60);
        _memoryTimer.IsRepeating = true;
        _memoryTimer.Tick += (_, _) => MemoryProbe.Log("periodic");
        _memoryTimer.Start();
        MemoryProbe.Log("startup");
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

    private void HistoryPinCropToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressPinCropToggle || HistoryPinCropToggle is null)
            return;
        App.Config.Current.HistoryPinCrop = HistoryPinCropToggle.IsOn;
        App.Config.Save();
        RefreshHistoryStatus();
        SetStatus(HistoryPinCropToggle.IsOn
            ? "贴图裁剪结果会写入截图历史的「贴图裁剪」分类。"
            : "贴图裁剪结果不再写入截图历史。");
        App.Log($"History: pinCrop = {HistoryPinCropToggle.IsOn}");
    }

    private void OnHistoryChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshHistoryStatus();
            RefreshOpenHistoryWindow();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshHistoryStatus();
                RefreshOpenHistoryWindow();
            });
        }
    }

    /// <summary>历史窗口开着时，截图新增/删除/清空也立即刷新列表，不必重新打开窗口。</summary>
    private void RefreshOpenHistoryWindow()
    {
        if (_historyWindow is not null)
            _ = _historyWindow.RefreshAsync();
    }

    private void RefreshHistoryStatus()
    {
        if (!App.Config.Current.SaveHistory)
        {
            HistoryStatusText.Text = "已关闭：截图只进剪贴板，不写入本机历史。";
            HistoryHomeSummaryText.Text = "保存已关闭：截图只进剪贴板，不写入本机历史。";
            return;
        }

        var count = CaptureHistoryService.Instance.GetItems().Count;
        var limit = CaptureHistoryService.PerCategoryLimit;
        HistoryStatusText.Text = $"已开启：窗口截图按进程分栏，常规、早期未分类各一类；贴图裁剪{(App.Config.Current.HistoryPinCrop ? "单独成类" : "不收录")}。每类保留最近 {limit} 张（data\\history，当前共 {count} 张）。";
        HistoryHomeSummaryText.Text = count == 0
            ? "还没有截图；按截图热键后会自动出现在这里。"
            : $"{count} 张 · 窗口截图按进程分栏 · 每类保留 {limit} 张";
    }

    /// <summary>首页状态只显示通用动作反馈；快捷键页的反馈单独走 InfoBar，避免串台。</summary>
    private void SetStatus(string message)
    {
        if (HomeStatusText is not null)
            HomeStatusText.Text = message;
    }

    private void SetHotkeyStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        HotkeyStatusBar.Severity = severity;
        HotkeyStatusBar.Message = message;
        HotkeyStatusBar.IsOpen = !string.IsNullOrWhiteSpace(message);
    }

    private void HistoryLimit_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressHistoryLimit || HistoryLimitBox is null)
            return;

        var value = double.IsNaN(args.NewValue) ? 30 : (int)Math.Clamp(args.NewValue, 1, 200);
        if (App.Config.Current.HistoryPerCategory == value)
            return;

        App.Config.Current.HistoryPerCategory = value;
        App.Config.Save();

        // 调小上限立即回收旧文件；调大只影响以后新增，不主动恢复已删内容。
        CaptureHistoryService.Instance.PruneNow(value);
        RefreshHistoryStatus();
        _ = _historyWindow?.RefreshAsync();
        App.Log($"History: perCategory={value}");
    }

}
