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

    private static string? NormalizeHexColor(string? text) => ColorRules.NormalizeHexColor(text);

    private static Windows.UI.Color? ParseHexColor(string? text)
    {
        var rgb = ColorRules.ParseRgb(text);
        return rgb is null
            ? null
            : Windows.UI.Color.FromArgb(0xFF, rgb.Value.R, rgb.Value.G, rgb.Value.B);
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

    private void GameFullscreenQuickToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressCaptureOptions || GameFullscreenQuickToggle is null)
            return;

        var enabled = GameFullscreenQuickToggle.IsOn;
        if (App.Config.Current.GameFullscreenQuickCapture == enabled)
            return;

        App.Config.Current.GameFullscreenQuickCapture = enabled;
        App.Config.Save();
        UpdateCaptureOptions();
        App.Log($"CaptureOptions: gameFullscreenQuick={enabled}");
    }

    private static int DelayToIndex(int seconds) => CaptureDelayRules.ToIndex(seconds);

    private static int DelayFromIndex(int index) => CaptureDelayRules.FromIndex(index);

    private static int NormalizeDelay(int seconds) => CaptureDelayRules.Normalize(seconds);

    private void UpdateCaptureOptions()
    {
        var delay = NormalizeDelay(App.Config.Current.CaptureDelaySeconds);
        var cursor = App.Config.Current.CaptureCursor;

        DelayCaptureItem.Text = delay > 0 ? $"延时截图（{delay} 秒）" : "延时截图（3 秒）";
        CaptureOptionsText.Text = delay > 0
            ? $"当前：按下截图热键后等待 {delay} 秒再冻结画面；等待期间再按一次截图热键可取消。"
            : "当前：截图热键立即冻结画面；托盘菜单的「延时截图」默认等 3 秒。";
        CaptureOptionsText.Text += cursor ? " 截图会包含鼠标指针。" : " 截图不包含鼠标指针。";
        CaptureOptionsText.Text += App.Config.Current.GameFullscreenQuickCapture
            ? " 全屏游戏里按截图热键会直接截整屏，不弹取景器。"
            : " 全屏游戏里仍会弹出取景器，可能短暂切换画面。";

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

}
