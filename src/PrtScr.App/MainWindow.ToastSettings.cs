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

}
