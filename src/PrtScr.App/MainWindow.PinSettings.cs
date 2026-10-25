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

    private static int PinBehaviorIndex(string? behavior) => Services.PinService.NormalizeBehavior(behavior) switch
    {
        Services.PinService.BehaviorLatest => 1,
        Services.PinService.BehaviorText => 2,
        Services.PinService.BehaviorOff => 3,
        _ => 0,
    };

    private static int PinTextPresetIndex(string color)
    {
        var presets = new[] { "#1F1F1F", "#F7F7F7", "#F3E9D2", "#263445", "#1E332C", "#3A2A22" };
        return Array.FindIndex(presets, p => string.Equals(p, color, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadPinBehaviorSettings()
    {
        var config = App.Config.Current;
        var behavior = Services.PinService.NormalizeBehavior(config.PinBehavior);
        var color = NormalizeHexColor(config.PinTextBackground) ?? "#1F1F1F";

        _suppressPinBehavior = true;
        PinBehaviorCombo.SelectedIndex = PinBehaviorIndex(behavior);
        _suppressPinBehavior = false;

        _suppressPinTextColor = true;
        PinTextColorBox.Text = color;
        ApplyPinTextColorPreview(color);
        PinTextColorPresetCombo.SelectedIndex = PinTextPresetIndex(color);
        _suppressPinTextColor = false;

        UpdatePinBehaviorHint();
        UpdatePinTextColorHint(color);
        ApplyPinBehaviorLabels();
    }

    private void PinBehaviorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPinBehavior || PinBehaviorCombo?.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;

        var behavior = Services.PinService.NormalizeBehavior(tag);
        if (!string.Equals(App.Config.Current.PinBehavior, behavior, StringComparison.OrdinalIgnoreCase))
        {
            App.Config.Current.PinBehavior = behavior;
            App.Config.Save();
            SetStatus($"贴图行为已改为「{Services.PinService.DescribeBehavior(behavior)}」。");
            App.Log($"Pin: behavior={behavior}");
        }

        UpdatePinBehaviorHint();
        ApplyPinBehaviorLabels();
    }

    private void UpdatePinBehaviorHint()
    {
        if (PinBehaviorHintText is null)
            return;

        PinBehaviorHintText.Text = Services.PinService.NormalizeBehavior(App.Config.Current.PinBehavior) switch
        {
            Services.PinService.BehaviorLatest =>
                "贴图时忽略剪贴板，直接取截图历史里时间最新的一张；截图历史为空时会提示。",
            Services.PinService.BehaviorText =>
                "只把剪贴板里的文字渲染成卡片；剪贴板里只有图片、没有文字时不会贴图。",
            Services.PinService.BehaviorOff =>
                "贴图热键、首页贴图卡片和托盘贴图入口都会停用，只显示一条已关闭提示；随时可以在这里重新打开。",
            _ =>
                "剪贴板里有图片就贴图片；没有图片但有文字就贴文字卡片；两者都没有时提示。",
        };
    }

    private void ApplyPinBehaviorLabels()
    {
        var behavior = Services.PinService.NormalizeBehavior(App.Config.Current.PinBehavior);
        if (PinCardDescriptionText is not null)
            PinCardDescriptionText.Text = Services.PinService.DescribeBehaviorTarget(behavior);
        if (TrayPinItem is not null)
        {
            TrayPinItem.Text = behavior switch
            {
                Services.PinService.BehaviorLatest => "贴最新截图",
                Services.PinService.BehaviorText => "贴剪贴板文字",
                Services.PinService.BehaviorOff => "贴图（已关闭）",
                _ => "从剪贴板贴图",
            };
        }
    }

    private void PinTextColorPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPinTextColor || PinTextColorPresetCombo?.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;
        PinTextColorBox.Text = tag;
    }

    private void PinTextColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressPinTextColor || PinTextColorBox is null || PinTextColorHintText is null)
            return;

        var normalized = NormalizeHexColor(PinTextColorBox.Text);
        if (normalized is null)
        {
            PinTextColorHintText.Text = "颜色格式应为 #RRGGBB（例如 #1F1F1F）；格式不对不会保存。";
            return;
        }

        ApplyPinTextColorPreview(normalized);
        if (string.Equals(App.Config.Current.PinTextBackground, normalized, StringComparison.OrdinalIgnoreCase))
        {
            UpdatePinTextColorHint(normalized);
            return;
        }

        App.Config.Current.PinTextBackground = normalized;
        App.Config.Save();
        _suppressPinTextColor = true;
        PinTextColorPresetCombo.SelectedIndex = PinTextPresetIndex(normalized);
        _suppressPinTextColor = false;
        UpdatePinTextColorHint(normalized);
        SetStatus($"文字贴图背景色已改为 {normalized}。");
        App.Log($"Pin: textBackground={normalized}");
    }

    private void ApplyPinTextColorPreview(string color)
    {
        var parsed = ParseHexColor(color);
        PinTextColorPreview.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            parsed ?? Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F));
    }

    private void UpdatePinTextColorHint(string color)
    {
        PinTextColorHintText.Text =
            $"当前 {color}。文字颜色按背景明暗自动取黑或白；只影响文字贴图，不影响截图、贴图的其他功能。";
    }

    private void PinTextPreview_Click(object sender, RoutedEventArgs e)
    {
        var sample = "文字贴图预览\n\n这是从剪贴板贴出来的文字卡片。\n可以标注、裁剪、另存为，也可以直接 OCR。";
        var result = Services.PinService.PasteText(sample);
        SetStatus(result.Success ? "已贴出文字贴图预览；关闭它即可。" : result.Message);
        App.Log($"Pin: textPreview status={result.Status}");
    }

}
