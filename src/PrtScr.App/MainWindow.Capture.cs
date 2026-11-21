using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using PrtScr.App.Services;
using PrtScr.Core;
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
        // 退出封口开始后，已经完成 Task.Delay 的延时回调仍可能晚到；这里只记日志，不再发起采集。
        if (Volatile.Read(ref _exitStarted) != 0)
        {
            App.Log("QuickCapture: 退出已开始，忽略晚到的全屏回调");
            return;
        }

        App.Log("QuickCapture: hotkey pressed");
        try
        {
            // 取图、旁路、剪贴板与历史全部由服务的 Quick 会话统一仲裁；UI 只 await 一次，再按结局更新状态。
            var outcome = await Services.CaptureService.Instance.StartQuickCaptureAsync();
            var failure = outcome.CaptureResult.Failure;

            if (outcome.Disposition == QuickCaptureDisposition.Ignored)
            {
                App.Log($"QuickCapture: 忽略（槽忙/latch/退出封口）status={outcome.Status} bytes={outcome.PngBytes} bypass={outcome.BypassActivated} failure={failure} detail={outcome.CaptureResult.Detail}");
                return;
            }

            // 取消（含采集终态为 Canceled）不提示：用户已主动取消或退出封口，不打扰。
            if (outcome.Disposition == QuickCaptureDisposition.Canceled || failure == CaptureFailure.Canceled)
            {
                App.Log($"QuickCapture: 取消 status={outcome.Status} bytes={outcome.PngBytes} bypass={outcome.BypassActivated} failure={failure} detail={outcome.CaptureResult.Detail}");
                return;
            }

            if (outcome.Disposition == QuickCaptureDisposition.Failed)
            {
                if (outcome.Status == Services.CaptureService.QuickCaptureStatus.LikelyBlack)
                {
                    SetStatus("当前画面像是独占全屏游戏，Windows 无法截取");
                    App.Log($"QuickCapture: 两次都是黑屏 (bypass={outcome.BypassActivated})");
                }
                else
                {
                    SetStatus("采集失败，请重试");
                    App.Log($"QuickCapture: 失败，无图像 (bypass={outcome.BypassActivated})");
                }

                ToastService.ShowCaptureFailure();
                return;
            }

            // Success 只表示图像采集完成并已尝试提交：复制/历史仍可能失败。
            var copied = outcome.Copied;
            SetStatus(copied ? "全屏截图已复制到剪贴板" : "全屏截图复制失败（剪贴板被占用）");
            if (!copied)
            {
                ToastService.ShowCaptureFailure();
            }
            else if (failure == CaptureFailure.None)
            {
                ToastService.ShowCaptureSuccess();
            }
            // 图像已复制但附属步骤失败（如历史写入）时抑制成功提示，只留日志，不新增提示文案。

            App.Log($"QuickCapture: 完成 {outcome.PngBytes / 1024}KB copied={copied} bypass={outcome.BypassActivated} historySaved={outcome.HistorySaved}");
            if (outcome.HistoryPath is { } savedPath)
                App.Log($"QuickCapture: history saved {savedPath}");
            if (failure != CaptureFailure.None)
                App.Log($"QuickCapture: result failure={failure} detail={outcome.CaptureResult.Detail}");
        }
        catch (Exception ex)
        {
            App.Log($"全屏截图异常: {ex}");
            ToastService.ShowCaptureFailure();
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
        // 退出封口开始后，晚到的热键回调不再发起新的取景/取消。
        if (Volatile.Read(ref _exitStarted) != 0)
        {
            App.Log("Capture: 退出已开始，忽略晚到的截图热键回调");
            return;
        }

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
            if (Services.CaptureService.Instance.IsOverlayCapturing)
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
        // 退出封口开始后，已完成的 Task.Delay 其后续仍可能晚到；这里只记日志，不再开新的延时。
        if (Volatile.Read(ref _exitStarted) != 0)
        {
            App.Log("Delay: 退出已开始，忽略晚到的延时截图请求");
            return;
        }

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
