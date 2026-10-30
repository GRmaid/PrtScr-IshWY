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
            // 订阅者本身已在线程池上（EscOverPinHook.RaiseDeferred），日志仍随动作一起进 UI 队列，
            // 绝不在热键投递路径上写盘。
            DispatcherQueue.TryEnqueue(() =>
            {
                App.Log("Hotkey: 键盘钩子捕获截图键");
                OnHotkeyPressed();
            });
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
        SetHotkeyStatus($"正在录制「{HotkeyLabel(action)}」：请按下组合键，Esc 取消。");
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
            SetHotkeyStatus("已取消录制快捷键。");
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
            SetHotkeyStatus("请按住 Ctrl / Alt / Shift / Win 之一（F1–F12 可单独使用）");
            return;
        }

        if (overlayOnly && (mods & (1 | 8)) != 0)
        {
            SetHotkeyStatus("取景器切窗键不支持 Alt / Win 修饰（Alt 会和取景操作冲突）");
            return;
        }

        if (overlayOnly && IsReservedOverlayKey(vk, mods, out var reserved))
        {
            SetHotkeyStatus($"「{reserved}」是取景器固定按键，换一个");
            return;
        }

        if (TryFindAnyHotkeyConflict(_recordingAction, vk, mods, out var other))
        {
            SetHotkeyStatus($"这个组合键已经给了「{other}」，换一个");
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
        SetHotkeyStatus(action is "windowprev" or "windownext"
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
            SetHotkeyStatus($"「{HotkeyLabel(action)}」的默认键被「{other}」占用，先调整那一项再恢复默认。");
            return;
        }

        ApplyHotkey(action, vk, mods);
        SetHotkeyStatus($"已把「{HotkeyLabel(action)}」恢复默认 {HotkeyService.Describe(vk, mods)}。");
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
        SetHotkeyStatus("所有快捷键已恢复默认。");
        App.Log("Hotkey: 全部恢复默认");
    }

    private void ApplyHotkey(string action, int vk, int mods)
    {
        if (vk != 0 && TryFindAnyHotkeyConflict(action, vk, mods, out var occupied))
        {
            SetHotkeyStatus($"这个组合键已经给了「{occupied}」，换一个");
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
                SetHotkeyStatus($"截图热键已设为 {text}");
                break;

            case "fullscreen":
                config.FullHotkeyVk = vk;
                config.FullHotkeyModifiers = mods;
                App.Config.Save();
                Services.EscOverPinHook.SetFullScreenKey(vk, mods);
                _hotkey.SetFullscreenKey(vk, mods);
                FullHotkeyText.Text = text;
                SetHotkeyStatus($"全屏截图热键已设为 {text}");
                break;

            case "pin":
                config.PinHotkeyVk = vk;
                config.PinHotkeyModifiers = mods;
                App.Config.Save();
                _hotkey.SetPinKey(vk, mods);
                PinHotkeyText.Text = text;
                SetHotkeyStatus($"贴图热键已设为 {text}");
                break;

            case "ocr":
                config.OcrHotkeyVk = vk;
                config.OcrHotkeyModifiers = mods;
                App.Config.Save();
                _hotkey.SetOcrKey(vk, mods);
                OcrHotkeyText.Text = text;
                SetHotkeyStatus($"OCR 热键已设为 {text}");
                break;

            case "mode":
                config.ModeHotkeyVk = vk;
                config.ModeHotkeyModifiers = mods;
                App.Config.Save();
                Services.EscOverPinHook.SetModeSwitchKey(vk, mods);
                _hotkey.SetModeKey(vk, mods);
                ModeHotkeyText.Text = text;
                SetHotkeyStatus($"切换截图档位热键已设为 {text}");
                break;

            case "windowprev":
                config.WindowPrevKeyVk = vk;
                config.WindowPrevKeyModifiers = mods;
                App.Config.Save();
                PushWindowSwitchKeys();
                WindowPrevHotkeyText.Text = text;
                SetHotkeyStatus($"取景器「上一个窗口」已设为 {text}");
                break;

            case "windownext":
                config.WindowNextKeyVk = vk;
                config.WindowNextKeyModifiers = mods;
                App.Config.Save();
                PushWindowSwitchKeys();
                WindowNextHotkeyText.Text = text;
                SetHotkeyStatus($"取景器「下一个窗口」已设为 {text}");
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


}
