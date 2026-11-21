using System.Drawing;
using System.Drawing.Imaging;
using PrtScr.Capture;
using PrtScr.Core;

namespace PrtScr.App.Services;

/// <summary>快速全屏截图的四种结局；Ignored 表示槽忙/已 latch/退出封口，本次根本没开始。</summary>
public enum QuickCaptureDisposition
{
    Ignored = 0,
    Canceled = 1,
    Success = 2,
    Failed = 3,
}

/// <summary>
/// 一次快速全屏截图的结果。Success 只表示采集完成并已尝试提交：Copied/HistorySaved 仍可能为 false，
/// CaptureResult 是 End 终态快照里的结果（Active 是释放前事实）。
/// </summary>
public sealed record QuickCaptureOutcome(
    QuickCaptureDisposition Disposition,
    int PngBytes,
    CaptureService.QuickCaptureStatus Status,
    bool Copied,
    bool HistorySaved,
    string? HistoryPath,
    bool BypassActivated,
    CaptureResult CaptureResult);

public sealed partial class CaptureService
{
    // 只作资源/等待句柄：CTS 用于让取消尽快脱身，Task 只用于退出等待；两者都不是 busy 真源。
    private CancellationTokenSource? _quickCancellation;
    private Task<QuickCaptureOutcome>? _quickTask;

    /// <summary>
    /// 快速全屏截图（不打开取景器）。首个 await 之前原子占用 Quick 会话槽，不新增取图前 thread-hop。
    /// 只在 UI 线程调用：旁路的 Begin/Dispose 与提交后的收尾都保持在同一 UI 线程。
    /// </summary>
    public Task<QuickCaptureOutcome> StartQuickCaptureAsync()
    {
        App.Log("Capture: quick start");
        var sessionId = Guid.NewGuid();
        if (_coordinator.Begin(sessionId, CaptureSessionKind.Quick) is null)
        {
            var slot = _coordinator.Snapshot();
            App.Log($"Capture: quick ignored (busy={slot.IsBusy}, latched={slot.CommitLatched}, shutdown={slot.IsShuttingDown}, state={slot.State})");
            return Task.FromResult(IgnoredOutcome());
        }

        var cancellation = new CancellationTokenSource();
        _quickCancellation = cancellation;
        var task = RunQuickAsync(sessionId, cancellation);
        _quickTask = task;
        return CompleteQuickAsync(task, cancellation);
    }

    private async Task<QuickCaptureOutcome> CompleteQuickAsync(Task<QuickCaptureOutcome> task, CancellationTokenSource cancellation)
    {
        try
        {
            return await task;
        }
        finally
        {
            if (ReferenceEquals(_quickTask, task))
                _quickTask = null;
            if (ReferenceEquals(_quickCancellation, cancellation))
                _quickCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<QuickCaptureOutcome> RunQuickAsync(Guid sessionId, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        Bitmap? bitmap = null;
        FullscreenBypass? bypass = null;
        var bypassActivated = false;
        var copied = false;
        var historySaved = false;
        string? historyPath = null;
        var status = QuickCaptureStatus.AllEnginesFailed;

        try
        {
            if (!_coordinator.TryTransition(sessionId, CaptureSessionState.Selecting, "quick-prepared"))
                return CanceledOutcome(sessionId, 0, status, bypassActivated);

            // 没有独立的 Core begin-attempt API：进入采集态只走既有 TryTransition（不改 Core、不动 Overlay wrapper 契约）。
            // 上下文沿用 wrapper 的 attempt:<trigger> 形式，日志类别与取景侧一致；被拒（已抢占/已封口/代际已变）即本代终止：
            // 先按本代 Guid 以 cancel 收尾（不碰新代），再让取消令牌脱身。
            if (!_coordinator.TryTransition(sessionId, CaptureSessionState.Capturing, "attempt:quick"))
            {
                EndSession(sessionId, "cancel");
                App.Log($"Capture: quick capture transition denied session={SessionText(sessionId)}");
                CancelQuickWork();
                return CanceledOutcome(sessionId, 0, status, bypassActivated);
            }

            var (hMonitor, monitor) = GetCursorMonitor();
            if (monitor.Width <= 0 || monitor.Height <= 0)
            {
                App.Log("Capture: quick monitor rect invalid, abort");
                _coordinator.TryMarkFailed(sessionId, CaptureFailure.Unavailable, "monitor rect invalid");
                return FinishQuickFailed(sessionId, status, bypassActivated);
            }

            _currentMonitor = hMonitor;
            WgcWindowCapture.EnsureMonitorPool(hMonitor);

            var direct = await CaptureMonitorBestAsync(hMonitor, monitor);
            bitmap = direct.Bitmap;
            status = direct.Status;
            var engine = direct.Engine;
            var printWindowTimedOut = direct.PrintWindowTimedOut;

            // 首次直接抓不可用才启用旁路；旁路把独占全屏拉回可抓状态，150ms 后只重试一次。
            if (bitmap is null && !cancellationToken.IsCancellationRequested && _coordinator.IsCurrent(sessionId))
            {
                try
                {
                    bypass = await FullscreenBypass.BeginAsync(
                        sessionId,
                        () => _coordinator.IsCurrent(sessionId),
                        cancellationToken,
                        force: true);
                }
                catch (OperationCanceledException)
                {
                    return CanceledOutcome(sessionId, 0, status, bypassActivated);
                }
                catch (Exception ex)
                {
                    App.Log($"Capture: quick bypass begin failed {ex.GetType().Name}: {ex.Message}");
                }

                if (bypass is not null && !bypass.IsCanceled)
                {
                    // 只有旁路真的把独占全屏窗口抢回前台才算激活；BeginAsync 内部超时会自行 Invalidate 并置 false。
                    bypassActivated = bypass.Activated;

                    // 等待前先核本次代际：已被取消/抢占就不再等，交给 finally 释放旁路。
                    if (!_coordinator.IsCurrent(sessionId) || cancellationToken.IsCancellationRequested)
                        return CanceledOutcome(sessionId, 0, status, bypassActivated);

                    await Task.Delay(150, cancellationToken);

                    // Delay 回来可能已过期：重试取图前必须再核 token/Current，避免把过期帧当本次结果。
                    if (!_coordinator.IsCurrent(sessionId) || cancellationToken.IsCancellationRequested)
                        return CanceledOutcome(sessionId, 0, status, bypassActivated);

                    var retried = await CaptureMonitorBestAsync(hMonitor, monitor);
                    bitmap = retried.Bitmap;
                    status = retried.Status;
                    engine = retried.Engine;
                    printWindowTimedOut = retried.PrintWindowTimedOut;
                }
            }

            // 每个 await 回来都核当前代际/owner；被取消、抢占或退出封口时只释放，不发成功。
            if (!_coordinator.IsCurrent(sessionId) || cancellationToken.IsCancellationRequested)
            {
                bitmap?.Dispose();
                bitmap = null;
                return CanceledOutcome(sessionId, 0, status, bypassActivated);
            }

            if (bitmap is null)
            {
                DisposeQuickBypass(bypass);
                bypass = null;
                _coordinator.TryMarkFailed(sessionId, CaptureFailure.Unavailable, $"status={status}");
                return FinishQuickFailed(sessionId, status, bypassActivated);
            }

            byte[] png;
            try
            {
                DrawCursorIfEnabled(bitmap, monitor);
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                png = stream.ToArray();
            }
            finally
            {
                bitmap.Dispose();
                bitmap = null;
            }

            // 编码前后再校验；取消结果不显示提示。
            if (!_coordinator.IsCurrent(sessionId) || cancellationToken.IsCancellationRequested)
                return CanceledOutcome(sessionId, png.Length, status, bypassActivated);

            if (!_coordinator.TryMarkSucceeded(sessionId, CaptureOrigin.Normal, engine, printWindowTimedOut))
                return CanceledOutcome(sessionId, png.Length, status, bypassActivated);

            var latch = _coordinator.TryLatchCommit(sessionId, out _);
            if (latch != CaptureArbitration.Granted)
            {
                // AlreadyLatched 不得重复执行；退出封口/代际变化同样不启动不可逆写入。
                App.Log($"Capture: quick commit latch {latch} session={SessionText(sessionId)}, writes skipped");
                return CanceledOutcome(sessionId, png.Length, status, bypassActivated);
            }

            // 旁路必须在本次 Quick End 之前、在创建它的 UI 线程上正常 Dispose（还焦点）。
            DisposeQuickBypass(bypass);
            bypass = null;

            var historyEnabled = App.Config.Current.SaveHistory;
            var clipboardTask = CommitClipboardAsync(sessionId, png);
            Task<(bool Saved, string? Path)>? historyTask = historyEnabled
                ? CommitHistoryAsync(sessionId, png)
                : null;

            if (historyTask is null)
            {
                copied = await clipboardTask;
            }
            else
            {
                // 两个 wrapper 各自吞异常，等两条都结束；先失败者成为主 Failure，双失败 Detail 都保留。
                await Task.WhenAll(clipboardTask, historyTask);
                copied = await clipboardTask;
                (historySaved, historyPath) = await historyTask;
            }

            var terminal = EndSession(sessionId, "quick-success");
            var result = terminal?.Result ?? CanceledResult(sessionId);
            return new QuickCaptureOutcome(
                QuickCaptureDisposition.Success,
                png.Length,
                status,
                copied,
                historySaved,
                historyPath,
                bypassActivated,
                result);
        }
        catch (OperationCanceledException)
        {
            App.Log($"Capture: quick canceled session={SessionText(sessionId)}");
            return CanceledOutcome(sessionId, 0, status, bypassActivated);
        }
        catch (Exception ex)
        {
            DisposeQuickBypass(bypass);
            bypass = null;
            App.Log($"Capture: quick failed {ex.GetType().Name}: {ex.Message}");

            if (_coordinator.IsCurrent(sessionId))
            {
                _coordinator.TryMarkFailed(sessionId, CaptureFailure.Unavailable, $"{ex.GetType().Name}: {ex.Message}");
                return FinishQuickFailed(sessionId, status, bypassActivated);
            }

            return CanceledOutcome(sessionId, 0, status, bypassActivated);
        }
        finally
        {
            bitmap?.Dispose();
            DisposeQuickBypass(bypass);
        }
    }

    private QuickCaptureOutcome FinishQuickFailed(Guid sessionId, QuickCaptureStatus status, bool bypassActivated)
    {
        var terminal = EndSession(sessionId, "quick-failed");
        return new QuickCaptureOutcome(
            QuickCaptureDisposition.Failed,
            0,
            status,
            false,
            false,
            null,
            bypassActivated,
            terminal?.Result ?? new CaptureResult(
                sessionId,
                CaptureOrigin.None,
                CaptureEngine.None,
                CaptureFailure.Unavailable,
                false,
                0,
                null));
    }

    private static QuickCaptureOutcome IgnoredOutcome()
        => new(
            QuickCaptureDisposition.Ignored,
            0,
            QuickCaptureStatus.AllEnginesFailed,
            false,
            false,
            null,
            false,
            // Ignored 不是成功：Failure.None 会让 CaptureResult.IsSuccess 误真，这里记非成功 Failure；UI 仍按 Disposition.Ignored 只 log。
            new CaptureResult(
                Guid.Empty,
                CaptureOrigin.None,
                CaptureEngine.None,
                CaptureFailure.Canceled,
                false,
                0,
                "quick-ignored"));

    private static QuickCaptureOutcome CanceledOutcome(
        Guid sessionId,
        int pngBytes,
        QuickCaptureStatus status,
        bool bypassActivated)
        => new(
            QuickCaptureDisposition.Canceled,
            pngBytes,
            status,
            false,
            false,
            null,
            bypassActivated,
            CanceledResult(sessionId));

    private static CaptureResult CanceledResult(Guid sessionId)
        => new(sessionId, CaptureOrigin.None, CaptureEngine.None, CaptureFailure.Canceled, false, 0, null);

    private static void DisposeQuickBypass(FullscreenBypass? bypass)
    {
        if (bypass is null)
            return;

        try
        {
            bypass.Dispose();
        }
        catch (Exception ex)
        {
            App.Log($"Capture: quick bypass dispose failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CancelQuickWork()
    {
        try
        {
            _quickCancellation?.Cancel();
        }
        catch (Exception ex)
        {
            // 已收尾的 Quick 不再需要取消；但取消回调抛异常也不能让 Shutdown/预留清理丢失。
            SafeLog($"Capture: quick cancel callback failed {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<bool> CommitClipboardAsync(Guid sessionId, byte[] png)
    {
        try
        {
            var written = await ClipboardService.SetImageAsync(png);
            if (!written)
            {
                App.Log($"Capture: quick clipboard reported failure session={SessionText(sessionId)}");
                _coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.ClipboardFailed);
            }

            return written;
        }
        catch (Exception ex)
        {
            App.Log($"Capture: quick clipboard failed session={SessionText(sessionId)} {ex.GetType().Name}: {ex.Message}");
            _coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.ClipboardFailed);
            return false;
        }
    }

    private async Task<(bool Saved, string? Path)> CommitHistoryAsync(Guid sessionId, byte[] png)
    {
        try
        {
            var path = await CaptureHistoryService.Instance.AddAsync(
                png,
                HistoryCaptureMode.Normal,
                meta: HistoryCaptureMeta.ScreenQuick);
            if (path is null)
            {
                // 历史已启用却返回 null：可能 PNG 已落盘但后续步骤失败，只能记通用 HistoryFailed，不声称回滚。
                App.Log($"Capture: quick history returned null session={SessionText(sessionId)}");
                _coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.HistoryFailed);
                return (false, null);
            }

            return (true, path);
        }
        catch (Exception ex)
        {
            App.Log($"Capture: quick history failed session={SessionText(sessionId)} {ex.GetType().Name}: {ex.Message}");
            _coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.HistoryFailed);
            return (false, null);
        }
    }
}
