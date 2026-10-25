using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using PrtScr.App.Services;
using System.IO;
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
        DefaultModeCombo.SelectedIndex = IsNormalDefaultMode() ? 1 : 0;
        _suppressCaptureOptions = false;

        _suppressPinInitialScale = true;
        PinInitialScaleBox.Value = Math.Clamp(App.Config.Current.PinInitialScale, 80, 100);
        _suppressPinInitialScale = false;
        UpdatePinInitialScaleHint();

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

    /// <summary>设置页与首页共用一条可读状态，避免动作反馈只出现在折叠区里。</summary>
    private void SetStatus(string message)
    {
        HotkeyStatusText.Text = message;
        if (HomeStatusText is not null)
            HomeStatusText.Text = message;
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

    private void PinInitialScale_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressPinInitialScale || PinInitialScaleBox is null)
            return;

        var value = double.IsNaN(args.NewValue) ? 100 : (int)Math.Clamp(args.NewValue, 80, 100);
        if (double.IsNaN(PinInitialScaleBox.Value) || Math.Abs(PinInitialScaleBox.Value - value) > 0.1)
        {
            _suppressPinInitialScale = true;
            PinInitialScaleBox.Value = value;
            _suppressPinInitialScale = false;
        }

        if (App.Config.Current.PinInitialScale == value)
        {
            UpdatePinInitialScaleHint();
            return;
        }

        App.Config.Current.PinInitialScale = value;
        App.Config.Save();
        UpdatePinInitialScaleHint();
        SetStatus($"新贴图初始大小：原图 {value}%");
        App.Log($"Pin: initialScale={value}%");
    }

    private void UpdatePinInitialScaleHint()
    {
        if (PinInitialScaleHintText is null)
            return;
        var value = Math.Clamp(App.Config.Current.PinInitialScale, 80, 100);
        PinInitialScaleHintText.Text =
            $"可调范围 80–100；80% 更小，100% 按原图大小。新贴图与切换到的历史图片按原图 {value}% 起步；超出工作区会自动缩小。桌面已有贴图不变，滚轮仍可缩放。";
    }

    private void DelayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCaptureOptions || DelayCombo is null)
            return;

        var seconds = DelayFromIndex(DelayCombo.SelectedIndex);
        if (App.Config.Current.CaptureDelaySeconds == seconds)
            return;

        App.Config.Current.CaptureDelaySeconds = seconds;
        App.Config.Save();
        UpdateCaptureOptions();
        App.Log($"CaptureOptions: delay={seconds}s");
    }

    private string CurrentCaptureProfile =>
        _runtimeCaptureProfile ?? App.Config.Current.CaptureProfile;

    private bool IsQuickProfile() =>
        string.Equals(CurrentCaptureProfile, "quick", StringComparison.OrdinalIgnoreCase);

    private void ResetModeDefaults_Click(object sender, RoutedEventArgs e)
    {
        var config = App.Config.Current;
        config.CaptureProfile = "normal";
        config.DefaultCaptureMode = "normal";
        App.Config.Save();

        _runtimeCaptureProfile = null;
        _suppressProfile = true;
        CaptureProfileCombo.SelectedIndex = 0;
        _suppressProfile = false;
        _suppressCaptureOptions = true;
        DefaultModeCombo.SelectedIndex = 1;
        _suppressCaptureOptions = false;
        UpdateCaptureOptions();
        SetStatus("截图档位与取景起始模式已恢复默认：常规取景 + 常规截图。");
        App.Log("CaptureOptions: 恢复默认模式");
    }

    private void LoadSelectionColorSettings()
    {
        var config = App.Config.Current;
        var color = NormalizeHexColor(config.SelectionBorderColor) ?? "#FFFFFF";
        var follow = config.SelectionBorderFollowTheme;
        var displayColor = follow ? Services.ThemeColorService.AccentHex : color;
        _suppressSelectionColor = true;
        SelectionColorFollowThemeCheckBox.IsChecked = follow;
        SelectionColorBox.Text = displayColor;
        SelectionColorBox.IsEnabled = !follow;
        SelectionColorPresetCombo.IsEnabled = !follow;
        ApplySelectionColorPreview(displayColor);
        SelectionColorPresetCombo.SelectedIndex = follow ? -1 : PresetIndex(color);
        _suppressSelectionColor = false;
        UpdateSelectionColorHint(follow, displayColor);
    }

    private void SelectionColorFollowTheme_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectionColor || SelectionColorFollowThemeCheckBox is null)
            return;

        var follow = SelectionColorFollowThemeCheckBox.IsChecked == true;
        var config = App.Config.Current;
        SelectionColorBox.IsEnabled = !follow;
        SelectionColorPresetCombo.IsEnabled = !follow;
        var color = NormalizeHexColor(config.SelectionBorderColor) ?? "#FFFFFF";
        var displayColor = follow ? Services.ThemeColorService.AccentHex : color;
        _suppressSelectionColor = true;
        SelectionColorBox.Text = displayColor;
        SelectionColorPresetCombo.SelectedIndex = follow ? -1 : PresetIndex(color);
        _suppressSelectionColor = false;
        ApplySelectionColorPreview(displayColor);
        UpdateSelectionColorHint(follow, displayColor);
        if (config.SelectionBorderFollowTheme == follow)
            return;

        config.SelectionBorderFollowTheme = follow;
        App.Config.Save();
        SetStatus(follow ? "选区边框颜色已改为跟随系统主题色。" : $"选区边框颜色改为手动颜色 {color}。");
        App.Log($"Overlay: selectionFollowTheme={follow}");
    }

    private void SelectionColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSelectionColor || SelectionColorBox is null ||
            SelectionColorFollowThemeCheckBox?.IsChecked == true)
            return;

        var normalized = NormalizeHexColor(SelectionColorBox.Text);
        if (normalized is null)
        {
            SelectionColorHintText.Text = "颜色格式应为 #RRGGBB（例如 #FFFFFF）；格式不对不会保存。";
            return;
        }

        ApplySelectionColorPreview(normalized);
        if (string.Equals(App.Config.Current.SelectionBorderColor, normalized, StringComparison.OrdinalIgnoreCase))
        {
            UpdateSelectionColorHint(false, normalized);
            return;
        }

        App.Config.Current.SelectionBorderColor = normalized;
        App.Config.Save();
        _suppressSelectionColor = true;
        SelectionColorPresetCombo.SelectedIndex = PresetIndex(normalized);
        _suppressSelectionColor = false;
        UpdateSelectionColorHint(false, normalized);
        SetStatus($"选区边框颜色已改为 {normalized}。");
        App.Log($"Overlay: selectionColor={normalized}");
    }

    private void SelectionColorPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionColor || SelectionColorPresetCombo?.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        SelectionColorBox.Text = tag;
    }

    private void ApplySelectionColorPreview(string color)
    {
        var parsed = ParseHexColor(color);
        SelectionColorPreview.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            parsed ?? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    private void UpdateSelectionColorHint(bool follow, string color)
    {
        SelectionColorHintText.Text = follow
            ? $"当前跟随系统主题色（{Services.ThemeColorService.AccentHex}）。关闭「跟随主题色」后可手动选色；边框和八个缩放手柄会一起变色。"
            : $"当前 {color}。浅色画面可改深灰，深色画面可改亮色；边框和八个缩放手柄会一起变色。";
    }

    private static int PresetIndex(string color)
    {
        var presets = new[] { "#FFFFFF", "#00C8FF", "#3EF08A", "#FFD54A", "#FF8A3D", "#FF5FD2", "#202020" };
        var index = Array.FindIndex(presets, p => string.Equals(p, color, StringComparison.OrdinalIgnoreCase));
        return index;
    }

    private static string? NormalizeHexColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim();
        if (!value.StartsWith('#'))
            value = "#" + value;
        if (value.Length != 7 || !uint.TryParse(value[1..],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return null;
        return value.ToUpperInvariant();
    }

    private static Windows.UI.Color? ParseHexColor(string? text)
    {
        var normalized = NormalizeHexColor(text);
        if (normalized is null)
            return null;
        var rgb = uint.Parse(normalized[1..], System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
        return Windows.UI.Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private void LoadToastSettings()
    {
        var config = App.Config.Current;
        _suppressToastSettings = true;
        ToastFontSizeBox.Value = Math.Clamp(config.ToastFontSize, 11, 32);
        ToastVisibleMsBox.Value = Math.Clamp(config.ToastVisibleMs, 500, 10000);
        ToastFadeInMsBox.Value = Math.Clamp(config.ToastFadeInMs, 0, 1000);
        ToastFadeOutMsBox.Value = Math.Clamp(config.ToastFadeOutMs, 0, 2000);
        ToastPositionCombo.SelectedIndex = ToastPositionIndex(config.ToastPosition);
        ToastProfileToggle.IsOn = config.ToastProfileEnabled;
        ToastProfileTextBox.Text = config.ToastProfileText;
        ToastSuccessToggle.IsOn = config.ToastSuccessEnabled;
        ToastSuccessTextBox.Text = config.ToastSuccessText;
        ToastFailureToggle.IsOn = config.ToastFailureEnabled;
        ToastFailureTextBox.Text = config.ToastFailureText;
        _suppressToastSettings = false;
        UpdateToastSettingsHint();
    }

    private static int ToastPositionIndex(string? position)
    {
        var positions = new[] { "top-left", "top-center", "top-right", "middle-left", "center", "middle-right", "bottom-left", "bottom-center", "bottom-right", "custom" };
        var index = Array.IndexOf(positions, position ?? "");
        return index < 0 ? 7 : index;
    }

    private void ToastFontSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressToastSettings || ToastFontSizeBox is null)
            return;
        if (double.IsNaN(args.NewValue))
        {
            _suppressToastSettings = true;
            ToastFontSizeBox.Value = App.Config.Current.ToastFontSize;
            _suppressToastSettings = false;
            return;
        }

        var size = Math.Clamp((int)Math.Round(args.NewValue), 11, 32);
        App.Config.Current.ToastFontSize = size;
        App.Config.Save();
        UpdateToastSettingsHint();
        App.Log($"Toast: fontSize={size}");
    }

    private void ToastTiming_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressToastSettings || ToastVisibleMsBox is null || ToastFadeInMsBox is null || ToastFadeOutMsBox is null)
            return;
        if (double.IsNaN(args.NewValue))
        {
            LoadToastSettings();
            return;
        }

        var config = App.Config.Current;
        config.ToastVisibleMs = ClampToastNumber(ToastVisibleMsBox.Value, 500, 10000, config.ToastVisibleMs);
        config.ToastFadeInMs = ClampToastNumber(ToastFadeInMsBox.Value, 0, 1000, config.ToastFadeInMs);
        config.ToastFadeOutMs = ClampToastNumber(ToastFadeOutMsBox.Value, 0, 2000, config.ToastFadeOutMs);
        App.Config.Save();
        UpdateToastSettingsHint();
        App.Log($"Toast: timing visible={config.ToastVisibleMs} fadeIn={config.ToastFadeInMs} fadeOut={config.ToastFadeOutMs}");
    }

    private static int ClampToastNumber(double value, int min, int max, int fallback) =>
        double.IsNaN(value) ? fallback : Math.Clamp((int)Math.Round(value), min, max);

    private void ToastPosition_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToastSettings || ToastPositionCombo?.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        if (string.Equals(tag, "custom", StringComparison.Ordinal))
        {
            // 自定义位置要等编辑器保存后才写入配置；取消时重新载入原位置，不留下半截状态。
            OpenToastPlacementEditor();
            return;
        }
        if (string.Equals(App.Config.Current.ToastPosition, tag, StringComparison.Ordinal))
            return;
        App.Config.Current.ToastPosition = tag;
        App.Config.Save();
        UpdateToastSettingsHint();
        App.Log($"Toast: position={tag}");
    }

    private void ToastProfileToggle_Toggled(object sender, RoutedEventArgs e) =>
        SaveToastToggle(ref _suppressToastSettings);

    private void ToastSuccessToggle_Toggled(object sender, RoutedEventArgs e) =>
        SaveToastToggle(ref _suppressToastSettings);

    private void ToastFailureToggle_Toggled(object sender, RoutedEventArgs e) =>
        SaveToastToggle(ref _suppressToastSettings);

    private void SaveToastToggle(ref bool suppress)
    {
        if (suppress || ToastProfileToggle is null)
            return;
        var config = App.Config.Current;
        config.ToastProfileEnabled = ToastProfileToggle.IsOn;
        config.ToastSuccessEnabled = ToastSuccessToggle.IsOn;
        config.ToastFailureEnabled = ToastFailureToggle.IsOn;
        App.Config.Save();
        UpdateToastSettingsHint();
    }

    private void ToastProfileText_TextChanged(object sender, TextChangedEventArgs e) =>
        SaveToastText();

    private void ToastSuccessText_TextChanged(object sender, TextChangedEventArgs e) =>
        SaveToastText();

    private void ToastFailureText_TextChanged(object sender, TextChangedEventArgs e) =>
        SaveToastText();

    private void SaveToastText()
    {
        if (_suppressToastSettings || ToastProfileTextBox is null)
            return;
        var config = App.Config.Current;
        config.ToastProfileText = ToastProfileTextBox.Text;
        config.ToastSuccessText = ToastSuccessTextBox.Text;
        config.ToastFailureText = ToastFailureTextBox.Text;
        App.Config.Save();
        UpdateToastSettingsHint();
    }

    private void UpdateToastSettingsHint()
    {
        var config = App.Config.Current;
        var position = string.Equals(config.ToastPosition, "custom", StringComparison.Ordinal)
            ? $"自定义（{config.ToastCustomX:0},{config.ToastCustomY:0} · {config.ToastCustomWidth:0}×{config.ToastCustomHeight:0}）"
            : "九宫格位置";
        var material = Services.ToastAppearance.MaterialLabel(config.ToastMaterial);
        var opacity = Math.Round(Services.ToastAppearance.ClampOpacity(config.ToastOpacity) * 100);
        ToastSettingsHintText.Text =
            $"提示立即生效；文字为空或该时机关闭时就不显示。当前位置：{position}；材质 {material} · 不透明度 {opacity}%；显示 {Math.Clamp(config.ToastVisibleMs, 500, 10000)}ms（淡入 {Math.Clamp(config.ToastFadeInMs, 0, 1000)}ms / 淡出 {Math.Clamp(config.ToastFadeOutMs, 0, 2000)}ms）；档位切换 {(config.ToastProfileEnabled ? "开" : "关")}、截图成功 {(config.ToastSuccessEnabled ? "开" : "关")}、截图失败 {(config.ToastFailureEnabled ? "开" : "关")}。";
    }

    private void EditToastPlacement_Click(object sender, RoutedEventArgs e) => OpenToastPlacementEditor();

    private void OpenToastPlacementEditor()
    {
        Views.ToastPlacementWindow.Open(
            () =>
            {
                LoadToastSettings();
                ToastService.Preview("这是一条截图提示预览");
                SetStatus("提示位置已保存，并已按新位置预览。");
            },
            LoadToastSettings);
    }

    private void PreviewToast_Click(object sender, RoutedEventArgs e)
    {
        ToastService.Preview("这是一条截图提示预览");
    }

    private void ResetToastDefaults_Click(object sender, RoutedEventArgs e)
    {
        var config = App.Config.Current;
        config.ToastFontSize = 14;
        config.ToastPosition = "bottom-center";
        config.ToastMaterial = "acrylic";
        config.ToastOpacity = 0.9;
        config.ToastFadeInMs = 60;
        config.ToastVisibleMs = 3200;
        config.ToastFadeOutMs = 350;
        config.ToastProfileEnabled = true;
        config.ToastProfileText = "已切换为「{mode}」";
        config.ToastSuccessEnabled = true;
        config.ToastSuccessText = "截图已复制到剪贴板";
        config.ToastFailureEnabled = true;
        config.ToastFailureText = "截图失败，请查看日志";
        App.Config.Save();
        LoadToastSettings();
        SetStatus("提示设置已恢复默认。");
    }

    private void CaptureProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfile || CaptureProfileCombo is null)
            return;

        var profile = CaptureProfileCombo.SelectedIndex == 1 ? "quick" : "normal";
        _runtimeCaptureProfile = null;
        if (string.Equals(App.Config.Current.CaptureProfile, profile, StringComparison.OrdinalIgnoreCase))
        {
            // 配置没变，但热键临时切换过的本次档位已被清掉，主页与提示也要跟着回到实际档位。
            UpdateCaptureOptions();
            return;
        }

        App.Config.Current.CaptureProfile = profile;
        App.Config.Save();
        UpdateCaptureOptions();
        App.Log($"CaptureProfile: default={profile}");
    }

    /// <summary>
    /// 切换档位热键（默认 Alt+Shift+C）：立即切换本次运行使用的截图档位。
    /// 不排队、不写配置——设置里的下拉框仍作为下次启动的默认档位。
    /// </summary>
    private void ToggleCaptureProfile()
    {
        var next = IsQuickProfile() ? "normal" : "quick";
        _runtimeCaptureProfile = next;
        SetProfileCombo(next);
        UpdateCaptureOptions();

        var label = string.Equals(next, "quick", StringComparison.OrdinalIgnoreCase) ? "快捷全屏" : "常规取景";
        SetStatus($"截图档位已切换为「{label}」，下一次截图立即生效");
        ToastService.ShowProfile(label);
        App.Log($"CaptureProfile: toggled to {next} (runtime, next launch={App.Config.Current.CaptureProfile})");
    }

    private void SetProfileCombo(string profile)
    {
        _suppressProfile = true;
        CaptureProfileCombo.SelectedIndex = string.Equals(profile, "quick", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _suppressProfile = false;
    }

    private static bool IsNormalDefaultMode() =>
        string.Equals(App.Config.Current.DefaultCaptureMode, "normal", StringComparison.OrdinalIgnoreCase);

    private void DefaultModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCaptureOptions || DefaultModeCombo is null)
            return;

        var mode = DefaultModeCombo.SelectedIndex == 1 ? "normal" : "window";
        if (string.Equals(App.Config.Current.DefaultCaptureMode, mode, StringComparison.OrdinalIgnoreCase))
            return;

        App.Config.Current.DefaultCaptureMode = mode;
        App.Config.Save();
        UpdateCaptureOptions();
        App.Log($"CaptureOptions: defaultMode={mode}");
    }

    private void CaptureCursorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressCaptureOptions || CaptureCursorToggle is null)
            return;

        var enabled = CaptureCursorToggle.IsOn;
        if (App.Config.Current.CaptureCursor == enabled)
            return;

        App.Config.Current.CaptureCursor = enabled;
        App.Config.Save();
        UpdateCaptureOptions();
        App.Log($"CaptureOptions: cursor={enabled}");
    }

    private static int DelayToIndex(int seconds) => seconds switch
    {
        3 => 1,
        5 => 2,
        10 => 3,
        _ => 0,
    };

    private static int DelayFromIndex(int index) => index switch
    {
        1 => 3,
        2 => 5,
        3 => 10,
        _ => 0,
    };

    private static int NormalizeDelay(int seconds) => seconds is 3 or 5 or 10 ? seconds : 0;

    private void UpdateCaptureOptions()
    {
        var delay = NormalizeDelay(App.Config.Current.CaptureDelaySeconds);
        var cursor = App.Config.Current.CaptureCursor;

        DelayCaptureItem.Text = delay > 0 ? $"延时截图（{delay} 秒）" : "延时截图（3 秒）";
        CaptureOptionsText.Text = delay > 0
            ? $"当前：按下截图热键后等待 {delay} 秒再冻结画面；等待期间再按一次截图热键可取消。"
            : "当前：截图热键立即冻结画面；托盘菜单的「延时截图」默认等 3 秒。";
        CaptureOptionsText.Text += cursor ? " 截图会包含鼠标指针。" : " 截图不包含鼠标指针。";

        var quick = IsQuickProfile();
        DefaultModeCombo.IsEnabled = !quick;
        UpdateProfileHint();
        var modeKey = ModeSwitchKeyText();
        HomeStatusText.Text = quick
            ? $"提示：当前为快捷全屏档，截图热键会直接截取整屏{(modeKey is null ? "。" : $"；{modeKey} 可切回常规取景。")}"
            : "提示：截图热键随时可用；取景中按 Tab 切换窗口截图 / 常规截图，回车截取当前窗口。";

        var normal = IsNormalDefaultMode();
        CaptureModeHintText.Text = quick
            ? "常规取景的起始模式将在切回常规后生效。"
            : normal
                ? "当前常规起始：常规截图。看到什么就截什么；按截图热键后按 Tab 可切到窗口截图。"
                : "当前常规起始：窗口截图。会重建窗口本体，抗遮挡；按截图热键后按 Tab 可切到常规截图。";
    }

    /// <summary>切换档位热键的当前文案；被删除时返回 null，提示里不再写死 Alt+Shift+C。</summary>
    private string? ModeSwitchKeyText()
    {
        var text = ModeHotkeyText.Text;
        return string.IsNullOrEmpty(text) || text == "未设置" ? null : text;
    }

    private void UpdateProfileHint()
    {
        var modeKey = ModeSwitchKeyText();
        CaptureProfileHintText.Text = (IsQuickProfile()
            ? "当前：快捷全屏。按截图热键立即抓整屏、复制到剪贴板并写入历史，不出取景器；"
              + (modeKey is null ? "切换档位热键未设置，可用上方下拉框切回常规取景。" : $"{modeKey} 可随时切回常规取景。")
            : "当前：常规取景。按截图热键冻结画面后框选或选窗；"
              + (modeKey is null ? "切换档位热键未设置，可用上方下拉框切到快捷全屏。" : $"{modeKey} 可随时切到快捷全屏。"))
            + " 下拉框是下次启动的默认档位，热键切换只影响本次运行。";
        UpdateScreenshotCard();
    }

    /// <summary>
    /// 主页截图卡片与截图热键走同一条 <see cref="OnHotkeyPressed"/>：
    /// 倒计时中再点 = 取消；快捷档位 = 直接整屏；常规档位 = 冻屏取景，起始模式看 defaultCaptureMode。
    /// 标题与说明按这条真实分支显示，不再固定写「窗口截图 / 抗遮挡」。
    /// </summary>
    private void UpdateScreenshotCard()
    {
        SetScreenshotCardText();
        // 卡片内容是自定义布局，读屏名称不会自动取到标题与说明，这里随状态同步。
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ScreenshotActionButton, $"{ScreenshotCardTitle.Text}：{ScreenshotCardDescription.Text}");
    }

    private void SetScreenshotCardText()
    {
        if (_delayCts is not null)
        {
            ScreenshotCardTitle.Text = "取消延时截图";
            ScreenshotCardDescription.Text = "正在倒计时，点这里或再按截图热键取消。";
            return;
        }

        var delay = NormalizeDelay(App.Config.Current.CaptureDelaySeconds);
        var wait = delay > 0 ? $"等 {delay} 秒后" : "";
        if (IsQuickProfile())
        {
            ScreenshotCardTitle.Text = "快捷全屏";
            ScreenshotCardDescription.Text = $"{wait}直接截取整屏并复制到剪贴板，不打开取景器。";
            return;
        }

        ScreenshotCardTitle.Text = "截图取景";
        ScreenshotCardDescription.Text = IsNormalDefaultMode()
            ? $"{wait}冻屏框选，从常规截图起步；Tab 可切到窗口截图（抗遮挡）。"
            : $"{wait}冻屏框选，从窗口截图起步（抗遮挡）；Tab 可切到常规截图。";
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

    private void InitTray()
    {
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
                SetStatus("剪贴板里没有图片可贴（先截一张图或复制一张图片）");
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
                SetStatus(message);
                Services.ToastService.ShowInfo(message);
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
            SetStatus(message);
            App.Log($"Hotkey: {message}");
        });

        Services.CaptureService.Instance.Hotkey = _hotkey;
        _hotkey.HotkeyPressed += () =>
        {
            if (!Services.EscOverPinHook.TryClaimScreenshotHotkey())
                return;
            App.Log("Hotkey: RegisterHotKey 兜底命中截图键");
            Services.EscOverPinHook.NotifyFallbackHit();
            DispatcherQueue.TryEnqueue(OnHotkeyPressed);
        };
        _hotkey.CancelPressed += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (Services.CaptureService.Instance.IsCapturing)
                Services.CaptureService.Instance.CancelCurrent();
        });

        // 旧配置没有 hotkeyVk 时按 hotkeyKey（F1…F12）回退；-1 表示用户已删除，不能再回退。
        var vk = ResolveScreenshotVk(config);
        var mods = vk == 0 ? 0 : (int)config.HotkeyModifiers;

        // 主截图热键也可能在游戏里被吞：钩子通道与 RegisterHotKey 兜底共用去重。
        Services.EscOverPinHook.SetScreenshotKey(vk, mods);
        Services.EscOverPinHook.ScreenshotPressed += () =>
        {
            // 钩子侧 EvaluateScreenshotHotkey 已经 claim 过；这里再 claim 会被 300ms 去重挡掉。
            App.Log("Hotkey: 键盘钩子捕获截图键");
            DispatcherQueue.TryEnqueue(OnHotkeyPressed);
        };

        _hotkey.Start(vk, mods);
        ScreenshotHotkeyText.Text = HotkeyService.Describe(vk, mods);
        App.Log($"Hotkey: 截图配置 {HotkeyService.Describe(vk, mods)}");
    }

    private void InitModeHotkey()
    {
        var config = App.Config.Current;
        var vk = config.ModeHotkeyVk;
        var mods = config.ModeHotkeyModifiers;

        Services.EscOverPinHook.SetModeSwitchKey(vk, mods);
        Services.EscOverPinHook.ModeSwitchPressed += () =>
        {
            // 钩子侧 EvaluateModeHotkey 已经 claim 过；二次 claim 会被去重挡掉。
            DispatcherQueue.TryEnqueue(ToggleCaptureProfile);
        };

        _hotkey.ModeHotkeyPressed += () =>
        {
            if (!Services.EscOverPinHook.TryClaimModeSwitchHotkey())
                return;
            App.Log("CaptureProfile: RegisterHotKey 兜底命中切换键");
            Services.EscOverPinHook.NotifyFallbackHit();
            DispatcherQueue.TryEnqueue(ToggleCaptureProfile);
        };

        _hotkey.SetModeKey(vk, mods);
        ModeHotkeyText.Text = HotkeyService.Describe(vk, mods);
        App.Log($"CaptureProfile: hotkey {ModeHotkeyText.Text}");
    }

    private void InitFullHotkey()
    {
        var config = App.Config.Current;

        // 全屏截图热键走全局键盘钩子（和 Esc 同一套）：游戏用 Raw Input/钩子吞键时 RegisterHotKey 会收不到。
        // 但低级钩子会被系统静默摘除（LowLevelHooksTimeout），所以再挂一条 RegisterHotKey 兜底；
        // 两条通道同时命中时用 TryClaimFullScreenHotkey 去重。
        var vk = ResolveFullscreenVk(config);
        var mods = vk == 0 ? 0 : config.FullHotkeyModifiers;
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

    /// <summary>取景器内的上一个/下一个窗口：只走低级键盘钩子，不注册全局热键。</summary>
    private void InitWindowSwitchHotkeys()
    {
        var config = App.Config.Current;
        PushWindowSwitchKeys();
        WindowPrevHotkeyText.Text = HotkeyService.Describe(config.WindowPrevKeyVk, config.WindowPrevKeyModifiers);
        WindowNextHotkeyText.Text = HotkeyService.Describe(config.WindowNextKeyVk, config.WindowNextKeyModifiers);
        App.Log($"Hotkey: 取景切窗 {WindowPrevHotkeyText.Text} / {WindowNextHotkeyText.Text}");
    }

    private static void PushWindowSwitchKeys()
    {
        var config = App.Config.Current;
        Services.EscOverPinHook.SetWindowSwitchKeys(
            config.WindowPrevKeyVk, config.WindowPrevKeyModifiers,
            config.WindowNextKeyVk, config.WindowNextKeyModifiers);
    }

    /// <summary>截图键的旧配置回退；-1 是「用户已删除」的哨兵，不能回退到 hotkeyKey。</summary>
    private static int ResolveScreenshotVk(AppConfig config) =>
        config.HotkeyVk < 0 ? 0 :
        config.HotkeyVk > 0 ? config.HotkeyVk :
        (int)HotkeyService.KeyNameToVirtualKey(config.HotkeyKey);

    /// <summary>全屏键同理：-1 表示已删除，0 才按旧字段 fullScreenHotkeyKey 回退。</summary>
    private static int ResolveFullscreenVk(AppConfig config) =>
        config.FullHotkeyVk < 0 ? 0 :
        config.FullHotkeyVk > 0 ? config.FullHotkeyVk :
        (int)HotkeyService.KeyNameToVirtualKey(config.FullScreenHotkeyKey);

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
                SetStatus("全屏截图失败");
                App.Log($"QuickCapture: 失败，无图像 (bypass={bypass.Activated})");
                ToastService.ShowCaptureFailure();
                return;
            }

            if (result.LikelyBlack)
            {
                SetStatus("当前画面像是独占全屏游戏，Windows 无法截取");
                App.Log($"QuickCapture: 两次都是黑屏 (bypass={bypass.Activated})");
                ToastService.ShowCaptureFailure();
                return;
            }

            var copyTask = Services.ClipboardService.SetImageAsync(result.Png);
            var historyTask = Services.CaptureHistoryService.Instance.AddAsync(
                result.Png, Services.HistoryCaptureMode.Normal);
            await Task.WhenAll(copyTask, historyTask);

            var ok = copyTask.Result;
            SetStatus(ok ? "全屏截图已复制到剪贴板" : "全屏截图复制失败（剪贴板被占用）");
            if (ok)
                ToastService.ShowCaptureSuccess();
            else
                ToastService.ShowCaptureFailure();
            App.Log($"QuickCapture: 完成 {result.Png.Length / 1024}KB copied={ok} bypass={bypass.Activated} history={historyTask.Result is not null}");
            if (historyTask.Result is { } savedPath)
                App.Log($"QuickCapture: history saved {savedPath}");
        }
        catch (Exception ex)
        {
            App.Log($"全屏截图异常: {ex}");
            ToastService.ShowCaptureFailure();
        }
        finally
        {
            Volatile.Write(ref _quickCaptureBusy, 0);
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => OnFullScreenPressed();

    private void CaptureNow_Click(object sender, RoutedEventArgs e) => OnHotkeyPressed();

    private async void DelayCapture_Click(object sender, RoutedEventArgs e)
    {
        var seconds = NormalizeDelay(App.Config.Current.CaptureDelaySeconds);
        if (seconds <= 0)
            seconds = 3;   // 托盘入口在未配置延时的时候默认 3 秒，保证功能可发现

        await StartDelayedCaptureAsync(seconds);
    }

    private void CancelDelay_Click(object sender, RoutedEventArgs e)
    {
        if (_delayCts is not null)
        {
            App.Log("Delay: tray cancel clicked");
            _delayCts.Cancel();
        }
    }

    private void ResetTrayToolTip() =>
        TrayIcon.ToolTipText = $"{AppInfo.Name} — 单击/双击打开设置，右键菜单";

    private void HotkeyRecord_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action)
            return;

        // 换一个录制目标时，先把上一个按钮恢复原状
        if (_recordingButton is not null && !ReferenceEquals(_recordingButton, button))
            ResetHotkeyRecording(_recordingButton);

        _recordingAction = action;
        _recordingButton = button;
        button.Content = "按键…";
        SetStatus($"正在录制「{HotkeyLabel(action)}」：请按下组合键，Esc 取消。");
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
            SetStatus("已取消录制快捷键。");
            return;
        }

        var mods = 0;
        if (IsDown(0x11)) mods |= 2;
        if (IsDown(0x12)) mods |= 1;
        if (IsDown(0x10)) mods |= 4;
        if (IsDown(0x5B) || IsDown(0x5C)) mods |= 8;

        // 取景器内的切窗键只在取景时生效，可以录成 [ / ] 这类无修饰键单键；
        // 其余全局热键仍要求修饰键（F1–F12 例外），避免裸键抢走正常输入。
        var overlayOnly = _recordingAction is "windowprev" or "windownext";
        var isFunctionKey = vk is >= 0x70 and <= 0x7B;
        if (mods == 0 && !isFunctionKey && !overlayOnly)
        {
            SetStatus("请按住 Ctrl / Alt / Shift / Win 之一（F1–F12 可单独使用）");
            return;
        }

        if (overlayOnly && (mods & (1 | 8)) != 0)
        {
            SetStatus("取景器切窗键不支持 Alt / Win 修饰（Alt 会和取景操作冲突）");
            return;
        }

        if (overlayOnly && IsReservedOverlayKey(vk, mods, out var reserved))
        {
            SetStatus($"「{reserved}」是取景器固定按键，换一个");
            return;
        }

        if (TryFindAnyHotkeyConflict(_recordingAction, vk, mods, out var other))
        {
            SetStatus($"这个组合键已经给了「{other}」，换一个");
            ResetHotkeyRecording(button);
            return;
        }

        var action = _recordingAction;
        ApplyHotkey(action, vk, mods);
        ResetHotkeyRecording(button);
    }

    private void ResetHotkeyRecording(Button button)
    {
        button.Content = "修改";
        _recordingAction = null;
        _recordingButton = null;
    }

    private static string HotkeyLabel(string action) => action switch
    {
        "screenshot" => "截图",
        "fullscreen" => "全屏截图",
        "pin" => "贴图",
        "ocr" => "文字识别",
        "mode" => "切换截图档位",
        "windowprev" => "上一个窗口",
        "windownext" => "下一个窗口",
        _ => action,
    };

    private void HotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action })
            ClearHotkey(action);
    }

    private void SetHotkeyText(string action, string text)
    {
        switch (action)
        {
            case "screenshot": ScreenshotHotkeyText.Text = text; break;
            case "fullscreen": FullHotkeyText.Text = text; break;
            case "pin": PinHotkeyText.Text = text; break;
            case "ocr": OcrHotkeyText.Text = text; break;
            case "mode": ModeHotkeyText.Text = text; break;
            case "windowprev": WindowPrevHotkeyText.Text = text; break;
            case "windownext": WindowNextHotkeyText.Text = text; break;
        }
    }

    /// <summary>删除一个全局热键。截图/全屏用 -1 区分「从未配置」和「用户已删除」，避免回退到旧 F1/F2。</summary>
    private void ClearHotkey(string action)
    {
        if (_recordingButton is not null)
            ResetHotkeyRecording(_recordingButton);

        var config = App.Config.Current;
        switch (action)
        {
            case "screenshot":
                config.HotkeyVk = -1;
                config.HotkeyModifiers = 0;
                App.Config.Save();
                Services.EscOverPinHook.SetScreenshotKey(0, 0);
                _hotkey.Restart(0, 0);
                break;

            case "fullscreen":
                config.FullHotkeyVk = -1;
                config.FullHotkeyModifiers = 0;
                App.Config.Save();
                Services.EscOverPinHook.SetFullScreenKey(0, 0);
                _hotkey.SetFullscreenKey(0, 0);
                break;

            case "pin":
                config.PinHotkeyVk = 0;
                config.PinHotkeyModifiers = 0;
                App.Config.Save();
                _hotkey.SetPinKey(0, 0);
                break;

            case "ocr":
                config.OcrHotkeyVk = 0;
                config.OcrHotkeyModifiers = 0;
                App.Config.Save();
                _hotkey.SetOcrKey(0, 0);
                break;

            case "mode":
                config.ModeHotkeyVk = 0;
                config.ModeHotkeyModifiers = 0;
                App.Config.Save();
                Services.EscOverPinHook.SetModeSwitchKey(0, 0);
                _hotkey.SetModeKey(0, 0);
                break;

            case "windowprev":
                config.WindowPrevKeyVk = 0;
                config.WindowPrevKeyModifiers = 0;
                App.Config.Save();
                PushWindowSwitchKeys();
                break;

            case "windownext":
                config.WindowNextKeyVk = 0;
                config.WindowNextKeyModifiers = 0;
                App.Config.Save();
                PushWindowSwitchKeys();
                break;

            default:
                return;
        }

        SetHotkeyText(action, "未设置");
        SyncHotkeyTexts();
        SetStatus(action is "windowprev" or "windownext"
            ? $"已删除「{HotkeyLabel(action)}」按键；取景器内仍可用滚轮切窗，点「修改」可重新录制。"
            : $"已删除「{HotkeyLabel(action)}」快捷键；仍可用首页按钮或托盘菜单，点「修改」可重新录制。");
        App.Log($"Hotkey: 删除 {action}");
    }

    /// <summary>首页四张动作卡片与设置表格共用同一份热键文案。</summary>
    private void SyncHotkeyTexts()
    {
        ScreenshotCardHotkey.Text = ScreenshotHotkeyText.Text;
        FullCardHotkey.Text = FullHotkeyText.Text;
        PinCardHotkey.Text = PinHotkeyText.Text;
        OcrCardHotkey.Text = OcrHotkeyText.Text;
        SyncSimpleHotkeyTexts();
        UpdateHotkeyClearButtons();
        // 档位说明里引用的是「切换截图档位」的当前组合键，改键/删键后同步。
        UpdateProfileHint();
    }

    /// <summary>把一个全局热键的文案同步到「设置 → 热键」卡片；未设置时显示 未设置。</summary>
    private void SetSimpleHotkeyText(string action, string text)
    {
        switch (action)
        {
            case "screenshot": SettingsScreenshotHotkeyText.Text = text; break;
            case "fullscreen": SettingsFullHotkeyText.Text = text; break;
            case "pin": SettingsPinHotkeyText.Text = text; break;
            case "ocr": SettingsOcrHotkeyText.Text = text; break;
            case "mode": SettingsModeHotkeyText.Text = text; break;
            case "windowprev": SettingsWindowPrevHotkeyText.Text = text; break;
            case "windownext": SettingsWindowNextHotkeyText.Text = text; break;
        }
    }

    private void SyncSimpleHotkeyTexts()
    {
        SetSimpleHotkeyText("screenshot", ScreenshotHotkeyText.Text);
        SetSimpleHotkeyText("fullscreen", FullHotkeyText.Text);
        SetSimpleHotkeyText("pin", PinHotkeyText.Text);
        SetSimpleHotkeyText("ocr", OcrHotkeyText.Text);
        SetSimpleHotkeyText("mode", ModeHotkeyText.Text);
        SetSimpleHotkeyText("windowprev", WindowPrevHotkeyText.Text);
        SetSimpleHotkeyText("windownext", WindowNextHotkeyText.Text);
    }

    private void UpdateHotkeyClearButtons()
    {
        ScreenshotClearButton.IsEnabled = ScreenshotHotkeyText.Text != "未设置";
        FullHotkeyClearButton.IsEnabled = FullHotkeyText.Text != "未设置";
        PinClearButton.IsEnabled = PinHotkeyText.Text != "未设置";
        OcrClearButton.IsEnabled = OcrHotkeyText.Text != "未设置";
        ModeClearButton.IsEnabled = ModeHotkeyText.Text != "未设置";
        WindowPrevClearButton.IsEnabled = WindowPrevHotkeyText.Text != "未设置";
        WindowNextClearButton.IsEnabled = WindowNextHotkeyText.Text != "未设置";
    }

    // 默认热键：与 ConfigService 的出厂值保持一致；单键重置与全部重置共用。
    private const int DefaultScreenshotVk = 0x41, DefaultScreenshotMods = 1;
    private const int DefaultFullVk = 0x44, DefaultFullMods = 1;
    private const int DefaultPinVk = 0x53, DefaultPinMods = 1;
    private const int DefaultOcrVk = 0x51, DefaultOcrMods = 1;
    private const int DefaultModeVk = 0x43, DefaultModeMods = 5;
    private const int DefaultWindowPrevVk = 0xDB, DefaultWindowNextVk = 0xDD;

    private static (int Vk, int Mods) DefaultHotkey(string action) => action switch
    {
        "screenshot" => (DefaultScreenshotVk, DefaultScreenshotMods),
        "fullscreen" => (DefaultFullVk, DefaultFullMods),
        "pin" => (DefaultPinVk, DefaultPinMods),
        "ocr" => (DefaultOcrVk, DefaultOcrMods),
        "mode" => (DefaultModeVk, DefaultModeMods),
        "windowprev" => (DefaultWindowPrevVk, 0),
        "windownext" => (DefaultWindowNextVk, 0),
        _ => (0, 0),
    };

    /// <summary>所有热键（含取景器切窗键）的当前有效组合；空键跳过。</summary>
    private static (string Action, string Label, int Vk, int Mods)[] ActiveHotkeys()
    {
        var c = App.Config.Current;
        var screenshotVk = ResolveScreenshotVk(c);
        var fullVk = ResolveFullscreenVk(c);
        return new[]
        {
            ("screenshot", "截图", screenshotVk, screenshotVk == 0 ? 0 : (int)c.HotkeyModifiers),
            ("fullscreen", "全屏截图", fullVk, fullVk == 0 ? 0 : c.FullHotkeyModifiers),
            ("pin", "贴图", c.PinHotkeyVk, c.PinHotkeyModifiers),
            ("ocr", "文字识别", c.OcrHotkeyVk, c.OcrHotkeyModifiers),
            ("mode", "切换截图档位", c.ModeHotkeyVk, c.ModeHotkeyModifiers),
            ("windowprev", "上一个窗口", c.WindowPrevKeyVk, c.WindowPrevKeyModifiers),
            ("windownext", "下一个窗口", c.WindowNextKeyVk, c.WindowNextKeyModifiers),
        };
    }

    /// <summary>全局热键与取景器切窗键共用一套按键空间；重复会有一方静默失效，所以跨表也要拦。</summary>
    private static bool TryFindAnyHotkeyConflict(string action, int vk, int mods, out string other)
    {
        other = "";
        if (vk == 0)
            return false;

        foreach (var entry in ActiveHotkeys())
        {
            if (entry.Action == action || entry.Vk == 0)
                continue;
            if (entry.Vk == vk && entry.Mods == mods)
            {
                other = entry.Label;
                return true;
            }
        }

        return false;
    }

    private void HotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action } || action.Length == 0)
            return;

        if (_recordingButton is not null)
            ResetHotkeyRecording(_recordingButton);

        var (vk, mods) = DefaultHotkey(action);
        if (vk == 0)
            return;

        if (TryFindAnyHotkeyConflict(action, vk, mods, out var other))
        {
            SetStatus($"「{HotkeyLabel(action)}」的默认键被「{other}」占用，先调整那一项再恢复默认。");
            HotkeyStatusText.Text = $"「{HotkeyLabel(action)}」默认键 {HotkeyService.Describe(vk, mods)} 与「{other}」冲突，未恢复。";
            return;
        }

        ApplyHotkey(action, vk, mods);
        SetStatus($"已把「{HotkeyLabel(action)}」恢复默认 {HotkeyService.Describe(vk, mods)}。");
        HotkeyStatusText.Text = $"「{HotkeyLabel(action)}」已恢复默认。";
        App.Log($"Hotkey: 恢复默认 {action}");
    }

    private void ResetAllHotkeys_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingButton is not null)
            ResetHotkeyRecording(_recordingButton);

        var config = App.Config.Current;
        config.HotkeyVk = DefaultScreenshotVk;
        config.HotkeyModifiers = DefaultScreenshotMods;
        config.FullHotkeyVk = DefaultFullVk;
        config.FullHotkeyModifiers = DefaultFullMods;
        config.PinHotkeyVk = DefaultPinVk;
        config.PinHotkeyModifiers = DefaultPinMods;
        config.OcrHotkeyVk = DefaultOcrVk;
        config.OcrHotkeyModifiers = DefaultOcrMods;
        config.ModeHotkeyVk = DefaultModeVk;
        config.ModeHotkeyModifiers = DefaultModeMods;
        config.WindowPrevKeyVk = DefaultWindowPrevVk;
        config.WindowPrevKeyModifiers = 0;
        config.WindowNextKeyVk = DefaultWindowNextVk;
        config.WindowNextKeyModifiers = 0;
        App.Config.Save();

        Services.EscOverPinHook.SetScreenshotKey(DefaultScreenshotVk, DefaultScreenshotMods);
        _hotkey.Restart(DefaultScreenshotVk, DefaultScreenshotMods);
        Services.EscOverPinHook.SetFullScreenKey(DefaultFullVk, DefaultFullMods);
        _hotkey.SetFullscreenKey(DefaultFullVk, DefaultFullMods);
        _hotkey.SetPinKey(DefaultPinVk, DefaultPinMods);
        _hotkey.SetOcrKey(DefaultOcrVk, DefaultOcrMods);
        Services.EscOverPinHook.SetModeSwitchKey(DefaultModeVk, DefaultModeMods);
        _hotkey.SetModeKey(DefaultModeVk, DefaultModeMods);
        PushWindowSwitchKeys();

        foreach (var action in new[] { "screenshot", "fullscreen", "pin", "ocr", "mode", "windowprev", "windownext" })
        {
            var (vk, mods) = DefaultHotkey(action);
            SetHotkeyText(action, HotkeyService.Describe(vk, mods));
        }

        SyncHotkeyTexts();
        SetStatus("所有快捷键已恢复默认。");
        HotkeyStatusText.Text = "所有快捷键已恢复默认。";
        App.Log("Hotkey: 全部恢复默认");
    }

    private void ApplyHotkey(string action, int vk, int mods)
    {
        if (vk != 0 && TryFindAnyHotkeyConflict(action, vk, mods, out var occupied))
        {
            SetStatus($"这个组合键已经给了「{occupied}」，换一个");
            HotkeyStatusText.Text = $"「{HotkeyService.Describe(vk, mods)}」与「{occupied}」冲突，未保存。";
            return;
        }

        var config = App.Config.Current;
        var text = HotkeyService.Describe(vk, mods);

        switch (action)
        {
            case "screenshot":
                config.HotkeyVk = vk;
                config.HotkeyModifiers = (uint)mods;
                App.Config.Save();
                Services.EscOverPinHook.SetScreenshotKey(vk, mods);
                _hotkey.Restart(vk, mods);
                ScreenshotHotkeyText.Text = text;
                SetStatus($"截图热键已设为 {text}");
                break;

            case "fullscreen":
                config.FullHotkeyVk = vk;
                config.FullHotkeyModifiers = mods;
                App.Config.Save();
                Services.EscOverPinHook.SetFullScreenKey(vk, mods);
                _hotkey.SetFullscreenKey(vk, mods);
                FullHotkeyText.Text = text;
                SetStatus($"全屏截图热键已设为 {text}");
                break;

            case "pin":
                config.PinHotkeyVk = vk;
                config.PinHotkeyModifiers = mods;
                App.Config.Save();
                _hotkey.SetPinKey(vk, mods);
                PinHotkeyText.Text = text;
                SetStatus($"贴图热键已设为 {text}");
                break;

            case "ocr":
                config.OcrHotkeyVk = vk;
                config.OcrHotkeyModifiers = mods;
                App.Config.Save();
                _hotkey.SetOcrKey(vk, mods);
                OcrHotkeyText.Text = text;
                SetStatus($"OCR 热键已设为 {text}");
                break;

            case "mode":
                config.ModeHotkeyVk = vk;
                config.ModeHotkeyModifiers = mods;
                App.Config.Save();
                Services.EscOverPinHook.SetModeSwitchKey(vk, mods);
                _hotkey.SetModeKey(vk, mods);
                ModeHotkeyText.Text = text;
                SetStatus($"切换截图档位热键已设为 {text}");
                break;

            case "windowprev":
                config.WindowPrevKeyVk = vk;
                config.WindowPrevKeyModifiers = mods;
                App.Config.Save();
                PushWindowSwitchKeys();
                WindowPrevHotkeyText.Text = text;
                SetStatus($"取景器「上一个窗口」已设为 {text}");
                break;

            case "windownext":
                config.WindowNextKeyVk = vk;
                config.WindowNextKeyModifiers = mods;
                App.Config.Save();
                PushWindowSwitchKeys();
                WindowNextHotkeyText.Text = text;
                SetStatus($"取景器「下一个窗口」已设为 {text}");
                break;
        }

        SyncHotkeyTexts();
        App.Log($"Hotkey: 更新 {action} -> {text}");
    }

    /// <summary>四个热键不允许重复，否则后注册的那个会静默失败。</summary>
    private static bool TryFindHotkeyConflict(string action, int vk, int mods, out string other)
    {
        other = "";
        var c = App.Config.Current;

        var screenshotVk = ResolveScreenshotVk(c);
        var screenshotMods = screenshotVk == 0 ? 0 : (int)c.HotkeyModifiers;
        var fullVk = ResolveFullscreenVk(c);
        var fullMods = fullVk == 0 ? 0 : c.FullHotkeyModifiers;

        if (screenshotVk != 0 && action != "screenshot" && vk == screenshotVk && mods == screenshotMods)
        {
            other = "窗口截图";
            return true;
        }
        if (fullVk != 0 && action != "fullscreen" && vk == fullVk && mods == fullMods)
        {
            other = "全屏截图";
            return true;
        }
        if (c.PinHotkeyVk != 0 && action != "pin" && vk == c.PinHotkeyVk && mods == c.PinHotkeyModifiers)
        {
            other = "贴图";
            return true;
        }
        if (c.OcrHotkeyVk != 0 && action != "ocr" && vk == c.OcrHotkeyVk && mods == c.OcrHotkeyModifiers)
        {
            other = "OCR";
            return true;
        }
        if (c.ModeHotkeyVk != 0 && action != "mode" && vk == c.ModeHotkeyVk && mods == c.ModeHotkeyModifiers)
        {
            other = "切换截图模式";
            return true;
        }
        if (c.WindowPrevKeyVk != 0 && action != "windowprev" && vk == c.WindowPrevKeyVk && mods == c.WindowPrevKeyModifiers)
        {
            other = "上一个窗口";
            return true;
        }
        if (c.WindowNextKeyVk != 0 && action != "windownext" && vk == c.WindowNextKeyVk && mods == c.WindowNextKeyModifiers)
        {
            other = "下一个窗口";
            return true;
        }

        if (action is "windowprev" or "windownext" && IsReservedOverlayKey(vk, mods, out var reserved))
        {
            other = reserved;
            return true;
        }

        return false;
    }

    /// <summary>取景器内已固定语义的按键，不能被切窗键占用。</summary>
    private static bool IsReservedOverlayKey(int vk, int mods, out string name)
    {
        name = "";
        if (mods == 0 && vk is 0x1B or 0x09 or 0x0D or 0x4F)
        {
            name = vk switch { 0x1B => "Esc", 0x09 => "Tab", 0x0D => "回车", _ => "O" };
            return true;
        }
        if (mods == 2 && vk is 0x43 or 0x53)
        {
            name = vk == 0x43 ? "Ctrl+C" : "Ctrl+S";
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
        // 必须在这里兜住并写日志。
        try
        {
            // 延时等待中再按一次 = 取消，而不是叠加一次新截图
            if (_delayCts is not null)
            {
                App.Log("Delay: hotkey pressed again, cancel");
                _delayCts.Cancel();
                return;
            }

            // 取景器已经打开时，热键仍然走原来的“再按一次取消”语义
            if (Services.CaptureService.Instance.IsCapturing)
            {
                await Services.CaptureService.Instance.StartCaptureAsync();
                return;
            }

            var delay = NormalizeDelay(App.Config.Current.CaptureDelaySeconds);
            if (delay > 0)
            {
                await StartDelayedCaptureAsync(delay);
                return;
            }

            if (IsQuickProfile())
            {
                App.Log("CaptureProfile: quick mode, capture fullscreen immediately");
                OnFullScreenPressed();
                return;
            }

            await Services.CaptureService.Instance.StartCaptureAsync();
        }
        catch (Exception ex)
        {
            App.Log($"热键处理异常: {ex}");
        }
    }

    /// <summary>延时截图：先等待，期间用户可以打开菜单/悬停；到点后再走正常冻屏取景。</summary>
    private async Task StartDelayedCaptureAsync(int seconds)
    {
        if (_delayCts is not null)
        {
            App.Log("Delay: 已有延时截图在进行，忽略新请求");
            return;
        }

        var cts = new CancellationTokenSource();
        _delayCts = cts;
        CancelDelayItem.Visibility = Visibility.Visible;
        UpdateScreenshotCard();

        try
        {
            App.Log($"Delay: start {seconds}s quick={IsQuickProfile()}");
            Services.ToastService.ShowInfo($"{seconds} 秒后截图，期间可正常操作；再按一次截图热键可取消。");

            for (var remaining = seconds; remaining > 0; remaining--)
            {
                TrayIcon.ToolTipText = $"{AppInfo.Name} — {remaining} 秒后截图";
                SetStatus($"延时截图：{remaining} 秒后冻结画面（再按一次截图热键可取消）");
                await Task.Delay(1000, cts.Token);
            }

            App.Log($"Delay: capture now quick={IsQuickProfile()}");
            if (IsQuickProfile())
            {
                SetStatus("延时结束，正在截取整屏…");
                OnFullScreenPressed();
            }
            else
            {
                SetStatus("延时结束，正在冻结画面…");
                await Services.CaptureService.Instance.StartCaptureAsync();
            }
        }
        catch (OperationCanceledException)
        {
            App.Log("Delay: canceled");
            SetStatus("已取消延时截图");
        }
        catch (Exception ex)
        {
            App.Log($"Delay: 异常 {ex}");
        }
        finally
        {
            if (ReferenceEquals(_delayCts, cts))
            {
                _delayCts = null;
                CancelDelayItem.Visibility = Visibility.Collapsed;
                ResetTrayToolTip();
                UpdateScreenshotCard();
            }

            cts.Dispose();
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