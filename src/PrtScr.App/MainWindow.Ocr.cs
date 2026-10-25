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


    private async Task RefreshOcrStatusAsync()
    {
        if (Interlocked.Exchange(ref _ocrProbeBusy, 1) == 1)
            return;

        TestUmiButton.IsEnabled = false;
        UmiTestRing.IsActive = true;
        UmiTestRing.Visibility = Visibility.Visible;
        try
        {
            var windows = PrtScr.Ocr.WindowsOcrEngine.GetStatus();
            var umi = await new PrtScr.Ocr.UmiOcrClient(App.Config.Current.UmiOcrPort).ProbeAsync();
            var prefer = App.Config.Current.PreferUmiOcr;

            OcrStatusBar.Severity = !windows.Available
                ? InfoBarSeverity.Error
                : umi.Online && prefer
                    ? InfoBarSeverity.Success
                    : umi.Online
                        ? InfoBarSeverity.Informational
                        : InfoBarSeverity.Warning;
            OcrStatusBar.Title = !windows.Available
                ? "OCR 不可用：缺少 Windows 语言包"
                : umi.Online && prefer
                    ? "OCR 引擎就绪：Umi-OCR 优先"
                    : umi.Online
                        ? "OCR 引擎就绪：Umi-OCR 已在线"
                        : "OCR 引擎就绪：使用 Windows OCR";
            OcrStatusBar.Message =
                $"Windows OCR：{(windows.Available ? $"可用（{windows.LanguageCount} 种语言）" : "不可用（系统缺少 OCR 语言包）")} · " +
                $"Umi-OCR：{(umi.Online ? $"在线（端口 {umi.Port}）" : $"未运行（端口 {umi.Port} 未响应）")}";
        }
        catch (Exception ex)
        {
            OcrStatusBar.Severity = InfoBarSeverity.Error;
            OcrStatusBar.Title = "OCR 状态检测失败";
            OcrStatusBar.Message = ex.Message;
            App.Log($"OCR: 状态检测失败 {ex.Message}");
        }
        finally
        {
            TestUmiButton.IsEnabled = true;
            UmiTestRing.IsActive = false;
            UmiTestRing.Visibility = Visibility.Collapsed;
            Interlocked.Exchange(ref _ocrProbeBusy, 0);
        }
    }
}
