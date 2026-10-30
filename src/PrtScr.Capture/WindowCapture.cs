using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using PrtScr.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;

namespace PrtScr.Capture;

/// <summary>单次窗口捕获的实际内容来源；上层据此刻画历史/提示，不得一律写成「窗口」。</summary>
public enum WindowCaptureSource
{
    None = 0,
    PrintWindow = 1,
    Wgc = 2,
}

/// <summary>窗口捕获失败/降级的原因；上层据此给出可见提示或标注来源，不得静默当普通失败。</summary>
public enum WindowCaptureFailure
{
    None = 0,
    /// <summary>同一窗口已有一次 PrintWindow 在飞（单飞拒绝，本次未发起新调用）。</summary>
    Busy = 1,
    /// <summary>PrintWindow 超过受控期限，调用方已脱身；底层调用本身无法中止。</summary>
    Timeout = 2,
    /// <summary>PrintWindow 返回假或纯黑，且 WGC 回退也失败。</summary>
    PrintWindowUnavailable = 3,
    Canceled = 4,
    InvalidBounds = 5,
    Error = 6,
}

/// <summary>
/// 窗口捕获结果。<see cref="Bitmap"/> 非空即成功；<see cref="Source"/> 给出实际来源；
/// <see cref="PrintWindowTimedOut"/> 单独标出「PrintWindow 超期后降级」这一事实——
/// 即使最终由 WGC 成功，上层（A02/P-04）也需要知道窗口捕获被降级过。
/// 粗粒度 <see cref="Failure"/> 保持 P-04 已消费的语义；WGC 回退的真实原因另见
/// <see cref="WgcFailure"/> / <see cref="WgcHResult"/> / <see cref="WgcDetail"/>。
/// </summary>
public sealed class WindowCaptureResult
{
    private WindowCaptureResult(
        Bitmap? bitmap,
        WindowCaptureSource source,
        WindowCaptureFailure failure,
        bool printWindowTimedOut,
        string? detail,
        WgcWindowCaptureFailure wgcFailure,
        int wgcHResult,
        string? wgcDetail)
    {
        Bitmap = bitmap;
        Source = source;
        Failure = failure;
        PrintWindowTimedOut = printWindowTimedOut;
        Detail = detail;
        WgcFailure = wgcFailure;
        WgcHResult = wgcHResult;
        WgcDetail = wgcDetail;
    }

    public Bitmap? Bitmap { get; }
    public WindowCaptureSource Source { get; }
    public WindowCaptureFailure Failure { get; }
    public bool PrintWindowTimedOut { get; }
    public string? Detail { get; }

    /// <summary>WGC 回退的真实失败原因；未走 WGC 或 WGC 成功时为 <see cref="WgcWindowCaptureFailure.None"/>。</summary>
    public WgcWindowCaptureFailure WgcFailure { get; }

    /// <summary>WGC 失败时的原生 HRESULT；未失败时为 0。</summary>
    public int WgcHResult { get; }

    /// <summary>WGC 失败时的原始 Detail（P-08 产出）；未失败时为 null。</summary>
    public string? WgcDetail { get; }

    public bool IsSuccess => Bitmap is not null;
    public bool IsTimeout => Failure == WindowCaptureFailure.Timeout;

    /// <summary>整条链是否因调用方取消而结束（含 WGC 侧取消归一后的结果）。</summary>
    public bool IsCanceled => Failure == WindowCaptureFailure.Canceled;

    internal static WindowCaptureResult Success(
        Bitmap bitmap,
        WindowCaptureSource source,
        bool printWindowTimedOut,
        string? detail,
        WgcWindowCaptureFailure wgcFailure = WgcWindowCaptureFailure.None,
        int wgcHResult = 0,
        string? wgcDetail = null)
        => new(bitmap, source, WindowCaptureFailure.None, printWindowTimedOut, detail,
            wgcFailure, wgcHResult, wgcDetail);

    internal static WindowCaptureResult Failed(
        WindowCaptureFailure failure,
        bool printWindowTimedOut,
        string? detail,
        WgcWindowCaptureFailure wgcFailure = WgcWindowCaptureFailure.None,
        int wgcHResult = 0,
        string? wgcDetail = null)
        => new(null, WindowCaptureSource.None, failure, printWindowTimedOut, detail,
            wgcFailure, wgcHResult, wgcDetail);
}

/// <summary>
/// 窗口本体捕获：PrintWindow + PW_RENDERFULLCONTENT，直接取窗口自身合成内容——
/// 抗遮挡（被其他窗口盖住的部分也完整），即“第二张图 → 第三张图”的效果。
/// M2 将升级为 Windows.Graphics.Capture（WGC）完全体。
/// </summary>
public static class WindowCapture
{
    // PW_RENDERFULLCONTENT = 2（Win 8.1+，含 DirectComposition 内容）；SDK 元数据未收录该成员
    private const PRINT_WINDOW_FLAGS PW_RENDERFULLCONTENT = (PRINT_WINDOW_FLAGS)2;

    /// <summary>
    /// PrintWindow 的受控期限（毫秒）。取值理由：
    /// 1) 正常窗口 PrintWindow 是几毫秒到几十毫秒，2s 只用来兜住「目标窗口线程无响应」的极端情况；
    /// 2) WGC 单帧等待本身是 2s（WgcWindowCapture.CaptureCoreAsync），两条回退链保持同量级；
    /// 3) 取景窗在捕获期间已隐藏，期限再长会让用户面对空屏，因此不许放大到 5s 以上。
    /// 注意：超期只能让调用方脱身，不能终止目标窗口的 PrintWindow（见 CaptureDetailedAsync 注释）。
    /// </summary>
    private const int PrintWindowTimeoutMs = 2000;

    /// <summary>
    /// 由 App 注入的日志回调（类库不依赖 App）。当前 App 只接了 WgcWindowCapture.Log
    /// （src/PrtScr.App/App.xaml.cs:56），因此未单独接线时窗口捕获的日志汇入同一个 sink。
    /// </summary>
    public static Action<string>? Log;

    private static void Trace(string message)
        => (Log ?? WgcWindowCapture.Log)?.Invoke($"WindowCapture: {message}");

    // 单飞门闩：值为 0 表示该 HWND 有一次 PrintWindow 在飞。门闩由底层 PrintWindow 线程真正
    // 返回时（经由一次性后继）释放，而不是调用方脱身时——否则超时后每按一次 F1 都会再叠一条
    // 挂起的线程。因此超时/取消后到该线程返回之前，同一窗口的新调用立即得到 Busy，不排队。
    private static readonly ConcurrentDictionary<nint, byte> _printWindowFlights = new();

    private static bool TryEnterFlight(nint hwnd) => hwnd != 0 && _printWindowFlights.TryAdd(hwnd, 0);
    private static void ExitFlight(nint hwnd) => _printWindowFlights.TryRemove(hwnd, out _);

    /// <summary>一次在飞的 PrintWindow：调用方脱身后，由后台后继据此释放晚到帧并归还门闩。</summary>
    private sealed class PrintFlight
    {
        public PrintFlight(nint hwnd) => Hwnd = hwnd;

        public nint Hwnd { get; }

        /// <summary>调用方已不再等待（超时/取消），晚到结果由后继释放。</summary>
        public volatile bool Abandoned;
    }

    /// <summary>
    /// 在专用线程上启动 PrintWindow，并挂一次性后继：任务结束（含异常）时观察异常，
    /// 调用方已脱身则释放晚到 Bitmap，最后才归还单飞门闩（门闩的生命周期 = 原生调用在飞的时长）。
    /// </summary>
    private static Task<Bitmap?> StartPrintWindow(WindowInfo window, PrintFlight flight)
    {
        var task = Task.Factory.StartNew(
            () => CaptureWindowCore(window),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _ = task.ContinueWith(
            static (t, state) =>
            {
                var f = (PrintFlight)state!;
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    if (f.Abandoned)
                        t.Result?.Dispose();   // 晚到帧的唯一释放点
                }
                else
                {
                    _ = t.Exception;           // 观察掉，避免 UnobservedTaskException
                }

                ExitFlight(f.Hwnd);            // 原生调用真正返回后才放开同一窗口
            },
            flight,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    /// <summary>
    /// 窗口本体捕获总入口（兼容旧签名）：优先 PrintWindow（快、无系统边框），
    /// 失败（硬件加速应用全黑/拒绝）或超期回退 WGC（OBS 同源）。
    /// 需要「超时/忙/实际来源」这些可判别原因时改用 <see cref="CaptureDetailedAsync"/>。
    /// </summary>
    public static async Task<Bitmap?> CaptureAsync(WindowInfo window, CancellationToken cancellationToken = default)
    {
        var result = await CaptureDetailedAsync(window, cancellationToken).ConfigureAwait(false);
        return result.Bitmap;
    }

    /// <summary>
    /// 窗口本体捕获（带原因）：PrintWindow 放在专用线程上跑，调用方最多等
    /// <see cref="PrintWindowTimeoutMs"/>ms；超期/取消后调用方立即脱身并返回可判别原因（或 WGC 降级结果），
    /// 晚到的 Bitmap 由一次性后继释放。同一窗口同时只允许一次 PrintWindow 在飞（单飞）。
    /// 限制：超期不能终止目标窗口的 PrintWindow，底层线程会残留到系统自行返回为止；
    /// 门闩由该线程返回时释放，期间对同一窗口的调用得到 <see cref="WindowCaptureFailure.Busy"/>。
    /// WGC 回退失败时，<see cref="WindowCaptureResult.WgcFailure"/> /
    /// <see cref="WindowCaptureResult.WgcHResult"/> / <see cref="WindowCaptureResult.WgcDetail"/> 给出 WGC 侧真实原因；
    /// WGC 报取消时整条链归一为 <see cref="WindowCaptureFailure.Canceled"/>，不误报成 PrintWindowUnavailable。
    /// 调用方 token 在进入时已取消 → 立即返回 <see cref="WindowCaptureFailure.Canceled"/>，不占门闩、不发起 PrintWindow。
    /// </summary>
    public static async Task<WindowCaptureResult> CaptureDetailedAsync(
        WindowInfo window, CancellationToken cancellationToken = default)
    {
        // 入口预判：token 已取消时立即脱身——不占单飞门闩、不起 PrintWindow、不产生晚到帧。
        // 已经起跑之后再取消仍走「调用方脱身 + 晚到帧一次性后继释放」，这里不放大既有竞态。
        if (cancellationToken.IsCancellationRequested)
        {
            Trace("取消：进入捕获前 token 已取消，不发起 PrintWindow");
            return WindowCaptureResult.Failed(
                WindowCaptureFailure.Canceled, false, "捕获已取消（进入捕获前 token 已取消）");
        }

        var width = window.FrameBounds.Width;
        var height = window.FrameBounds.Height;
        if (window.Handle == 0 || width <= 0 || height <= 0)
        {
            Trace($"跳过：句柄/尺寸非法 hwnd=0x{window.Handle:X} {width}×{height}");
            return WindowCaptureResult.Failed(
                WindowCaptureFailure.InvalidBounds, false, $"句柄或尺寸非法：hwnd=0x{window.Handle:X} {width}×{height}");
        }

        if (!TryEnterFlight(window.Handle))
        {
            Trace($"忙：hwnd=0x{window.Handle:X} 已有一次 PrintWindow 在飞，本次不发起新调用");
            return WindowCaptureResult.Failed(
                WindowCaptureFailure.Busy, false, "同一窗口已有一次 PrintWindow 在飞");
        }

        Task<Bitmap?>? printTask = null;
        PrintFlight? flight = null;
        var printWindowTimedOut = false;
        Bitmap? printed = null;
        try
        {
            try
            {
                // 专用线程（LongRunning）：PrintWindow 挂住时不占用线程池线程，也不会让
                // 上层取景会话的其它 async 步骤排队。
                flight = new PrintFlight(window.Handle);
                printTask = StartPrintWindow(window, flight);
            }
            catch (Exception ex)
            {
                // 线程没起来，也就不存在在飞的原生调用：立即归还门闩并改走 WGC 回退。
                Trace($"PrintWindow 启动失败：hwnd=0x{window.Handle:X} {ex.GetType().Name} 0x{ex.HResult:X}: {ex.Message}");
                ExitFlight(window.Handle);
                printTask = null;
            }

            if (printTask is not null)
            {
                try
                {
                    var deadline = Task.Delay(PrintWindowTimeoutMs, cancellationToken);
                    var winner = await Task.WhenAny(printTask, deadline).ConfigureAwait(false);
                    if (ReferenceEquals(winner, printTask))
                    {
                        printed = await printTask.ConfigureAwait(false);
                    }
                    else if (cancellationToken.IsCancellationRequested)
                    {
                        AbandonLate(printTask, flight!);
                        Trace($"取消：hwnd=0x{window.Handle:X} PrintWindow 未返回即被调用方取消，晚到结果将由后台释放");
                        return WindowCaptureResult.Failed(WindowCaptureFailure.Canceled, false, "捕获已取消");
                    }
                    else
                    {
                        printWindowTimedOut = true;
                        AbandonLate(printTask, flight!);
                        Trace($"超时：hwnd=0x{window.Handle:X} PrintWindow 超过 {PrintWindowTimeoutMs}ms，" +
                              "调用方脱身（原生调用不可中止），回退 WGC");
                    }
                }
                catch (Exception ex)
                {
                    Trace($"PrintWindow 异常：hwnd=0x{window.Handle:X} {ex.GetType().Name} 0x{ex.HResult:X}: {ex.Message}");
                }
            }

            if (printed is not null)
                return WindowCaptureResult.Success(printed, WindowCaptureSource.PrintWindow, false, null);

            if (cancellationToken.IsCancellationRequested)
                return WindowCaptureResult.Failed(WindowCaptureFailure.Canceled, printWindowTimedOut, "捕获已取消");

            WgcWindowCaptureResult wgc;
            try
            {
                // P-07b：走详细入口拿 WGC 侧的 Failure/HRESULT/Detail，不再只剩 null。
                // P-08 契约下该入口不抛异常；保留兜底 catch 以防实现变化把异常漏到上层。
                wgc = await WgcWindowCapture.CaptureWindowDetailedAsync(window, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Trace($"取消：hwnd=0x{window.Handle:X} WGC 回退抛出取消，整条链按取消返回");
                return WindowCaptureResult.Failed(
                    WindowCaptureFailure.Canceled, printWindowTimedOut, "捕获已取消",
                    WgcWindowCaptureFailure.Canceled, 0, "WGC 调用抛出 OperationCanceledException");
            }
            catch (Exception ex)
            {
                var detail = $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}";
                Trace($"WGC 回退异常：hwnd=0x{window.Handle:X} {detail}");
                return WindowCaptureResult.Failed(
                    printWindowTimedOut ? WindowCaptureFailure.Timeout : WindowCaptureFailure.PrintWindowUnavailable,
                    printWindowTimedOut,
                    ComposeWgcFailureDetail(printWindowTimedOut, $"WGC 调用异常 {detail}"),
                    WgcWindowCaptureFailure.CaptureFailed, ex.HResult, detail);
            }

            if (wgc.Bitmap is not null)
            {
                if (printWindowTimedOut)
                    Trace($"降级成功：hwnd=0x{window.Handle:X} PrintWindow 超时后 WGC 取到窗口内容");
                else if (printTask is null)
                    Trace($"降级成功：hwnd=0x{window.Handle:X} PrintWindow 线程未起，改由 WGC 取窗口内容");
                return WindowCaptureResult.Success(
                    wgc.Bitmap,
                    WindowCaptureSource.Wgc,
                    printWindowTimedOut,
                    printWindowTimedOut ? $"PrintWindow 超过 {PrintWindowTimeoutMs}ms，已改用 WGC" : null,
                    WgcWindowCaptureFailure.None, wgc.HResult, wgc.Detail);
            }

            // WGC 的取消必须归一为 Canceled：上层（P-04）据 Failure 决定「会话作废」还是「失败提示」，
            // 误报成 PrintWindowUnavailable/Timeout 会让取消也弹失败提示。
            if (wgc.Failure == WgcWindowCaptureFailure.Canceled)
            {
                Trace($"取消：hwnd=0x{window.Handle:X} WGC 回退报告已取消（PrintWindow 超时={printWindowTimedOut}），" +
                      "整条链按取消返回");
                return WindowCaptureResult.Failed(
                    WindowCaptureFailure.Canceled, printWindowTimedOut,
                    ComposeWgcFailureDetail(printWindowTimedOut, DescribeWgcFailure(wgc)),
                    wgc.Failure, wgc.HResult, wgc.Detail);
            }

            Trace($"WGC 回退失败：hwnd=0x{window.Handle:X} {DescribeWgcFailure(wgc)}");
            return WindowCaptureResult.Failed(
                printWindowTimedOut ? WindowCaptureFailure.Timeout : WindowCaptureFailure.PrintWindowUnavailable,
                printWindowTimedOut,
                ComposeWgcFailureDetail(printWindowTimedOut, DescribeWgcFailure(wgc)),
                wgc.Failure, wgc.HResult, wgc.Detail);
        }
        finally
        {
            // 门闩不在这里归还：调用方脱身时底层的 PrintWindow 线程仍可能在跑，
            // 由 StartPrintWindow 挂的一次性后继在线程真正返回时 ExitFlight。
            // 只有「线程没起来 / 根本没建 flight」这两种情况才由这里兜底归还。
            if (printTask is null)
                ExitFlight(window.Handle);
        }
    }

    /// <summary>
    /// 调用方已脱身：标记这次在飞为「放弃」，晚到 Bitmap 由 <see cref="StartPrintWindow"/> 的一次性后继释放。
    /// 若任务恰好已经结束、后继可能已先跑过，这里补一次释放（Bitmap.Dispose 幂等，重复调用无害）。
    /// </summary>
    private static void AbandonLate(Task<Bitmap?> task, PrintFlight flight)
    {
        flight.Abandoned = true;
        if (!task.IsCompleted)
            return;

        if (task.Status == TaskStatus.RanToCompletion)
            task.Result?.Dispose();
        else
            _ = task.Exception;
    }

    /// <summary>把 P-08 的 WGC 失败结果压成一行人读描述（枚举名 + HRESULT + 原始 Detail）。</summary>
    private static string DescribeWgcFailure(WgcWindowCaptureResult wgc)
        => wgc.Detail is null
            ? $"WGC {wgc.Failure}（HRESULT=0x{wgc.HResult:X8}）"
            : $"WGC {wgc.Failure}（HRESULT=0x{wgc.HResult:X8}）：{wgc.Detail}";

    /// <summary>整条链失败原因：PrintWindow 侧结论 + WGC 侧真实原因。</summary>
    private static string ComposeWgcFailureDetail(bool printWindowTimedOut, string wgcPart)
        => (printWindowTimedOut
                ? $"PrintWindow 超过 {PrintWindowTimeoutMs}ms 且 WGC 回退失败"
                : "PrintWindow 失败（返回假或纯黑）且 WGC 回退失败")
           + "：" + wgcPart;

    /// <summary>
    /// 同步捕获入口（保持旧签名兼容，供启动预热用）：无受控期限，且与
    /// <see cref="CaptureDetailedAsync"/> 共用单飞门闩；已在飞时返回 null（预热丢结果无影响）。
    /// 取景主链路请用 <see cref="CaptureAsync"/> / <see cref="CaptureDetailedAsync"/>。
    /// </summary>
    public static Bitmap? CaptureWindow(WindowInfo window)
    {
        if (!TryEnterFlight(window.Handle))
        {
            Trace($"忙（同步入口）：hwnd=0x{window.Handle:X} 已有一次 PrintWindow 在飞，本次跳过");
            return null;
        }

        try
        {
            return CaptureWindowCore(window);
        }
        finally
        {
            ExitFlight(window.Handle);
        }
    }

    private static Bitmap? CaptureWindowCore(WindowInfo window)
    {
        var width = window.FrameBounds.Width;
        var height = window.FrameBounds.Height;
        if (width <= 0 || height <= 0)
            return null;

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var keep = false;
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = new HDC(graphics.GetHdc());
            bool ok;
            try
            {
                ok = PInvoke.PrintWindow((HWND)window.Handle, hdc, PW_RENDERFULLCONTENT);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            if (!ok)
                return null;

            // 部分硬件加速应用 PrintWindow 会返回 true 但内容全黑，采样判定后回退屏幕裁剪
            if (LooksSolidBlack(bitmap))
                return null;

            keep = true;
            return bitmap;
        }
        finally
        {
            if (!keep)
                bitmap.Dispose();
        }
    }

    /// <summary>9×9 网格采样，全部为纯黑则视为捕获失败。</summary>
    private static bool LooksSolidBlack(Bitmap bitmap)
    {
        var stepX = bitmap.Width / 10;
        var stepY = bitmap.Height / 10;
        if (stepX == 0 || stepY == 0)
            return false;

        for (var iy = 1; iy <= 9; iy++)
        {
            for (var ix = 1; ix <= 9; ix++)
            {
                var pixel = bitmap.GetPixel(stepX * ix, stepY * iy);
                if (pixel.R != 0 || pixel.G != 0 || pixel.B != 0)
                    return false;
            }
        }
        return true;
    }
}
