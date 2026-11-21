using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PrtScr.Capture;
using PrtScr.Interop;
using PrtScr.App.Views;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace PrtScr.App.Services;

/// <summary>
/// 截图会话协调者：热键 → 冻结全屏 → 打开取景覆盖窗 →（窗口内交互）→ 剪贴板。
/// v1 一次只取鼠标所在的那块显示器（微信同款行为）；跨显示器选区在 M7 完善。
/// </summary>
public sealed partial class CaptureService
{
    public static CaptureService Instance { get; } = new();

    private CaptureOverlayWindow? _overlay;
    private nint _currentMonitor;
    private Task? _overlayStartTask;
    private Task? _shutdownWait;

    // P-14b′：活动/Current/Owner/元数据/预留/退出只有一个真源；CTS 与运行中 Task 只作资源/等待句柄。
    private readonly CaptureSessionCoordinator _coordinator = new(SafeLog);

    private CaptureService()
    {
    }

    /// <summary>取景器（Overlay）是否占用会话槽；Quick 占槽不算，供 UI 决策。</summary>
    public bool IsOverlayCapturing
    {
        get
        {
            var snapshot = _coordinator.Snapshot();
            return snapshot.Active && snapshot.OwnerKind == CaptureSessionKind.Overlay;
        }
    }

    /// <summary>会话槽是否忙：有活动会话或取景预留（已结束的最近身份不算忙）。</summary>
    public bool IsCaptureBusy => _coordinator.IsBusy;

    /// <summary>旧入口兼容：旧语义只表示取景器在截图。Quick 占槽时不置位；新 UI 请按需改用 IsOverlayCapturing / IsCaptureBusy。</summary>
    public bool IsCapturing => IsOverlayCapturing;

    /// <summary>当前会话槽的状态（P-14b）；没有活动会话时为 Idle。</summary>
    public CaptureSessionState CurrentCaptureSessionState => _coordinator.State;

    /// <summary>当前这次取景的会话身份；新截图开始后旧身份自动失效（A05）。</summary>
    public Guid CurrentCaptureSessionId => _coordinator.CurrentSessionId;

    /// <summary>await 之后写内容/历史/剪贴板/OCR 之前先校验：不是最新会话就丢弃（A05）。</summary>
    public bool IsCurrentCaptureSession(Guid sessionId) => _coordinator.IsCurrent(sessionId);

    /// <summary>会话槽归属判定（F1）：取消只让 Current 失效，Owner 保留到下一次 Begin。</summary>
    internal bool IsOwnedCaptureSession(Guid sessionId) => _coordinator.IsOwner(sessionId);

    /// <summary>取消/放弃一次取景：让该会话所有晚到结果失效（A03/A05）。</summary>
    public void InvalidateCaptureSession(Guid sessionId) => _coordinator.Invalidate(sessionId);

    /// <summary>取景收尾的唯一漏斗：Core 统一写 finished → state → result；仅活动 Overlay 收尾才拆取景期 Esc/通知 WGC。</summary>
    internal void EndCaptureSession(Guid sessionId, string reason) => EndSession(sessionId, reason);

    internal void BeginCaptureSession(Guid sessionId) => _coordinator.Begin(sessionId, CaptureSessionKind.Overlay);

    /// <summary>取景窗已准备好：Preparing → Selecting。</summary>
    internal void MarkCapturePrepared(Guid sessionId)
        => _coordinator.TryTransition(sessionId, CaptureSessionState.Selecting, "prepared");

    /// <summary>一次取图动作开始：Selecting → Capturing（失败后恢复取景、再次确认时可重复）。</summary>
    internal void BeginCaptureAttempt(Guid sessionId, string trigger)
        => _coordinator.TryTransition(sessionId, CaptureSessionState.Capturing, $"attempt:{trigger}");

    /// <summary>取图成功：记录来源/引擎/超时标记，Capturing → Committing，并写结果日志。</summary>
    internal void MarkCaptureAttemptSucceeded(
        Guid sessionId,
        CaptureOrigin origin,
        CaptureEngine engine,
        bool printWindowTimedOut)
        => _coordinator.TryMarkSucceeded(sessionId, origin, engine, printWindowTimedOut);

    /// <summary>取图失败：记录失败分类与原因，写结果日志后 Capturing/Committing → Selecting（允许主人直接重试）。</summary>
    internal void MarkCaptureAttemptFailed(Guid sessionId, CaptureFailure failure, string? detail)
        => _coordinator.TryMarkFailed(sessionId, failure, detail);

    /// <summary>提交阶段回到取景（另存对话框取消、编码/保存异常）：Committing → Selecting。</summary>
    internal void ResetCaptureAttempt(Guid sessionId, string context)
        => _coordinator.TryTransition(sessionId, CaptureSessionState.Selecting, context);

    /// <summary>
    /// 退出第一步（UI 退出在 await 之前同步调用）：永久封口、取消未 latch 的预提交会话并释放取景预留。
    /// 只做同步纯规则与取消令牌，不等任何线程；等待预算由 <see cref="ShutdownAsync"/> 统一承担。
    /// </summary>
    public void BeginShutdown()
    {
        if (!_coordinator.BeginShutdown())
            return;

        var snapshot = _coordinator.Snapshot();
        SafeLog($"Capture: shutdown begin (owner={SessionText(snapshot.OwnerSessionId)}, kind={snapshot.OwnerKind}, latched={snapshot.CommitLatched})");

        if (snapshot.Active && !snapshot.CommitLatched && snapshot.OwnerSessionId != Guid.Empty)
            EndSession(snapshot.OwnerSessionId, "cancel");

        CancelQuickWork();
    }

    /// <summary>
    /// 等待本轮新增长任务收尾，合计最多 <see cref="CaptureSessionCoordinator.ShutdownGrace"/>，只等待一次。
    /// 不等待底层不可控的 native 抓取线程（它们晚到由各自的一次性后继释放）。
    /// </summary>
    public Task ShutdownAsync() => _shutdownWait ??= WaitForOwnedWorkAsync();

    private async Task WaitForOwnedWorkAsync()
    {
        var pending = new List<Task>(2);
        if (_quickTask is not null)
            pending.Add(_quickTask);
        if (_overlayStartTask is not null && !_overlayStartTask.IsCompleted)
            pending.Add(_overlayStartTask);

        if (pending.Count == 0)
            return;

        var all = Task.WhenAll(pending);
        var winner = await Task.WhenAny(all, Task.Delay(CaptureSessionCoordinator.ShutdownGrace));
        if (ReferenceEquals(winner, all))
        {
            try
            {
                await all;
            }
            catch (Exception ex)
            {
                App.Log($"Capture: shutdown observed pending failure {ex.GetType().Name}: {ex.Message}");
            }

            return;
        }

        App.Log($"Capture: shutdown wait grace {CaptureSessionCoordinator.ShutdownGrace.TotalMilliseconds:0}ms elapsed");
    }

    /// <summary>收尾漏斗；返回 Core 的终态快照（Active 是释放前事实）。被忽略/stale 返回 null。</summary>
    private CaptureSessionSnapshot? EndSession(Guid sessionId, string reason)
    {
        var terminal = _coordinator.End(sessionId, reason);
        if (terminal is null)
            return null;

        // 只对真正收尾的活动 Overlay 拆取景期 Esc 并通知 WGC 池；Quick 与 stale 收尾不触碰。
        if (terminal.OwnerKind == CaptureSessionKind.Overlay)
        {
            Hotkey?.EndCaptureMode();
            if (_currentMonitor != 0)
                WgcWindowCapture.NotifyOverlayClosed(_currentMonitor);
        }

        return terminal;
    }

    private static string SessionText(Guid sessionId)
        => sessionId == Guid.Empty ? "<none>" : sessionId.ToString("N");

    /// <summary>
    /// 服务日志入口：当前状态机日志在 Core 侧 Flush 时不捕异常，清理路径（退出/预留释放）也夹着日志，
    /// 日志自身失败绝不能破坏会话/预留/退出协调状态。
    /// </summary>
    private static void SafeLog(string line)
    {
        try
        {
            App.Log(line);
        }
        catch
        {
            // 日志失败只丢这一行，不影响协调状态。
        }
    }

    /// <summary>由 MainWindow 注入：用于取景期间接管全局 Esc。</summary>
    public HotkeyService? Hotkey { get; set; }

    /// <summary>启动时预创建取景窗（隐藏），让第一次 F1 也走复用路径。</summary>
    public void EnsureOverlay()
    {
        if (_overlay is not null)
            return;

        try
        {
            _overlay = new CaptureOverlayWindow();
            // 收尾统一走 CaptureOverlayWindow.FinishCaptureSession → EndCaptureSession（P-04b）；
            // Dismissed 只作为窗口层通知，不再在这里另设清零点。
            _overlay.WarmUpHidden();
            App.Log("Capture: overlay pre-created");
        }
        catch (Exception ex)
        {
            App.Log($"Capture: 预创建取景窗失败 {ex}");
        }
    }

    /// <summary>
    /// 在 UI 线程调用。仅代表真正的取景（Overlay）意图：活动取景按旧文案取消；
    /// 未 latch 的 Quick 才被抢占。取景预留与 Begin 都在首个 await 之前完成。
    /// </summary>
    public Task StartCaptureAsync()
    {
        var task = StartOverlayCoreAsync();
        _overlayStartTask = task;
        return task;
    }

    private async Task StartOverlayCoreAsync()
    {
        App.Log("Capture: start");

        var snapshot = _coordinator.Snapshot();
        if (snapshot.Active && snapshot.OwnerKind == CaptureSessionKind.Overlay)
        {
            var activeText = snapshot.CurrentSessionId == Guid.Empty ? "<none>" : snapshot.CurrentSessionId.ToString("N");
            App.Log($"Capture: already capturing, cancel session={activeText}");

            if (snapshot.State == CaptureSessionState.Preparing)
            {
                // Preparing 还没交给 Overlay：只取消旧会话会让新 Preparing 永远忙，必须失效本代并 End。
                if (snapshot.CurrentSessionId != Guid.Empty)
                    _coordinator.Invalidate(snapshot.CurrentSessionId);
                EndSession(snapshot.OwnerSessionId, "cancel");
            }
            else
            {
                _overlay?.Cancel();
            }

            return;
        }

        // 预留段：TryReserveOverlay 成功到 Begin 消费预留之间，任何异常（取消回调 AggregateException、
        // 服务日志、Core Flush 日志）都不得让本次预留泄漏；finally 必须按本代准确的 reservationId+generation 释放。
        var reservationId = Guid.Empty;
        long reservationGeneration = 0;
        var reservationConsumed = false;
        var sessionId = Guid.Empty;
        Bitmap? frozen = null;
        var frozenHandedOff = false;

        try
        {
            if (snapshot.Active && snapshot.OwnerKind == CaptureSessionKind.Quick)
            {
                // Quick 已 latch = 不可逆写入已开始，取景只忽略、不排队。
                if (snapshot.CommitLatched)
                {
                    App.Log($"Capture: overlay start ignored (quick commit latched, session={SessionText(snapshot.OwnerSessionId)})");
                    return;
                }

                if (!_coordinator.TryReserveOverlay(out reservationId, out var preemptedQuickId))
                {
                    App.Log($"Capture: overlay reservation denied (quick session={SessionText(snapshot.OwnerSessionId)})");
                    return;
                }

                reservationGeneration = _coordinator.OverlayReservationGeneration;

                // Core 已在同一同步边界内失效并按 cancel 收尾旧 Quick；这里只让仍在等待的采集脱身。
                // 取消回调抛异常也不能让预留/退出清理丢失：CancelQuickWork 自吞全部异常。
                CancelQuickWork();
                App.Log($"Capture: overlay preempted quick session={SessionText(preemptedQuickId)}");
            }
            else
            {
                if (!_coordinator.TryReserveOverlay(out reservationId, out _))
                {
                    App.Log("Capture: overlay reservation denied (busy or shutting down)");
                    return;
                }

                reservationGeneration = _coordinator.OverlayReservationGeneration;
            }

            sessionId = Guid.NewGuid();
            if (_coordinator.Begin(sessionId, CaptureSessionKind.Overlay, reservationId) is null)
            {
                App.Log($"Capture: overlay begin denied session={SessionText(sessionId)}");
                return;   // 预留由 finally 按本次代际释放
            }

            reservationConsumed = true;
            _currentMonitor = 0;

            // 冻结画面走常驻 WGC 池：零会话启停（根治按下瞬间的全屏爆闪），
            // 只需从池里舀最新一帧，~10ms；失败回退 BitBlt。
            var (hMonitor, monitor) = GetCursorMonitor();
            if (monitor.Width <= 0 || monitor.Height <= 0)
            {
                App.Log("Capture: monitor rect invalid, abort");
                EndSession(sessionId, "start-failed");
                return;
            }

            var pool = WgcWindowCapture.EnsureMonitorPool(hMonitor);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (captured, captureStatus, captureEngine, printWindowTimedOut) = await CaptureMonitorBestAsync(hMonitor, monitor);
            frozen = captured;
            if (frozen is null)
            {
                App.Log($"Capture: 所有采集引擎均失败 status={captureStatus}");
                EndSession(sessionId, "start-failed");
                return;
            }

            // await 回来先核当前代际/owner 再交接冻结帧；被取消/抢占/退出封口时只释放，不发成功。
            if (!_coordinator.IsCurrent(sessionId))
            {
                App.Log($"Capture: session {SessionText(sessionId)} frozen frame discarded (not current)");
                return;
            }

            _currentMonitor = hMonitor;
            DrawCursorIfEnabled(frozen, monitor);
            App.Log($"Capture: frozen {sw.ElapsedMilliseconds}ms pool={pool} status={captureStatus} engine={captureEngine} timedOut={printWindowTimedOut}");

            // 窗口枚举不碰 UI，与抓屏并行跑，别占用亮窗前的关键路径。
            var windows = await Task.Run(() => WindowEnumerator.EnumerateVisibleWindows());
            if (!_coordinator.IsCurrent(sessionId))
            {
                App.Log($"Capture: session {SessionText(sessionId)} window list discarded (not current)");
                return;
            }

            EnsureOverlay();
            if (_overlay is null)
            {
                EndSession(sessionId, "start-failed");
                return;
            }

            _overlay.Prepare(frozen, monitor, windows);
            // Prepare 第一件事就是把 frozen 挂成 overlay 的 _full；从这一步起释放责任归取景窗。
            frozenHandedOff = true;
            MarkCapturePrepared(sessionId);
            Hotkey?.BeginCaptureMode();
            App.Log($"Capture: overlay shown session={sessionId:N}");
        }
        catch (Exception ex)
        {
            // Begin 已生效之后才出错：End 只作用本代局部 Guid，绝不按全局 Current 清新槽/新代。
            if (reservationConsumed && sessionId != Guid.Empty)
                EndSession(sessionId, "start-failed");
            App.Log($"Capture: 异常 {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
        finally
        {
            if (!reservationConsumed && reservationId != Guid.Empty)
                _coordinator.ReleaseOverlayReservation(reservationId, reservationGeneration);

            if (!frozenHandedOff)
                frozen?.Dispose();
        }
    }

    /// <summary>
    /// 显示器采集：WGC 常驻池优先；若拿到疑似黑屏（游戏全屏常见）→ 回退 BitBlt。
    /// </summary>
    private static async Task<(Bitmap? Bitmap, QuickCaptureStatus Status, CaptureEngine Engine, bool PrintWindowTimedOut)> CaptureMonitorBestAsync(nint hMonitor, WindowRect monitor)
    {
        // 三态：看到过帧但全黑 => LikelyBlack；一个帧都没拿到 => AllEnginesFailed。
        var sawFrame = false;
        var foregroundTimedOut = false;
        Bitmap? wgc = null;
        try
        {
            wgc = await WgcWindowCapture.GetLatestMonitorFrameAsync(hMonitor);
        }
        catch (Exception ex)
        {
            App.Log($"Capture: WGC 抛异常 {ex.GetType().Name}: {ex.Message}");
        }

        if (wgc is not null)
        {
            sawFrame = true;
            if (!IsNearlyBlack(wgc))
            {
                App.Log($"Capture: engine=WGC {wgc.Width}×{wgc.Height}");
                return (wgc, QuickCaptureStatus.Success, CaptureEngine.Wgc, false);
            }

            App.Log("Capture: WGC 帧疑似黑屏，回退 DXGI");
            wgc.Dispose();
        }

        // 第二引擎：DXGI Desktop Duplication（微信 / Snipping Tool 同源，全屏游戏更可靠）
        var dxgi = await Task.Run(() => DesktopDuplicationCapture.Capture(hMonitor));
        if (dxgi is not null)
        {
            sawFrame = true;
            if (!IsNearlyBlack(dxgi))
            {
                App.Log($"Capture: engine=DXGI {dxgi.Width}×{dxgi.Height}");
                return (dxgi, QuickCaptureStatus.Success, CaptureEngine.Dxgi, false);
            }

            App.Log("Capture: DXGI 帧疑似黑屏，回退 BitBlt");
            dxgi.Dispose();
        }

        var fallback = CaptureMonitorByBitBlt(monitor);
        // fallback 的 owner 只在本方法内：作为返回值交出前，任何出口都不能丢下它（A10/P-09b）
        var fallbackOwned = fallback is not null;
        if (fallback is not null)
            sawFrame = true;
        try
        {
            if (fallback is not null && IsNearlyBlack(fallback))
            {
                // 部分全屏游戏对 CAPTUREBLT 返回黑屏，普通 BitBlt 反而能拿到画面
                App.Log("Capture: CAPTUREBLT 黑屏，尝试普通 BitBlt");
                fallback.Dispose();
                fallbackOwned = false;
                fallback = CaptureMonitorByBitBlt(monitor, includeLayered: false);
                fallbackOwned = fallback is not null;
                if (fallback is not null)
                    sawFrame = true;
            }

            if (fallback is not null && !IsNearlyBlack(fallback))
            {
                App.Log($"Capture: engine=BitBlt {fallback.Width}×{fallback.Height}");
                fallbackOwned = false;
                return (fallback, QuickCaptureStatus.Success, CaptureEngine.BitBlt, false);
            }

            // 最后一招：全屏游戏通常是一个"窗口"，用窗口捕获（PrintWindow 优先、WGC 回退）。
            var foreground = await CaptureForegroundWindowAsync(monitor);
            if (foreground is not null)
            {
                if (foreground.Bitmap is not null)
                {
                    sawFrame = true;
                    var foregroundEngine = MapWindowCaptureSource(foreground.Source);
                    bool foregroundBlack;
                    try
                    {
                        foregroundBlack = IsNearlyBlack(foreground.Bitmap);
                    }
                    catch
                    {
                        foreground.Bitmap.Dispose();
                        throw;
                    }

                    if (!foregroundBlack)
                    {
                        App.Log($"Capture: engine={foregroundEngine} {foreground.Bitmap.Width}×{foreground.Bitmap.Height} timedOut={foreground.PrintWindowTimedOut}");
                        return (foreground.Bitmap, QuickCaptureStatus.Success, foregroundEngine, foreground.PrintWindowTimedOut);
                    }

                    App.Log("Capture: 前台窗口帧疑似黑屏，丢弃");
                    foreground.Bitmap.Dispose();
                }
                else
                {
                    App.Log($"Capture: 前台窗口捕获失败 source={foreground.Source} failure={foreground.Failure} timedOut={foreground.PrintWindowTimedOut} detail={foreground.Detail}");
                }

                foregroundTimedOut = foreground.PrintWindowTimedOut;
            }

            // B-3b：不再把全黑 BitBlt/窗口帧当成功交出去。无可用帧由上层走失败提示，
            // 避免黑图进入取景、历史与剪贴板。
            App.Log("Capture: 所有采集引擎均失败或返回疑似黑屏");
            return (null, sawFrame ? QuickCaptureStatus.LikelyBlack : QuickCaptureStatus.AllEnginesFailed, CaptureEngine.None, foregroundTimedOut);
        }
        finally
        {
            if (fallbackOwned)
                fallback?.Dispose();
        }
    }

    private static async Task<WindowCaptureResult?> CaptureForegroundWindowAsync(WindowRect monitor)
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == 0 || !GetWindowRect(hwnd, out var rect))
                return null;

            // 只接受几乎铺满当前显示器的窗口（全屏游戏/全屏窗口）
            var width = rect.right - rect.left;
            var height = rect.bottom - rect.top;
            if (Math.Abs(width - monitor.Width) > 8 || Math.Abs(height - monitor.Height) > 8)
                return null;
            if (Math.Abs(rect.left - monitor.X) > 8 || Math.Abs(rect.top - monitor.Y) > 8)
                return null;

            var info = new WindowInfo(
                hwnd, "", "", "", 0,
                new WindowRect(rect.left, rect.top, width, height),
                false, false, false, false, false);

            // 必须用详细入口：旧 CaptureAsync 只剩 Bitmap，真实来源（PrintWindow/WGC）与超期事实会丢。
            var result = await WindowCapture.CaptureDetailedAsync(info);
            if (result.Bitmap is not null)
                App.Log($"Capture: 前景窗口捕获 source={result.Source} {result.Bitmap.Width}×{result.Bitmap.Height} timedOut={result.PrintWindowTimedOut}");
            return result;
        }
        catch (Exception ex)
        {
            App.Log($"Capture: 前景窗口兜底失败 {ex.Message}");
            return null;
        }
    }

    /// <summary>窗口捕获实际来源映射到结果引擎；Source 只有 PrintWindow/Wgc/None，未知按 Fallback，绝不猜 Screen。</summary>
    private static CaptureEngine MapWindowCaptureSource(WindowCaptureSource source)
        => source switch
        {
            WindowCaptureSource.PrintWindow => CaptureEngine.PrintWindow,
            WindowCaptureSource.Wgc => CaptureEngine.Wgc,
            _ => CaptureEngine.Fallback,
        };

    /// <summary>BitBlt 兜底：把虚拟屏截图裁出显示器区域（显示器本地坐标）。失败返回 null 并记日志。</summary>
    private static Bitmap? CaptureMonitorByBitBlt(WindowRect monitor, bool includeLayered = true)
    {
        try
        {
            var (full, _) = ScreenCapturer.CaptureVirtualScreen(includeLayered);
            try
            {
                return full.Clone(
                    new Rectangle(monitor.X, monitor.Y, monitor.Width, monitor.Height),
                    PixelFormat.Format32bppArgb);
            }
            finally
            {
                full.Dispose();
            }
        }
        catch (Win32Exception ex)
        {
            App.Log($"Capture: BitBlt 失败 error=0x{ex.NativeErrorCode:X8} ({ex.NativeErrorCode}) includeLayered={includeLayered}: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            App.Log($"Capture: BitBlt 异常 {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 一键全屏快照：抓光标所在显示器（WGC 常驻池优先，BitBlt 兜底），返回 PNG 字节。
    /// 不打开取景器、不中断用户当前操作。
    /// </summary>
    public enum QuickCaptureStatus
    {
        Success = 0,
        LikelyBlack = 1,
        AllEnginesFailed = 2,
    }

    public sealed record QuickCaptureResult(byte[]? Png, QuickCaptureStatus Status);

    public static async Task<QuickCaptureResult> CaptureFullScreenPngAsync()
    {
        try
        {
            var (hMonitor, monitor) = GetCursorMonitor();
            if (monitor.Width <= 0 || monitor.Height <= 0)
                return new QuickCaptureResult(null, QuickCaptureStatus.AllEnginesFailed);

            WgcWindowCapture.EnsureMonitorPool(hMonitor);
            var (bitmap, status, engine, printWindowTimedOut) = await CaptureMonitorBestAsync(hMonitor, monitor);
            if (bitmap is null)
            {
                App.Log($"QuickCapture: 无可用帧 status={status} engine={engine} timedOut={printWindowTimedOut}");
                return new QuickCaptureResult(null, status);
            }

            try
            {
                DrawCursorIfEnabled(bitmap, monitor);
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                App.Log($"QuickCapture: {bitmap.Width}×{bitmap.Height} status={status}");
                return new QuickCaptureResult(ms.ToArray(), QuickCaptureStatus.Success);
            }
            finally
            {
                bitmap.Dispose();
            }
        }
        catch (Exception ex)
        {
            App.Log($"QuickCapture 失败: {ex}");
            return new QuickCaptureResult(null, QuickCaptureStatus.AllEnginesFailed);
        }
    }

    /// <summary>按设置把当前鼠标指针画到冻结图上（GDI 截屏本身不含光标）。</summary>
    private static void DrawCursorIfEnabled(Bitmap bitmap, WindowRect monitor)
    {
        if (!App.Config.Current.CaptureCursor)
            return;

        try
        {
            var cursor = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref cursor) || (cursor.flags & CursorShowing) == 0 || cursor.hCursor == 0)
                return;

            var x = cursor.ptScreenPos.X - monitor.X;
            var y = cursor.ptScreenPos.Y - monitor.Y;

            // 图标原点不是热点：先减去热点偏移，箭头/文本光标/I 型光标才会落在真实位置
            if (GetIconInfo(cursor.hCursor, out var icon))
            {
                x -= icon.xHotspot;
                y -= icon.yHotspot;
                if (icon.hbmMask != 0)
                    _ = DeleteObject(icon.hbmMask);
                if (icon.hbmColor != 0)
                    _ = DeleteObject(icon.hbmColor);
            }

            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            try
            {
                _ = DrawIconEx(hdc, x, y, cursor.hCursor, 0, 0, 0, 0, DiNormal);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            App.Log($"Capture: cursor included at {x},{y}");
        }
        catch (Exception ex)
        {
            // 光标只是锦上添花，失败绝不能影响截图主体
            App.Log($"Capture: 光标合成失败 {ex.Message}");
        }
    }

    private const uint CursorShowing = 0x0001;
    private const uint DiNormal = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int cbSize;
        public uint flags;
        public nint hCursor;
        public NativePoint ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(nint hIcon, out IconInfo iconInfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(nint hdc, int x, int y, nint hIcon, int cx, int cy, uint istep, nint hbrFlickerFreeDraw, uint diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    /// <summary>采样判断是否几乎全黑（独占全屏游戏被系统屏蔽时的典型结果）。</summary>
    private static bool IsNearlyBlack(Bitmap bitmap)
    {
        var stepX = Math.Max(1, bitmap.Width / 16);
        var stepY = Math.Max(1, bitmap.Height / 16);
        var total = 0;
        var bright = 0;

        for (var y = stepY / 2; y < bitmap.Height; y += stepY)
        {
            for (var x = stepX / 2; x < bitmap.Width; x += stepX)
            {
                var pixel = bitmap.GetPixel(x, y);
                total++;
                if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) > 24)
                    bright++;
            }
        }

        return total > 0 && bright * 100 / total < 2;   // 亮像素不足 2% 视为黑屏
    }

    /// <summary>取消当前取景（全局 Esc 入口）。</summary>
    public void CancelCurrent() => _overlay?.Cancel();
    /// <summary>
    /// 启动预热：第一次真实截图不再为 JIT 编译和 GDI/DWM 首次调用买单
    /// （主人实测：刚打开的前几次 F1 有闪屏和焦点失灵，全热之后消失）。
    /// </summary>
    public static void WarmUpNative()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var _ = ScreenCapturer.CaptureVirtualScreen().Image;
            }
            catch
            {
            }

            try
            {
                _ = WindowEnumerator.EnumerateVisibleWindows();
            }
            catch
            {
            }

            // 预热：直接启动常驻捕获池（会话启动的一次性合成器切换发生在应用启动时，
            // 之后每次 F1 都不再启停会话——爆闪根治）
            try
            {
                if (GetCursorPos(out var origin))
                {
                    var hMonitor = MonitorFromPoint(origin, 1);
                    if (!hMonitor.IsNull)
                        WgcWindowCapture.EnsureMonitorPool(hMonitor);
                }
            }
            catch
            {
            }
        });
    }

    // GetCursorPos/MonitorFromPoint 的 POINT 结构在 CsWin32 生成中缺失，自备一份等价声明
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern BOOL GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern HMONITOR MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    /// <summary>鼠标所在显示器的物理矩形（显示器本地坐标系的原点即虚拟屏坐标）。</summary>
    private static (HMONITOR Monitor, WindowRect Rect) GetCursorMonitor()
    {
        if (!GetCursorPos(out var pt))
            return default;

        var hMonitor = MonitorFromPoint(pt, 1 /* MONITOR_DEFAULTTONEAREST */);
        if (hMonitor.IsNull)
            return default;

        var info = new MONITORINFO
        {
            cbSize = (uint)Marshal.SizeOf<MONITORINFO>(),
        };
        unsafe
        {
            if (!PInvoke.GetMonitorInfo(hMonitor, &info))
                return default;
        }

        var rect = new WindowRect(
            info.rcMonitor.left, info.rcMonitor.top,
            info.rcMonitor.right - info.rcMonitor.left,
            info.rcMonitor.bottom - info.rcMonitor.top);
        return (hMonitor, rect);
    }
}
