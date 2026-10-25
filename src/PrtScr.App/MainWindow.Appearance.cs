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

        UpdateBackgroundAppearance();
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

        UpdateBackgroundAppearance();
    }

    private void UpdateBackgroundAppearance()
    {
        var path = App.Config.Current.BackgroundImagePath;
        var hasImage = BackgroundImage.Source is not null;
        BackgroundPreviewImage.Source = hasImage ? BackgroundImage.Source : null;
        BackgroundFileNameText.Text = hasImage ? Path.GetFileName(path) : "默认背景（未设置图片）";
        BackgroundOpacityText.Text = hasImage
            ? $"不透明度 {BackgroundImage.Opacity * 100:0}%"
            : "不透明度 —";
        BackgroundOpacitySlider.IsEnabled = hasImage;
        BackgroundDisabledHintText.Text = hasImage
            ? "背景图会覆盖窗口底色；觉得文字对比度不够时，把不透明度调低或恢复默认。"
            : "没有背景图时不透明度不可调；先点「选择背景图片…」。";
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        => OpenDataFolder(Path.Combine(AppContext.BaseDirectory, "data", "logs"), "日志");

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        => OpenDataFolder(Path.Combine(AppContext.BaseDirectory, "data"), "数据");

    private void OpenDataFolder(string path, string label)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            AboutStatusText.Text = $"已打开{label}文件夹：{path}";
        }
        catch (Exception ex)
        {
            AboutStatusText.Text = $"打开{label}文件夹失败：{ex.Message}";
            App.Log($"About: 打开{label}文件夹失败 {ex.Message}");
        }
    }

    private void CopyVersionInfo_Click(object sender, RoutedEventArgs e)
    {
        var info = $"{AppInfo.Name} v{typeof(App).Assembly.GetName().Version}\n{AppInfo.Tagline}\n分窗口截屏截图工具（IshWY.exe）";
        try
        {
            var package = new DataPackage();
            package.SetText(info);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            AboutStatusText.Text = "已复制版本信息到剪贴板。";
        }
        catch (Exception ex)
        {
            AboutStatusText.Text = $"复制失败：{ex.Message}";
            App.Log($"About: 复制版本信息失败 {ex.Message}");
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

}
