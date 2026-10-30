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

    private void TestUmi_Click(object sender, RoutedEventArgs e) => _ = RefreshOcrStatusAsync(probeUmi: true);

    private void OcrPreferUmi_Toggled(object sender, RoutedEventArgs e)
    {
        if (OcrPreferUmiToggle is null)
            return;

        App.Config.Current.PreferUmiOcr = OcrPreferUmiToggle.IsOn;
        App.Config.Save();
        _ = RefreshOcrStatusAsync();
    }


    /// <summary>
    /// 刷新 OCR 状态。默认不联网：只有用户点「测试 Umi-OCR 连接」时 probeUmi 才为 true，
    /// 保证关闭 Umi 后启动、切换开关、改端口等自动路径都不会向 Umi 发请求。
    /// </summary>
    private async Task RefreshOcrStatusAsync(bool probeUmi = false)
    {
        if (Interlocked.Exchange(ref _ocrProbeBusy, 1) == 1)
            return;

        var prefer = App.Config.Current.PreferUmiOcr;
        var port = App.Config.Current.UmiOcrPort;
        if (probeUmi)
        {
            TestUmiButton.IsEnabled = false;
            UmiTestRing.IsActive = true;
            UmiTestRing.Visibility = Visibility.Visible;
        }

        try
        {
            var windows = PrtScr.Ocr.WindowsOcrEngine.GetStatus();
            PrtScr.Ocr.UmiOcrClient.ProbeResult? umi = null;
            if (probeUmi)
            {
                umi = await new PrtScr.Ocr.UmiOcrClient(port).ProbeAsync();
                App.Log($"OCR: 测试 Umi-OCR 端口 {port} → {(umi.Online ? "在线" : "离线")}"
                    + (string.IsNullOrWhiteSpace(umi.Error) ? "" : $"（{umi.Error}）"));
            }

            var umiOnline = umi?.Online == true;
            if (!windows.Available)
            {
                OcrStatusBar.Severity = InfoBarSeverity.Error;
                OcrStatusBar.Title = "OCR 不可用：缺少 Windows 语言包";
            }
            else if (prefer && umiOnline)
            {
                OcrStatusBar.Severity = InfoBarSeverity.Success;
                OcrStatusBar.Title = "OCR 引擎就绪：Umi-OCR 优先";
            }
            else if (prefer)
            {
                OcrStatusBar.Severity = umi is null ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
                OcrStatusBar.Title = umi is null
                    ? "OCR 引擎就绪：Umi-OCR 已启用（未检测）"
                    : "OCR 引擎就绪：使用 Windows OCR（Umi-OCR 未连接）";
            }
            else
            {
                OcrStatusBar.Severity = InfoBarSeverity.Success;
                OcrStatusBar.Title = "OCR 引擎就绪：使用 Windows OCR";
            }

            string umiState;
            if (!prefer)
            {
                if (umi is null)
                    umiState = "已关闭（不会连接；点「测试 Umi-OCR 连接」可单次检测）";
                else if (umi.Online)
                    umiState = $"已关闭，但本次探测在线（端口 {umi.Port}；仅检测，不参与识别）";
                else
                    umiState = $"已关闭，本次探测未连接（端口 {umi.Port}）";
            }
            else if (umi is null)
            {
                umiState = $"已启用，未检测（端口 {port}；点「测试 Umi-OCR 连接」做单次检测）";
            }
            else if (umi.Online)
            {
                umiState = $"在线（端口 {umi.Port}）";
            }
            else
            {
                umiState = $"未连接（端口 {umi.Port}）：{umi.Error}";
            }

            OcrStatusBar.Message =
                $"Windows OCR：{(windows.Available ? $"可用（{windows.LanguageCount} 种语言）" : "不可用（系统缺少 OCR 语言包）")} · " +
                $"Umi-OCR：{umiState}";
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
            if (probeUmi)
            {
                TestUmiButton.IsEnabled = true;
                UmiTestRing.IsActive = false;
                UmiTestRing.Visibility = Visibility.Collapsed;
            }

            Interlocked.Exchange(ref _ocrProbeBusy, 0);
        }
    }
}
