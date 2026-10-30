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
        Services.FullscreenBypass? bypass = null;
        try
        {
            // 先直接抓：WGC/DXGI 在大多数窗口化、无边框全屏下不需要抢焦点。
            // 只有首帧失败或疑似黑屏（独占全屏）才短暂切前台，避免每次按热键都闪一下、游戏音频中断。
            var result = await Services.CaptureService.CaptureFullScreenPngAsync();
            if (result.Png is null || result.Status != Services.CaptureService.QuickCaptureStatus.Success)
            {
                App.Log($"QuickCapture: 首帧不可用 status={result.Status}，短暂切前台后重试");
                bypass = await Services.FullscreenBypass.BeginAsync();
                await Task.Delay(150);
                result = await Services.CaptureService.CaptureFullScreenPngAsync();
            }

            var activated = bypass?.Activated ?? false;

            if (result.Png is null || result.Status != Services.CaptureService.QuickCaptureStatus.Success)
            {
                if (result.Status == Services.CaptureService.QuickCaptureStatus.LikelyBlack)
                {
                    SetStatus("当前画面像是独占全屏游戏，Windows 无法截取");
                    App.Log($"QuickCapture: 两次都是黑屏 (bypass={activated})");
                }
                else
                {
                    SetStatus("采集失败，请重试");
                    App.Log($"QuickCapture: 失败，无图像 (bypass={activated})");
                }

                ToastService.ShowCaptureFailure();
                return;
            }

            var copyTask = Services.ClipboardService.SetImageAsync(result.Png);
            var historyTask = await Services.CaptureHistoryService.Instance.AddAsync(
                result.Png, HistoryCaptureMode.Normal);
            var ok = await copyTask;

            SetStatus(ok ? "全屏截图已复制到剪贴板" : "全屏截图复制失败（剪贴板被占用）");
            if (ok)
                ToastService.ShowCaptureSuccess();
            else
                ToastService.ShowCaptureFailure();
            App.Log($"QuickCapture: 完成 {result.Png.Length / 1024}KB copied={ok} bypass={activated} history={historyTask is not null}");
            if (historyTask is { } savedPath)
                App.Log($"QuickCapture: history saved {savedPath}");
        }
        catch (Exception ex)
        {
            App.Log($"全屏截图异常: {ex}");
            ToastService.ShowCaptureFailure();
        }
        finally
        {
            bypass?.Dispose();
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

            if (delay == 0 && !IsQuickProfile()
                && App.Config.Current.GameFullscreenQuickCapture
                && Services.FullscreenBypass.IsForegroundFullscreenGame())
            {
                App.Log("Capture: 前台全屏游戏，自动改用全屏快照，避免取景器抢焦点");
                OnFullScreenPressed();
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

}
