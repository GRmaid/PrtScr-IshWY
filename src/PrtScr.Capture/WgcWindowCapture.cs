using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using PrtScr.Interop;
using WinRT;

namespace PrtScr.Capture;

/// <summary>WGC 窗口捕获失败原因；上层据此区分「窗口失效 / 条目创建失败 / 设备不可用 / 无帧 / 拷贝失败 / 取消」。</summary>
public enum WgcWindowCaptureFailure
{
    None = 0,
    /// <summary>HWND 为 0，调用方传入了无效窗口。</summary>
    InvalidWindow = 1,
    /// <summary>IGraphicsCaptureItemInterop::CreateForWindow 失败（HResult/Detail 给出原因）。</summary>
    ItemCreationFailed = 2,
    /// <summary>D3D11 设备或 WinRT 设备不可用。</summary>
    DeviceUnavailable = 3,
    /// <summary>2 秒内没有收到可用的首帧。</summary>
    NoFrame = 4,
    /// <summary>首帧拷贝为 Bitmap 失败，或捕获过程抛出异常。</summary>
    CaptureFailed = 5,
    /// <summary>调用方取消。</summary>
    Canceled = 6,
}

/// <summary>
/// WGC 窗口单帧捕获结果。失败时 <see cref="Bitmap"/> 为 null 且 <see cref="Failure"/> 可判别；
/// <see cref="HResult"/> 在 COM 异常/条目创建失败时给出原生错误码，否则为 0。
/// </summary>
public sealed class WgcWindowCaptureResult
{
    private WgcWindowCaptureResult(
        Bitmap? bitmap,
        WgcWindowCaptureFailure failure,
        int hresult,
        string? detail)
    {
        Bitmap = bitmap;
        Failure = failure;
        HResult = hresult;
        Detail = detail;
    }

    public Bitmap? Bitmap { get; }
    public WgcWindowCaptureFailure Failure { get; }
    public int HResult { get; }
    public string? Detail { get; }
    public bool IsSuccess => Bitmap is not null;
    public bool IsCanceled => Failure == WgcWindowCaptureFailure.Canceled;

    internal static WgcWindowCaptureResult Success(Bitmap bitmap)
        => new(bitmap, WgcWindowCaptureFailure.None, 0, null);

    internal static WgcWindowCaptureResult Failed(
        WgcWindowCaptureFailure failure, int hresult, string? detail)
        => new(null, failure, hresult, detail);
}

/// <summary>
/// WGC（Windows.Graphics.Capture）单帧捕获——OBS 同源引擎。
/// 支持窗口条目（PrintWindow 的兜底）与显示器条目（冻屏取景的快路径，
/// GPU 侧合成比 CAPTUREBLT BitBlt 快一个量级，且天然包含分层窗口）。
/// 窗口条目经 IGraphicsCaptureItemInterop.CreateForWindow 获取：该接口随
/// Windows.Graphics.Capture 在 1803+ 提供，不依赖 20348 才有的
/// GraphicsCaptureItem.TryCreateFromWindowId。失败一律写日志并返回可判别的
/// <see cref="WgcWindowCaptureFailure"/>，不再静默吞掉异常。
/// </summary>
public static class WgcWindowCapture
{
    /// <summary>由 App 注入的日志回调（类库不依赖 App）。</summary>
    public static Action<string>? Log;

    private static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        // 原生签名是 (HWND/HMONITOR, REFIID, void**)：句柄在前、IID 在后。
        // 2026-10-19 实测：顺序写反时 CreateForMonitor 稳定返回 E_INVALIDARG 0x80070057。
        void CreateForWindow(nint window, in Guid iid, out IntPtr item);

        void CreateForMonitor(nint hmonitor, in Guid iid, out IntPtr item);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(in Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint adapter, uint driverType, uint software, uint flags,
        nint featureLevels, uint numFeatureLevels, uint sdkVersion,
        out nint device, nint featureLevel, out nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true, EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    // 把 DWM 尚未提交的合成走完：切换/提升窗口后的第一时间取帧，池里可能还是切换前的构图
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private const uint DriverTypeHardware = 1;      // D3D_DRIVER_TYPE_HARDWARE
    private const uint DeviceFlagBgraSupport = 0x20; // D3D11_CREATE_DEVICE_BGRA_SUPPORT
    private const uint D3D11SdkVersion = 7;

    // D3D 设备创建要几十毫秒，跨截图缓存复用（每次会话只借它的上下文，不释放）
    private static readonly object _deviceLock = new();
    private static IDirect3DDevice? _cachedWinrtDevice;
    private static nint _cachedDevice;
    private static nint _cachedContext;

    private static (IDirect3DDevice Winrt, nint Device, nint Context) GetOrCreateDevice()
    {
        lock (_deviceLock)
        {
            if (_cachedWinrtDevice is not null)
                return (_cachedWinrtDevice, _cachedDevice, _cachedContext);

            var hr = D3D11CreateDevice(
                0, DriverTypeHardware, 0, DeviceFlagBgraSupport,
                0, 0, D3D11SdkVersion, out var device, 0, out var context);
            if (hr != 0 || device == 0)
                return (null!, 0, 0);

            if (Marshal.QueryInterface(device, IID_IDXGIDevice, out var dxgiDevice) != 0)
            {
                _ = Marshal.Release(device);
                _ = Marshal.Release(context);
                return (null!, 0, 0);
            }

            try
            {
                if (CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var graphicsDeviceAbi) != 0)
                {
                    _ = Marshal.Release(dxgiDevice);
                    _ = Marshal.Release(device);
                    _ = Marshal.Release(context);
                    return (null!, 0, 0);
                }

                IDirect3DDevice winrtDevice;
                try
                {
                    winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDeviceAbi);
                }
                finally
                {
                    // ABI 引用的所有权在本方法：无论 FromAbi 成功还是抛异常都只还一次。
                    Marshal.Release(graphicsDeviceAbi);
                }

                _cachedWinrtDevice = winrtDevice;
                _cachedDevice = device;
                _cachedContext = context;
                _ = Marshal.Release(dxgiDevice);
                return (_cachedWinrtDevice, _cachedDevice, _cachedContext);
            }
            catch
            {
                _ = Marshal.Release(dxgiDevice);
                _ = Marshal.Release(device);
                _ = Marshal.Release(context);
                return (null!, 0, 0);
            }
        }
    }

    /// <summary>
    /// 窗口单帧捕获的兼容入口：失败返回 null（原因已写日志；需要可判别原因请用
    /// <see cref="CaptureWindowDetailedAsync"/>）。保留签名供既有调用方与 P-07 回退链使用。
    /// </summary>
    public static async Task<Bitmap?> CaptureWindowAsync(WindowInfo window, CancellationToken cancellationToken = default)
    {
        var result = await CaptureWindowDetailedAsync(window, cancellationToken).ConfigureAwait(false);
        return result.Bitmap;
    }

    /// <summary>
    /// 窗口单帧捕获的详细入口：不抛异常，失败时返回 <see cref="WgcWindowCaptureFailure"/>
    /// 与原始 HRESULT，供上层区分「窗口失效 / 条目创建失败 / 设备不可用 / 无帧 / 拷贝失败 / 取消」。
    /// </summary>
    public static async Task<WgcWindowCaptureResult> CaptureWindowDetailedAsync(
        WindowInfo window, CancellationToken cancellationToken = default)
    {
        if (window.Handle == 0)
        {
            Log?.Invoke("WGC: 窗口捕获失败：HWND=0");
            return WgcWindowCaptureResult.Failed(
                WgcWindowCaptureFailure.InvalidWindow, unchecked((int)0x80070057), "HWND=0");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Log?.Invoke($"WGC: 窗口捕获已取消 HWND=0x{window.Handle:X}");
            return WgcWindowCaptureResult.Failed(
                WgcWindowCaptureFailure.Canceled, 0, "调用方已取消");
        }

        try
        {
            return await Task.Run(
                () => CaptureWindowCoreAsync(window, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log?.Invoke($"WGC: 窗口捕获已取消 HWND=0x{window.Handle:X}");
            return WgcWindowCaptureResult.Failed(
                WgcWindowCaptureFailure.Canceled, 0, "调用方已取消");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"WGC: 窗口捕获异常 HWND=0x{window.Handle:X} {Describe(ex)}");
            return WgcWindowCaptureResult.Failed(
                WgcWindowCaptureFailure.CaptureFailed, ex.HResult, Describe(ex));
        }
    }

    private static async Task<WgcWindowCaptureResult> CaptureWindowCoreAsync(
        WindowInfo window, CancellationToken cancellationToken)
    {
        var item = CreateItemForWindow(window.Handle, out var itemHResult, out var itemDetail);
        if (item is null)
        {
            return WgcWindowCaptureResult.Failed(
                WgcWindowCaptureFailure.ItemCreationFailed, itemHResult, itemDetail);
        }

        try
        {
            var (winrtDevice, device, context) = GetOrCreateDevice();
            if (winrtDevice is null)
            {
                Log?.Invoke($"WGC: 窗口捕获设备不可用 HWND=0x{window.Handle:X}");
                return WgcWindowCaptureResult.Failed(
                    WgcWindowCaptureFailure.DeviceUnavailable, 0, "D3D11 设备/上下文创建失败");
            }

            var bitmap = await CaptureCoreAsync(item, winrtDevice, device, context, cancellationToken)
                .ConfigureAwait(false);
            if (bitmap is null)
            {
                Log?.Invoke($"WGC: 首帧拷贝失败 HWND=0x{window.Handle:X}");
                return WgcWindowCaptureResult.Failed(
                    WgcWindowCaptureFailure.CaptureFailed, 0, "帧拷贝返回空（staging/Map/LockBits 失败）");
            }

            return WgcWindowCaptureResult.Success(bitmap);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Log?.Invoke($"WGC: 2s 内未收到首帧 HWND=0x{window.Handle:X}");
            return WgcWindowCaptureResult.Failed(
                WgcWindowCaptureFailure.NoFrame, 0, "2 秒内未收到首帧");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"WGC: 窗口捕获异常 HWND=0x{window.Handle:X} {Describe(ex)}");
            return WgcWindowCaptureResult.Failed(
                WgcWindowCaptureFailure.CaptureFailed, ex.HResult, Describe(ex));
        }
        finally
        {
            ReleaseCaptureItem(item);
        }
    }

    private static string Describe(Exception ex)
        => $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}";

    /// <summary>
    /// 确定性释放 WinRT 投影对象（GraphicsCaptureItem 未实现 IDisposable）。
    /// CsWinRT 的原生引用挂在 IWinRTObject.NativeObject 上，Dispose 即释放该引用；
    /// 调用后该对象不应再被使用。释放失败只记日志，不改变调用方结果。
    /// </summary>
    private static void ReleaseCaptureItem(GraphicsCaptureItem? item)
    {
        if (item is null)
            return;

        try
        {
            ((IWinRTObject)item).NativeObject.Dispose();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"WGC: 释放 GraphicsCaptureItem 失败 {Describe(ex)}");
        }
    }

    private sealed class MonitorPool
    {
        public required Direct3D11CaptureFramePool Pool { get; init; }
        public required GraphicsCaptureSession Session { get; init; }
        public required GraphicsCaptureItem Item { get; init; }
        public required nint Device { get; init; }
        public required nint Context { get; init; }
        // QPC 毫秒（与 Direct3D11CaptureFrame.SystemRelativeTime 同一时基）：
        // 取景器刚关闭时，池内还会残留含取景器的帧，需要一段时间窗内不采用。
        public long OverlayClosedTick;

        // 常驻池必须持续消费 FrameArrived：2 个缓冲一满，WGC 会丢弃新构图、
        // 只留旧帧 —— 现象就是“窗口已经被盖住/抬起，F1 冻结的却是旧画面”。
        // 这里始终只保留最新一帧，取景时直接取走。
        public readonly object FrameLock = new();
        public Direct3D11CaptureFrame? Latest;
    }

    /// <summary>等待“请求之后才合成”的新帧的上限；超时则退回最新一帧（桌面静止时不会再有新帧）。</summary>
    private const int FreshFrameWaitMs = 90;

    private static long NowQpcMs() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    private static long FrameTickMs(Direct3D11CaptureFrame frame) => (long)frame.SystemRelativeTime.TotalMilliseconds;

    private static readonly object _poolLock = new();
    private static readonly Dictionary<nint, MonitorPool> _monitorPools = new();

    /// <summary>
    /// 确保某显示器的常驻捕获会话已启动（幂等）。会话常驻后 F1 时不再启停会话——
    /// 这正是“按下热键全屏爆闪”的根治（合成器通路只在启动时切换一次）。
    /// </summary>
    public static bool EnsureMonitorPool(nint hMonitor)
    {
        lock (_poolLock)
        {
            if (_monitorPools.ContainsKey(hMonitor))
                return true;
        }

        GraphicsCaptureItem? item = null;
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        var registered = false;
        try
        {
            item = CreateItemForMonitor(hMonitor);
            if (item is null)
            {
                Log?.Invoke("WGC: CreateItemForMonitor = null");
                return false;
            }

            var (winrtDevice, device, context) = GetOrCreateDevice();
            if (winrtDevice is null)
            {
                Log?.Invoke("WGC: D3D device = null");
                return false;
            }

            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            try
            {
                session.IsBorderRequired = false;
            }
            catch
            {
            }

            var state = new MonitorPool
            {
                Pool = pool,
                Session = session,
                Item = item,
                Device = device,
                Context = context,
            };
            var weak = new WeakReference<MonitorPool>(state);
            pool.FrameArrived += (sender, _) =>
            {
                if (weak.TryGetTarget(out var target))
                    RetainLatest(target, sender);
            };

            session.StartCapture();

            lock (_poolLock)
            {
                _monitorPools[hMonitor] = state;
            }
            registered = true;
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"WGC: EnsureMonitorPool 失败 {ex.GetType().Name} 0x{ex.HResult:X}: {ex.Message}");
            return false;
        }
        finally
        {
            if (!registered)
            {
                // 启动失败时池/会话/条目尚未进入常驻表，必须就地按逆序释放
                session?.Dispose();
                pool?.Dispose();
                ReleaseCaptureItem(item);
            }
        }
    }

    public static void NotifyOverlayClosed(nint hMonitor)
    {
        lock (_poolLock)
        {
            if (_monitorPools.TryGetValue(hMonitor, out var state))
                state.OverlayClosedTick = NowQpcMs();
        }
    }

    /// <summary>
    /// 取显示器最新一帧作为冻结画面（池常驻，零会话启停 = 零爆闪）。
    /// 取景器刚关闭的短时间内池里可能残留含取景器的帧，会自动等一帧新画面。
    /// </summary>
    public static async Task<Bitmap?> GetLatestMonitorFrameAsync(nint hMonitor, CancellationToken cancellationToken = default)
    {
        // 冻屏必须取“请求之后才合成”的画面：窗口刚被抬起/遮挡时，池里最新一帧
        // 仍可能是切换前的构图（现象：被盖住的窗口又出现在截图里）。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            MonitorPool? state;
            lock (_poolLock)
                _monitorPools.TryGetValue(hMonitor, out state);

            if (state is null)
            {
                EnsureMonitorPool(hMonitor);
                lock (_poolLock)
                    _monitorPools.TryGetValue(hMonitor, out state);
                if (state is null)
                    return null;
            }

            var started = Stopwatch.GetTimestamp();

            // 常驻池的 FrameArrived 一直在把最新帧收进 state.Latest：2 缓冲写满后
            // WGC 会丢弃新构图（旧帧留在池里），那正是“被盖住的窗口又出现”的来源。
            // DwmFlush 先把请求前已提交的合成推完，随后只认更晚合成的帧。
            _ = DwmFlush();
            var requestTick = NowQpcMs();
            var latest = TakeLatest(state);
            var keptAge = latest is null ? -1 : (long)(requestTick - FrameTickMs(latest));

            var frame = await WaitFrameAfterAsync(state, latest, requestTick, cancellationToken);
            if (frame is not null)
            {
                using (frame)
                {
                    var waited = (long)((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency);
                    var age = NowQpcMs() - FrameTickMs(frame);
                    Log?.Invoke($"WGC: 帧 age={age}ms waited={waited}ms kept={keptAge}ms");
                    return CopyFrameToBitmap(state.Device, state.Context, frame);
                }
            }

            Log?.Invoke("WGC: 等不到可用帧，重建常驻池");
            RebuildMonitorPool(hMonitor);
        }

        return null;
    }

    /// <summary>FrameArrived 里持续消费：把池里积压的帧全取出来，只留最新一张。</summary>
    private static void RetainLatest(MonitorPool state, Direct3D11CaptureFramePool pool)
    {
        Direct3D11CaptureFrame? newest = null;
        while (pool.TryGetNextFrame() is { } next)
        {
            newest?.Dispose();
            newest = next;
        }

        if (newest is null)
            return;

        lock (state.FrameLock)
        {
            state.Latest?.Dispose();
            state.Latest = newest;
        }
    }

    /// <summary>取走当前保留的最新帧（取走后帧归调用方释放）。</summary>
    private static Direct3D11CaptureFrame? TakeLatest(MonitorPool state)
    {
        lock (state.FrameLock)
        {
            var frame = state.Latest;
            state.Latest = null;
            return frame;
        }
    }

    private static async Task<Direct3D11CaptureFrame?> WaitFrameAfterAsync(
        MonitorPool state,
        Direct3D11CaptureFrame? latest,
        long requestTick,
        CancellationToken cancellationToken)
    {
        if (latest is not null && IsFrameAfter(latest, requestTick))
            return latest;

        var fallback = latest;   // 桌面静止时不会再有新帧，保留它做兜底
        var deadline = NowQpcMs() + FreshFrameWaitMs;
        try
        {
            while (NowQpcMs() < deadline)
            {
                await Task.Delay(5, cancellationToken);

                var next = TakeLatest(state);
                if (next is null)
                    continue;

                if (IsFrameAfter(next, requestTick))
                {
                    fallback?.Dispose();
                    fallback = null;
                    return next;
                }

                fallback?.Dispose();
                fallback = next;
            }

            // 超时仍无“请求之后”的帧：只要最后一帧不是取景器残影，用它（画面本来就是静止的）
            if (fallback is not null && !IsOverlayResidue(state, fallback))
            {
                var result = fallback;
                fallback = null;
                return result;
            }

            return null;
        }
        finally
        {
            // 取消/异常/残影丢弃时，兜底帧仍归本函数所有，必须释放
            fallback?.Dispose();
        }
    }

    private static bool IsFrameAfter(Direct3D11CaptureFrame frame, long requestTick)
        => FrameTickMs(frame) >= requestTick - 4;   // 容忍 QPC/SystemRelativeTime 的取整误差

    private static bool IsOverlayResidue(MonitorPool state, Direct3D11CaptureFrame frame)
        => state.OverlayClosedTick != 0 && FrameTickMs(frame) < state.OverlayClosedTick + 60;

    private static void RebuildMonitorPool(nint hMonitor)
    {
        MonitorPool? state = null;
        lock (_poolLock)
        {
            if (_monitorPools.Remove(hMonitor, out var existing))
                state = existing;
        }

        if (state is null)
            return;

        lock (state.FrameLock)
        {
            state.Latest?.Dispose();
            state.Latest = null;
        }

        // 逐个释放：会话已失效不该阻止池与条目的释放
        try
        {
            state.Session.Dispose();
        }
        catch
        {
            // 会话可能已失效，忽略
        }

        try
        {
            state.Pool.Dispose();
        }
        catch
        {
            // 池可能已失效，忽略
        }

        ReleaseCaptureItem(state.Item);
    }

    private static GraphicsCaptureItem? CreateItemForMonitor(nint hMonitor)
    {
        try
        {
            var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory.ThisPtr);
            try
            {
                interop.CreateForMonitor(hMonitor, IID_IGraphicsCaptureItem, out var itemAbi);
                if (itemAbi == 0)
                {
                    Log?.Invoke("WGC: CreateForMonitor 返回空 item");
                    return null;
                }
                try
                {
                    return GraphicsCaptureItem.FromAbi(itemAbi);
                }
                finally
                {
                    Marshal.Release(itemAbi);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(interop);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"WGC: CreateItemForMonitor 异常 {ex.GetType().Name} 0x{ex.HResult:X}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 通过 IGraphicsCaptureItemInterop::CreateForWindow 获取窗口条目。
    /// 与 CreateItemForMonitor 同一模板：ActivationFactory → RCW → interop → FromAbi → Release。
    /// 失败时把 HRESULT 与异常类型写入 <paramref name="detail"/>，由调用方随结果返回。
    /// </summary>
    private static GraphicsCaptureItem? CreateItemForWindow(nint hwnd, out int hresult, out string detail)
    {
        hresult = 0;
        detail = string.Empty;
        try
        {
            var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory.ThisPtr);
            try
            {
                interop.CreateForWindow(hwnd, IID_IGraphicsCaptureItem, out var itemAbi);
                if (itemAbi == 0)
                {
                    hresult = unchecked((int)0x80004003);   // E_POINTER
                    detail = "CreateForWindow 返回空 item（E_POINTER）";
                    Log?.Invoke($"WGC: CreateForWindow(HWND=0x{hwnd:X}) {detail}");
                    return null;
                }

                try
                {
                    return GraphicsCaptureItem.FromAbi(itemAbi);
                }
                finally
                {
                    Marshal.Release(itemAbi);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(interop);
            }
        }
        catch (Exception ex)
        {
            hresult = ex.HResult;
            detail = $"{ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}";
            Log?.Invoke($"WGC: CreateForWindow(HWND=0x{hwnd:X}) 失败 {detail}");
            return null;
        }
    }

    private static async Task<Bitmap?> CaptureCoreAsync(
        GraphicsCaptureItem item,
        IDirect3DDevice winrtDevice,
        nint device,
        nint context,
        CancellationToken cancellationToken)
    {
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        var firstFrame = new TaskCompletionSource<Direct3D11CaptureFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // 首帧只交给等待方一次；之后多到的帧（含首帧被放弃后晚到的帧）必须当场 Dispose，
        // 否则会占住 WGC 帧池的缓冲。
        void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            var frame = sender.TryGetNextFrame();
            if (frame is not null && !firstFrame.TrySetResult(frame))
                frame.Dispose();
        }

        try
        {
            using var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            try
            {
                session.IsBorderRequired = false;   // 旧系统可能不支持该 API，失败则保留系统黄框
            }
            catch
            {
            }

            pool.FrameArrived += OnFrameArrived;
            try
            {
                session.StartCapture();
                try
                {
                    using var frame = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                    return CopyFrameToBitmap(device, context, frame);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    // 等待方已放弃：首帧可能仍在路上。挂一次性后继，帧一到就 Dispose。
                    _ = firstFrame.Task.ContinueWith(
                        static completed =>
                        {
                            if (completed.Status == TaskStatus.RanToCompletion)
                                completed.Result?.Dispose();
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    throw;
                }
            }
            finally
            {
                pool.FrameArrived -= OnFrameArrived;
            }
        }
        finally
        {
            // CreateCaptureSession / StartCapture / 属性赋值任一处抛异常，pool 也必须释放
            pool.Dispose();
        }
    }

    private static unsafe Bitmap? CopyFrameToBitmap(nint device, nint context, Direct3D11CaptureFrame frame)
    {
        // IDirect3DSurface → ID3D11Texture2D（COM QI）
        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        try
        {
            var texturePtr = access.GetInterface(IID_ID3D11Texture2D);
            if (texturePtr == 0)
                return null;

            try
            {
                var desc = new D3D11_TEXTURE2D_DESC();
                ((ID3D11Texture2D*)texturePtr)->GetDesc(&desc);

                // 建 CPU 可读的 staging 纹理并拷贝
                var stagingDesc = new D3D11_TEXTURE2D_DESC
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = desc.Format,
                    SampleDesc = desc.SampleDesc,
                    Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                    CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                    BindFlags = 0,
                    MiscFlags = 0,
                };

                ID3D11Texture2D* staging = null;
                ((ID3D11Device*)device)->CreateTexture2D(&stagingDesc, null, &staging);
                if (staging is null)
                    return null;

                try
                {
                    var deviceContext = (ID3D11DeviceContext*)context;
                    deviceContext->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)texturePtr);

                    var mapped = new D3D11_MAPPED_SUBRESOURCE();
                    deviceContext->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
                    if (mapped.pData is null)
                        return null;

                    try
                    {
                        var width = (int)desc.Width;
                        var height = (int)desc.Height;
                        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                        try
                        {
                            var data = bitmap.LockBits(
                                new Rectangle(0, 0, width, height),
                                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                            try
                            {
                                var source = (byte*)mapped.pData;
                                var target = (byte*)data.Scan0;
                                for (var y = 0; y < height; y++)
                                {
                                    Buffer.MemoryCopy(
                                        source + (nint)y * mapped.RowPitch,
                                        target + (nint)y * data.Stride,
                                        data.Stride, (nint)width * 4);
                                }
                            }
                            finally
                            {
                                bitmap.UnlockBits(data);
                            }
                        }
                        catch
                        {
                            bitmap.Dispose();
                            throw;
                        }

                        return bitmap;
                    }
                    finally
                    {
                        deviceContext->Unmap((ID3D11Resource*)staging, 0);
                    }
                }
                finally
                {
                    _ = Marshal.Release((nint)staging);
                }
            }
            finally
            {
                _ = Marshal.Release(texturePtr);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(access);
        }
    }
}
