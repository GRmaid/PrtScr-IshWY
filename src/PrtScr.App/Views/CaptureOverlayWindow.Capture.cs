using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using PrtScr.App.Services;
using PrtScr.Capture;
using PrtScr.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;

namespace PrtScr.App.Views;

/// <summary>冻屏取景覆盖窗：键盘/确认/取消、裁剪与保存流程。</summary>
public sealed partial class CaptureOverlayWindow
{
    /// <summary>本次取图的真实内容来源（A02）：只有窗口本体捕获才算窗口，屏幕裁切一律算屏幕。</summary>
    private enum CaptureContentOrigin
    {
        Screen,
        Window,
    }

    /// <summary>取图成功的结果：位图、来源、历史分类与降级提示同源，避免再靠模式/目标猜测来源（A02）。</summary>
    private readonly record struct SelectionCapture(
        Bitmap Bitmap,
        CaptureContentOrigin Origin,
        HistoryCaptureMode Mode,
        string ProcessName,
        string WindowTitle,
        string? Notice);

    private enum CaptureOutcomeStatus
    {
        Succeeded,
        Busy,
        Timeout,
        Canceled,
        Failed,
    }

    /// <summary>取图结果：成功带 SelectionCapture；失败带可判别状态与给主人看的原因（A02/A05）。</summary>
    private readonly record struct CaptureOutcome(
        CaptureOutcomeStatus Status,
        SelectionCapture? Capture,
        string? Detail)
    {
        public static CaptureOutcome Success(SelectionCapture capture)
            => new(CaptureOutcomeStatus.Succeeded, capture, capture.Notice);

        public static CaptureOutcome Abort(CaptureOutcomeStatus status, string? detail)
            => new(status, null, detail);
    }

    /// <summary>低级键盘钩子回调（钩子线程）：只切回 UI 线程，绝不做其他事。</summary>
    private void OnOverlayHotkey(int vk, bool ctrl)
    {
        DispatcherQueue.TryEnqueue(() => HandleOverlayHotkey(vk, ctrl));
    }

    /// <summary>取景窗可见时的键盘直达，不依赖 XAML 焦点。</summary>
    private void HandleOverlayHotkey(int vk, bool ctrl)
    {
        if (_closed)
            return;

        switch (vk)
        {
            case 0x1B: // Esc
                Cancel();
                break;
            case 0x0D: // Enter
                _ = ConfirmCurrentTargetAsync();
                break;
            case 0x09: // Tab
                ToggleCaptureMode();
                break;
            case OverlayPrevWindowKey: // 取景器内：上一个窗口
                CycleHover(-1);
                break;
            case OverlayNextWindowKey: // 取景器内：下一个窗口
                CycleHover(1);
                break;
            case 0x4F: // O：OCR 选区
                if (_hasSelection)
                    _ = OcrSelectionAsync();
                break;
            case 0x43 when ctrl: // Ctrl+C
                if (_hasSelection)
                    _ = ConfirmAsync();
                else
                    _ = CopyLoupeColorAsync();
                break;
            case 0x53 when ctrl: // Ctrl+S
                if (_hasSelection)
                    _ = SaveSelectionAsync();
                break;
        }
    }

    /// <summary>焦点锚的键盘直达：Esc/回车/Tab 全部不依赖窗口焦点漂移。</summary>
    private void FocusSink_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                Cancel();
                break;

            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                _ = ConfirmCurrentTargetAsync();
                break;

            case Windows.System.VirtualKey.Tab:
                e.Handled = true;
                ToggleCaptureMode();
                break;
        }
    }

    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Cancel();
    }

    private void OnEnter(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = ConfirmCurrentTargetAsync();
    }

    // ---------- 收尾 ----------

    /// <summary>取消本次取景：先让会话身份失效，晚到的取图与写入都会自行丢弃（A03/A05）。</summary>
    public void Cancel()
    {
        var session = CurrentSessionId();
        if (session != Guid.Empty)
            CaptureService.Instance.InvalidateCaptureSession(session);
        App.Log($"Overlay: cancel session={SessionText(session)}");
        // 即使窗口已经藏起来（确认/另存飞行期）也必须走收尾漏斗（P-04b）。
        Dismiss(session, "cancel");
    }

    /// <summary>CaptureService 为每次取景发的会话身份（A05）。</summary>
    private static Guid CurrentSessionId() => CaptureService.Instance.CurrentCaptureSessionId;

    /// <summary>会话是否仍是当前这次取景（新截图开始或取消后失效）。</summary>
    private static bool IsCurrentSession(Guid session) => CaptureService.Instance.IsCurrentCaptureSession(session);

    /// <summary>日志用会话文本：已失效的会话在日志里仍要能追溯到是哪一次取景（P-04b）。</summary>
    private static string SessionText(Guid session) => session == Guid.Empty ? "<none>" : session.ToString("N");

    /// <summary>
    /// 确认截图时先把取景窗收掉：冻结图已经够用，没必要让遮罩/选区一直停在屏幕上
    /// 等裁剪、PNG 编码、写剪贴板和落历史——那会看起来像“回车卡住”。
    /// _closed 置位后重复按键不会再次进入。
    /// </summary>
    private void HideForCapture()
    {
        if (_closed)
            return;
        _closed = true;
        StopEscPolling();
        EscOverPinHook.SetOverlayKeysActive(false);
        AppWindow.Hide();
        try
        {
            HideColorLoupe();
            _toastTimer?.Stop();
            CopyToast.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.Log($"Overlay hide 异常: {ex.Message}");
        }
        App.Log("Overlay: hidden for capture");
    }

    /// <summary>
    /// 唯一收尾漏斗（P-04b）：释放冻结图，并通过 CaptureService.EndCaptureSession
    /// 清 _isCapturing、注销取景期 Esc、通知 WGC 池。幂等，会话失效后仍可调用。
    /// </summary>
    private void FinishCaptureSession(Guid session, string reason)
    {
        if (_sessionFinished)
        {
            App.Log($"Overlay: session {SessionText(session)} finish ignored (already finished, reason={reason})");
            return;
        }
        _sessionFinished = true;

        var before = GC.GetTotalMemory(false);
        try
        {
            Frozen.Source = null;
            _full.Dispose();
            _full = new Bitmap(1, 1);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay cleanup 异常: {ex.Message}");
        }

        // 2560×1600 的 WriteableBitmap + GDI Bitmap 各约 16 MB，PixelBuffer 会进大对象堆。
        // 但不能在 UI 线程上阻塞式 GC——那正是下一次截图“卡一下”的来源；放到后台延迟压缩。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180);
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                App.Log($"Overlay: frozen buffers released, managed={before / 1048576}->{GC.GetTotalMemory(false) / 1048576}MB");
            }
            catch (Exception ex)
            {
                App.Log($"Overlay GC 异常: {ex.Message}");
            }
        });

        CaptureService.Instance.EndCaptureSession(session, reason);
        App.Log($"Overlay: session {SessionText(session)} finished reason={reason}");
        Dismissed?.Invoke();
    }

    /// <summary>
    /// 隐藏窗口（复用）并收尾。窗口已经藏起来时不能早退：确认飞行期主人再按 F1/Esc
    /// 时正是这条路径，早退会让 _isCapturing 与取景期 Esc 永远留着（P-04b）。
    /// </summary>
    private void Dismiss(Guid session, string reason)
    {
        if (_closed)
        {
            App.Log($"Overlay: dismiss while hidden session={SessionText(session)} reason={reason}");
            FinishCaptureSession(session, reason);
            return;
        }

        HideForCapture();
        FinishCaptureSession(session, reason);
    }

    /// <summary>把选区按“窗口矩形 → 捕获位图”的比率映射到窗口图上并裁剪。</summary>
    private Bitmap? CropFromWindow(Bitmap windowBitmap, WindowInfo window)
    {
        var sel = _selection;
        if (sel.IsEmpty || windowBitmap.Width <= 0 || windowBitmap.Height <= 0)
            return null;

        var frame = window.FrameBounds;
        if (frame.Width <= 0 || frame.Height <= 0)
            return null;

        // 选区 DIP → 显示器本地物理 → 虚拟屏物理 → 窗口内相对坐标
        var vx0 = _monitor.X + sel.X * _scale;
        var vy0 = _monitor.Y + sel.Y * _scale;
        var vx1 = _monitor.X + (sel.X + sel.Width) * _scale;
        var vy1 = _monitor.Y + (sel.Y + sel.Height) * _scale;

        // 捕获位图与窗口矩形可能存在几像素边框差，按比率归一化
        var sx = windowBitmap.Width / (double)frame.Width;
        var sy = windowBitmap.Height / (double)frame.Height;

        var x0 = (int)Math.Round((vx0 - frame.X) * sx);
        var y0 = (int)Math.Round((vy0 - frame.Y) * sy);
        var x1 = (int)Math.Round((vx1 - frame.X) * sx);
        var y1 = (int)Math.Round((vy1 - frame.Y) * sy);

        x0 = Math.Clamp(x0, 0, windowBitmap.Width - 1);
        y0 = Math.Clamp(y0, 0, windowBitmap.Height - 1);
        x1 = Math.Clamp(x1, x0 + 1, windowBitmap.Width);
        y1 = Math.Clamp(y1, y0 + 1, windowBitmap.Height);

        var cropped = windowBitmap.Clone(
            new System.Drawing.Rectangle(x0, y0, x1 - x0, y1 - y0),
            PixelFormat.Format32bppArgb);
        App.Log($"Capture: 窗口局部 {x1 - x0}×{y1 - y0}（窗口 {frame.Width}×{frame.Height}，捕获图 {windowBitmap.Width}×{windowBitmap.Height}）");
        return cropped;
    }

    /// <summary>
    /// 按当前选区/窗口取回一张位图，并带上「实际内容来源」与历史分类（A02）。
    /// 窗口捕获失败但屏幕裁切可用时，结果一律标成屏幕来源（历史=常规）并保留降级原因；
    /// Busy/超时/取消是可判别状态：不静默降级、不落历史、不写剪贴板（A02/A03）。
    /// </summary>
    private async Task<CaptureOutcome> CaptureSelectionAsync(Guid session)
    {
        // 常规截图：不重建任何窗口，直接裁冻结画面——菜单、悬停提示、下拉框、指针所见即所得
        if (_captureMode == CaptureMode.Normal)
        {
            var screenOnly = CropFullScreenSelection();
            return screenOnly is null
                ? CaptureOutcome.Abort(CaptureOutcomeStatus.Failed, "没有可用的选区")
                : CaptureOutcome.Success(new SelectionCapture(
                    screenOnly, CaptureContentOrigin.Screen, HistoryCaptureMode.Normal,
                    string.Empty, string.Empty, null));
        }

        string? degradeReason = null;

        // 窗口局部：先抓完整窗口（抗遮挡），再按选区裁剪
        if (_subregionWindow is { } sub)
        {
            var result = await WindowCapture.CaptureDetailedAsync(sub).ConfigureAwait(true);
            if (!CanContinue(session))
            {
                result.Bitmap?.Dispose();
                return CaptureOutcome.Abort(CaptureOutcomeStatus.Canceled, "取景已取消");
            }

            if (result.Bitmap is { } fullWindow)
            {
                using (fullWindow)
                {
                    var cropped = CropFromWindow(fullWindow, sub);
                    if (cropped is not null)
                    {
                        App.Log($"Capture: 窗口局部成功 source={result.Source} timedOut={result.PrintWindowTimedOut}");
                        return CaptureOutcome.Success(new SelectionCapture(
                            cropped, CaptureContentOrigin.Window, HistoryCaptureMode.Window,
                            sub.ProcessName, sub.Title, result.PrintWindowTimedOut ? TimedOutNotice() : null));
                    }
                }

                degradeReason = "窗口局部选区为空";
                App.Log("Capture: 窗口局部裁剪为空，回退屏幕裁剪");
            }
            else
            {
                var abort = ToAbort(result, "窗口局部");
                if (abort is not null)
                    return abort.Value;
                degradeReason = result.Detail ?? $"窗口局部捕获失败（{result.Failure}）";
                App.Log($"Capture: 窗口局部捕获失败 failure={result.Failure} detail={result.Detail}，回退屏幕裁剪");
            }
        }

        // 窗口模式：PrintWindow 直接取窗口自身内容（抗遮挡，被挡住的部分也完整），
        // 失败（不支持/全黑）则回退 WGC；同一窗口刚才已经失败过就不再重复发起。
        var subAlreadyFailed = _subregionWindow is { } failedSub
            && degradeReason is not null
            && _clickedWindow is { } clickedSame
            && failedSub.Handle == clickedSame.Handle;
        if (_clickedWindow is { } win && !subAlreadyFailed)
        {
            var result = await WindowCapture.CaptureDetailedAsync(win).ConfigureAwait(true);
            if (!CanContinue(session))
            {
                result.Bitmap?.Dispose();
                return CaptureOutcome.Abort(CaptureOutcomeStatus.Canceled, "取景已取消");
            }

            if (result.Bitmap is { } windowBitmap)
            {
                App.Log($"Capture: 窗口捕获成功 source={result.Source} timedOut={result.PrintWindowTimedOut}");
                return CaptureOutcome.Success(new SelectionCapture(
                    windowBitmap, CaptureContentOrigin.Window, HistoryCaptureMode.Window,
                    win.ProcessName, win.Title, result.PrintWindowTimedOut ? TimedOutNotice() : null));
            }

            var abort = ToAbort(result, "窗口");
            if (abort is not null)
                return abort.Value;
            degradeReason = result.Detail ?? $"窗口捕获失败（{result.Failure}）";
            App.Log($"Capture: 窗口捕获失败 failure={result.Failure} detail={result.Detail}，回退屏幕裁剪");
        }

        // 窗口内容拿不到：改用屏幕区域裁切，来源必须是屏幕、历史按常规，禁止再按窗口落历史（A02）
        if (!CanContinue(session))
            return CaptureOutcome.Abort(CaptureOutcomeStatus.Canceled, "取景已取消");

        var fallback = CropFullScreenSelection();
        if (fallback is null)
            return CaptureOutcome.Abort(CaptureOutcomeStatus.Failed, "没有可用的选区");

        var reason = degradeReason;
        if (reason is null)
        {
            // 窗口模式下自由拖拽、没命中任何窗口：本来裁的就是屏幕，不是失败降级，不提示
            App.Log("Capture: 无窗口目标，按屏幕区域裁切");
            return CaptureOutcome.Success(new SelectionCapture(
                fallback, CaptureContentOrigin.Screen, HistoryCaptureMode.Normal,
                string.Empty, string.Empty, null));
        }

        App.Log($"Capture: 窗口捕获降级为屏幕区域裁切：{reason}");
        return CaptureOutcome.Success(new SelectionCapture(
            fallback, CaptureContentOrigin.Screen, HistoryCaptureMode.Normal,
            string.Empty, string.Empty, $"窗口捕获失败，已按常规截图保存（{reason}）"));
    }

    /// <summary>await 之后继续取图前的会话校验：会话已被新截图替换或已被取消都算结束（A05）。</summary>
    private bool CanContinue(Guid session) => IsCurrentSession(session) && !_sessionFinished;

    /// <summary>PrintWindow 超期但 WGC 取到窗口内容：来源仍是窗口，但要提示降级（P-07 契约）。</summary>
    private static string TimedOutNotice() => "窗口响应较慢，已改用 WGC 抓取窗口内容";

    /// <summary>把窗口捕获的失败原因分成「可报错终止（Busy/超时/取消）」与「可屏幕降级」两类（A02）。</summary>
    private CaptureOutcome? ToAbort(WindowCaptureResult result, string where)
    {
        switch (result.Failure)
        {
            case WindowCaptureFailure.Busy:
                App.Log($"Capture: {where}捕获遇到 Busy，不降级");
                return CaptureOutcome.Abort(CaptureOutcomeStatus.Busy, "该窗口上一张截图还在处理中，请稍后重试");
            case WindowCaptureFailure.Canceled:
                App.Log($"Capture: {where}捕获已取消");
                return CaptureOutcome.Abort(CaptureOutcomeStatus.Canceled, "取景已取消");
            case WindowCaptureFailure.Timeout:
                App.Log($"Capture: {where}捕获超时且 WGC 失败 detail={result.Detail}");
                return CaptureOutcome.Abort(CaptureOutcomeStatus.Timeout, "窗口响应超时，本次未截图；请稍后重试");
            default:
                return null;
        }
    }

    /// <summary>按选区从冻结画面裁切（DIP → 显示器本地物理坐标，夹在显示器内）。</summary>
    private Bitmap? CropFullScreenSelection()
    {
        var dip = _selection;
        if (dip.IsEmpty)
            return null;

        var x = Math.Clamp((int)Math.Round(dip.X * _scale), 0, _monitor.Width - 1);
        var y = Math.Clamp((int)Math.Round(dip.Y * _scale), 0, _monitor.Height - 1);
        var right = Math.Clamp((int)Math.Round((dip.X + dip.Width) * _scale), x + 1, _monitor.Width);
        var bottom = Math.Clamp((int)Math.Round((dip.Y + dip.Height) * _scale), y + 1, _monitor.Height);

        return _full.Clone(
            new System.Drawing.Rectangle(x, y, right - x, bottom - y),
            PixelFormat.Format32bppArgb);
    }

    /// <summary>对当前选区做 OCR（O 键）：取图与触发前都校验会话，旧会话的识别请求不会发出（A05）。</summary>
    private async Task OcrSelectionAsync()
    {
        var session = CurrentSessionId();
        Bitmap? captured = null;
        try
        {
            var outcome = await CaptureSelectionAsync(session);
            if (!CanContinue(session))
            {
                App.Log($"Capture: session {session:N} 的 OCR 取图晚到，已丢弃（当前 {CurrentSessionId():N}）");
                outcome.Capture?.Bitmap.Dispose();
                return;
            }

            if (outcome.Capture is not { } selection)
            {
                if (outcome.Status != CaptureOutcomeStatus.Canceled)
                    ShowCopyToast(outcome.Detail ?? "识别失败，请重试");
                return;
            }

            captured = selection.Bitmap;
            using var ms = new MemoryStream();
            captured.Save(ms, ImageFormat.Png);
            var bytes = ms.ToArray();

            if (!CanContinue(session))
            {
                App.Log($"Capture: session {session:N} 的 OCR 编码晚到，已丢弃（当前 {CurrentSessionId():N}）");
                return;
            }

            App.Log($"Capture: OCR 取图来源={selection.Origin}（{selection.Mode}）");
            Dismiss(session, "ocr");
            await OcrUiService.RecognizeAndShowAsync(bytes);
        }
        catch (Exception ex)
        {
            App.Log($"OCR 选区失败: {ex}");
        }
        finally
        {
            captured?.Dispose();
        }
    }

    /// <summary>
    /// 回车确认当前目标：已有选区走原确认流程；只有滚轮/悬停高亮时，
    /// 把高亮窗口当作整窗选区再确认，这样滚轮选完不必再点一下。
    /// </summary>
    private async Task ConfirmCurrentTargetAsync()
    {
        if (_closed || _confirming)
            return;

        _confirming = true;
        try
        {
            if (!_hasSelection)
            {
                if (_hoverWindow is null)
                    return;

                _clickedWindow = _hoverWindow;
                _selection = DipRect(_clickedWindow.FrameBounds);
                _hasSelection = true;
                _subregionWindow = null;
                // 不在这里 UpdateVisuals：回车是“直接截当前高亮窗口”，
                // 若先画选区边框/遮罩，窗口捕获的几十毫秒里主人会看到亮框先缩到窗口再截图。
                // 保持原有悬停高亮，等 ConfirmAsync 捕获完直接 Dismiss。
                App.Log($"Overlay: enter captures hovered window 0x{_clickedWindow.Handle:X} process=\"{_clickedWindow.ProcessName}\"");
            }

            await ConfirmAsync();
        }
        finally
        {
            _confirming = false;
        }
    }

    private async Task ConfirmAsync()
    {
        // 贴图窗已经藏起来了（HideForCapture）说明这次确认已在处理中：
        // 拦住 Ctrl+C / 按钮的重复触发，避免同一会话写两条历史（A05）。
        if (_closed)
        {
            App.Log("Capture: confirm ignored (overlay already hidden)");
            return;
        }

        // 每次取景的会话身份：所有 await 之后的写入都要先确认自己仍属于这次取景（A05）。
        var session = CurrentSessionId();
        if (session == Guid.Empty)
        {
            App.Log("Capture: confirm ignored (no active capture session)");
            return;
        }

        Bitmap? captured = null;
        var ended = false;
        var keepOverlay = false;
        // 局部所有权：从藏窗前就记下“这次确认负责收尾”，之后不再用全局会话判断，
        // 否则取消/新截图把会话判失效时 finally 会跳过收尾，_isCapturing 永久为真（P-04b）。
        var ownsFinish = false;
        try
        {
            // 先藏窗再取图：窗口捕获/PNG 编码期间主人不会再看到遮罩和选区。
            ownsFinish = true;
            HideForCapture();
            var outcome = await CaptureSelectionAsync(session);

            // 取图期间可能已经开始下一次截图，或主人按了 Esc：旧会话的结果一律丢弃（A05）
            if (!IsCurrentSession(session))
            {
                App.Log($"Capture: session {session:N} 的确认结果晚到，已丢弃（当前 {CurrentSessionId():N}）");
                outcome.Capture?.Bitmap.Dispose();
                return;
            }

            if (outcome.Capture is not { } selection)
            {
                if (outcome.Status == CaptureOutcomeStatus.Canceled)
                {
                    App.Log($"Capture: session {session:N} 取图已取消，不写历史/剪贴板");
                    return;
                }

                // Busy / 超时 / 失败：不静默降级、不落历史、不写剪贴板，恢复取景窗让主人直接重试
                var message = outcome.Detail ?? "截图失败，请重试";
                App.Log($"Capture: session {session:N} 取图失败 status={outcome.Status}：{message}");
                keepOverlay = RestoreOverlayAfterCaptureFailure(message);
                return;
            }

            captured = selection.Bitmap;

            // 图已经拿到，冻结缓冲可以释放，取景服务也立即进入空闲状态；
            // 后面的编码/剪贴板/历史都在后台，不挡下一次热键。
            FinishCaptureSession(session, "confirm-success");
            ended = true;

            App.Log(selection.Origin == CaptureContentOrigin.Screen
                ? $"Capture: 本次来源=屏幕（历史按常规） notice={selection.Notice}"
                : $"Capture: 本次来源=窗口 process={selection.ProcessName} notice={selection.Notice}");

            var bytes = await Task.Run(() => EncodePng(captured));

            // 编码是后台耗时步骤：回来时可能已经是下一次截图的会话（A05）
            if (!IsCurrentSession(session))
            {
                App.Log($"Capture: session {session:N} 的编码结果晚到，已丢弃（当前 {CurrentSessionId():N}）");
                return;
            }

            var copyTask = ClipboardService.SetImageAsync(bytes);
            var historyTask = CaptureHistoryService.Instance.AddAsync(
                bytes, selection.Mode, selection.ProcessName, selection.WindowTitle);
            await Task.WhenAll(copyTask, historyTask);

            var ok = copyTask.Result;
            App.Log(ok ? "Capture: copied" : "Capture: clipboard final failure");
            if (historyTask.Result is { } savedPath)
                App.Log($"Capture: history saved {savedPath}");

            // 后台写入期间可能已经换了下一次截图：旧会话不再弹收尾提示（A05）
            if (!IsCurrentSession(session))
            {
                App.Log($"Capture: session {session:N} 写入期间已换新会话，跳过收尾提示");
                return;
            }

            if (!ok)
            {
                ToastService.ShowCaptureFailure();
            }
            else if (selection.Notice is { } notice)
            {
                // 降级成功必须有可见说明：历史按实际来源记，界面也告诉主人为什么不是窗口图（A02）
                ToastService.ShowInfo(notice);
            }
            else
            {
                ToastService.ShowCaptureSuccess();
            }
        }
        catch (Exception ex)
        {
            App.Log($"截图确认失败: {ex}");
            ToastService.ShowCaptureFailure();
        }
        finally
        {
            captured?.Dispose();
            // 只按本次确认自己的局部所有权收尾，不再依赖 IsCurrentSession：
            // 取消路径已由 Cancel 收尾时这里是幂等空转（P-04b）。
            // Busy/超时恢复取景窗（keepOverlay）不算结束，会话保留供主人重试。
            if (!ended && !keepOverlay && ownsFinish)
                FinishCaptureSession(session, "confirm-end");
        }
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private void Capture_Click(object sender, RoutedEventArgs e) => _ = ConfirmAsync();

    private void CancelCapture_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (_hasSelection)
            _ = OcrSelectionAsync();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveSelectionAsync();

    private void OnSaveShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_hasSelection)
            _ = SaveSelectionAsync();
    }

    /// <summary>选区另存为 PNG/JPEG；保存成功后同样复制到剪贴板并写入历史，再关闭取景。</summary>
    private async Task SaveSelectionAsync()
    {
        if (!_hasSelection || _closed || _saving)
            return;

        // 另存有文件对话框这个长等待：从一开头记住会话，回来先确认还是同一次取景（A05）
        var session = CurrentSessionId();
        _saving = true;
        // 文件选择器会抢前台：期间暂停取景器按键，避免 Esc/Tab/回车被钩子当成取景操作。
        _keysSuspended = true;
        EscOverPinHook.SetOverlayKeysActive(false);
        Bitmap? captured = null;
        try
        {
            var outcome = await CaptureSelectionAsync(session);
            if (!CanContinue(session))
            {
                App.Log($"Capture: session {session:N} 的另存取图晚到，已丢弃（当前 {CurrentSessionId():N}）");
                outcome.Capture?.Bitmap.Dispose();
                return;
            }

            if (outcome.Capture is not { } selection)
            {
                if (outcome.Status != CaptureOutcomeStatus.Canceled)
                    ShowCopyToast(outcome.Detail ?? "另存失败，请重试");
                return;
            }

            captured = selection.Bitmap;
            using var ms = new MemoryStream();
            captured.Save(ms, ImageFormat.Png);
            var bytes = ms.ToArray();

            var path = await FileSaveService.SavePngAsync(
                WindowNative.GetWindowHandle(this), bytes, FileSaveService.SuggestedName("shot"));

            // 文件对话框可能开了很久：落盘之后先确认会话没换，再决定是否继续写剪贴板/历史
            if (!CanContinue(session))
            {
                App.Log($"Capture: session {session:N} 的另存结果晚到，已跳过剪贴板/历史（file={path}）");
                return;
            }

            if (path is null)
            {
                FocusSink.Focus(FocusState.Programmatic);
                return;
            }

            // 另存为不是“只写文件”：保存成功后仍要进剪贴板和截图历史，保持与回车确认一致
            var copyTask = ClipboardService.SetImageAsync(bytes);
            var historyTask = CaptureHistoryService.Instance.AddAsync(
                bytes, selection.Mode, selection.ProcessName, selection.WindowTitle);
            await Task.WhenAll(copyTask, historyTask);

            if (!IsCurrentSession(session))
            {
                App.Log($"Capture: session {session:N} 另存写入期间已换新会话，跳过收尾");
                return;
            }

            var copied = copyTask.Result;
            App.Log(copied ? "Capture: copied" : "Capture: clipboard final failure");
            App.Log($"Capture: saved as {path}");
            if (historyTask.Result is { } historyPath)
                App.Log($"Capture: history saved {historyPath}");

            if (!copied)
                ToastService.ShowCaptureFailure();
            else if (selection.Notice is { } notice)
                ToastService.ShowInfo(notice);
            else
                ToastService.ShowCaptureSuccess();
            Dismiss(session, "save");
        }
        catch (Exception ex)
        {
            App.Log($"另存为失败: {ex}");
            ToastService.ShowCaptureFailure();
        }
        finally
        {
            captured?.Dispose();
            _saving = false;
            _keysSuspended = false;
            if (!_closed)
            {
                EscOverPinHook.SetOverlayKeysActive(true);
                FocusSink.Focus(FocusState.Programmatic);
            }
        }
    }

    /// <summary>
    /// 取图失败（Busy/超时/失败）后把取景窗恢复出来：保留当前选区，主人可以直接重试，
    /// 而不是悄悄改成屏幕裁切或什么都不发生（A02）。
    /// </summary>
    private bool RestoreOverlayAfterCaptureFailure(string message)
    {
        if (_sessionFinished)
            return false;

        try
        {
            // HideForCapture 把 _closed 置真；恢复时反向还原，晚到的旧结果仍由会话校验挡住（A05）
            _closed = false;
            AppWindow.Show();
            EscOverPinHook.SetOverlayKeysActive(true);
            StartEscPolling();
            UpdateVisuals();
            ShowCopyToast(message);
            Activate();
            _ = FocusSink.Focus(FocusState.Programmatic);
            App.Log($"Overlay: 取图失败后恢复取景：{message}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"Overlay: 恢复取景失败 {ex}");
            return false;
        }
    }

}
