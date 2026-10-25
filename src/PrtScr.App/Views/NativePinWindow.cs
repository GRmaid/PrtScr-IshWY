using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using PrtScr.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Views;

/// <summary>
/// 原生分层窗口（per-pixel alpha）实现的贴图：
/// 真透明、置顶、拖动、滚轮缩放（GDI StretchBlt 快速路径 + 停手高清精修）、右键菜单、Esc/双击关闭。
/// WinUI 3 的窗口由 DirectComposition 承载，无法用 XAML 实现真正的半透明，故走原生窗口。
/// </summary>
public sealed unsafe partial class NativePinWindow
{
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_NULL = 0x0000;
    private const uint WM_APP_ZOOM_SETTLED = 0x8000 + 10;
    private const uint WM_APP_REASSERT_TOPMOST = 0x8000 + 11;
    private const uint WM_APP_ENTER_EDIT = 0x8000 + 12;
    public const uint WmAppPinCommand = 0x8000 + 30;
    private const uint WM_APP_CLEAR_NAV_HINT = 0x8000 + 31;
    private const uint WM_APP_END_FLASH = 0x8000 + 32;
    private const uint WM_HOTKEY = 0x0312;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;


    // 贴图自己轮询 Esc：不依赖低级键盘钩子（钩子会被系统静默摘除），也不抢焦点。
    private Thread? _escPollThread;
    private volatile bool _escPollStop;
    private bool _escWasDown;
    private volatile bool _escNeedsRelease;
    private volatile bool _menuOpen;

    private const int VK_ESCAPE = 0x1B;
    private const int CmdCopy = 1;
    private const int CmdClose = 2;
    private const int CmdEdit = 3;
    private const int CmdOcr = 4;
    private const int CmdSaveAs = 5;
    private const int CmdCrop = 6;
    private const int CmdHistory = 7;
    private const int CmdToggleLock = 8;
    private const int CmdToggleThrough = 9;
    private const int CmdOpacity100 = 10;
    private const int CmdOpacity75 = 11;
    private const int CmdOpacity50 = 12;
    private const int CmdOpacity25 = 13;

    private const uint WM_CLASS_STYLE = 0x0008;   // CS_DBLCLKS

    /// <summary>解码后的源图：Image 直接引用 PNG 流，流必须与图同生命周期。</summary>
    private sealed class DecodedSource : IDisposable
    {
        private readonly MemoryStream _stream;

        public DecodedSource(Image image, MemoryStream stream)
        {
            Image = image;
            _stream = stream;
        }

        public Image Image { get; }

        public void Dispose()
        {
            Image.Dispose();
            _stream.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
        public uint bmiColors;
    }

    private static class Gdi
    {
        public const int HALFTONE = 4;
        public const uint SRCCOPY = 0x00CC0020;

        [DllImport("user32.dll")] public static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(nint hwnd, nint hdc);
        [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint hdc);
        [DllImport("gdi32.dll")] public static extern nint SelectObject(nint hdc, nint obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool StretchBlt(nint hdcDest, int x, int y, int w, int h,
            nint hdcSrc, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(nint hdc, int mode);
        [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(nint hdc, int x, int y, nint prev);
        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern nint CreateDIBSection(nint hdc, ref BitmapInfo info, uint usage,
            out nint bits, nint section, uint offset);

        public static unsafe void Copy(nint dest, nint src, int bytes)
        {
            System.Buffer.MemoryCopy((void*)src, (void*)dest, bytes, bytes);
        }
    }

    [DllImport("user32.dll")]
    private static extern BOOL GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    /// <summary>
    /// 贴图存续期间持续轮询 Esc 边沿：光标压在贴图上时关闭。
    /// 不再依赖全局低级键盘钩子——低级钩子回调超时会被系统静默摘除，
    /// 表现正是“全屏热键还能用（有 RegisterHotKey 兜底），Esc 关贴图却失灵”。
    /// 轮询不消费按键，不影响其他程序。
    /// </summary>
    private void StartEscPoll()
    {
        // 专用线程而不是线程池 Timer：GetAsyncKeyState 的“本次按下”状态是每线程的，
        // 每 30ms 换一个线程池线程会让边沿判断不可靠
        _escPollThread = new Thread(() =>
        {
            Thread.Sleep(60);   // 等窗口真正显示出来再开始
            while (!_escPollStop)
            {
                PollEscape();
                Thread.Sleep(30);
            }
        })
        {
            IsBackground = true,
            Name = "PrtScr.PinEsc",
            Priority = ThreadPriority.BelowNormal,
        };
        _escPollThread.Start();
    }

    private void PollEscape()
    {
        var hwnd = _hwnd;
        if (hwnd.IsNull)
            return;

        // 裁剪层自己用低级钩子接管 Esc；刚关闭 600ms 内也别让同一次按键继续关贴图。
        if (PinCropWindow.IsActive ||
            Environment.TickCount64 - PinCropWindow.ClosedAtTick < 600)
            return;

        try
        {
            var down = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
            var isNewPress = down && !_escWasDown;
            _escWasDown = down;
            if (!down)
                _escNeedsRelease = false;

            if (_escNeedsRelease || !isNewPress || _menuOpen)
                return;

            // 标注模式下即使光标在工具栏上也应能退出
            if (!_editMode)
            {
                if (!GetCursorPos(out var pt) || WindowFromPoint(pt) != (nint)hwnd.Value)
                    return;
            }

            // 由仲裁器决定关贴图、关 OCR 还是只退出标注；同一次按键只派发一次
            EscRouter.Dispatch();
        }
        catch
        {
            // 轮询线程不能把异常抛出去
        }
    }

    [DllImport("user32.dll")]
    private static extern HMONITOR MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        nint hwnd, nint hdcDst, ref NativePoint pptDst, ref NativeSize psize,
        nint hdcSrc, ref NativePoint pptSrc, uint crKey, ref BlendFunction pblend, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    private static readonly object ClassLock = new();
    private static bool _classRegistered;
    private static HINSTANCE _hInstance;
    private const string ClassName = "PrtScrPinWindow";
    private static readonly Dictionary<nint, NativePinWindow> ByHwnd = new();

    private byte[] _pngBytes;
    private int _pixelWidth;
    private int _pixelHeight;
    private PinService.PinNavigation? _navigation;
    private long _lastNavigationTick;
    private NativePoint _clickDownCursor;
    private readonly DispatcherQueue _uiQueue;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private HWND _hwnd;
    private double _zoom;
    private double _opacity = 1;
    private int _winX;
    private int _winY;
    private int _winW;
    private int _winH;
    private readonly int _spawnOffsetX;
    private readonly int _spawnOffsetY;
    private int _maxWinW = 8192;
    private int _maxWinH = 8192;
    private bool _dragging;
    private bool _clickMoved;
    private NativePoint _dragStartCursor;
    private int _dragStartX;
    private int _dragStartY;
    private bool _ok;

    private System.Threading.Timer? _settleTimer;
    private System.Threading.Timer? _topmostTimer;
    private int _renderedW;
    private int _renderedH;
    private bool _needsRedrawAfterZoom;

    // 2A 贴图管理：状态只在这个贴图线程里改，管理器通过 PinService 快照读取。
    private readonly DateTime _createdAt = DateTime.Now;
    private RECT _workArea;
    private string _sourceScope = "贴图";
    private bool _locked;
    private bool _clickThrough;
    private bool _hidden;
    private string? _navHint;
    private long _navHintUntil;
    private long _flashUntil;
    private System.Threading.Timer? _navHintTimer;
    private System.Threading.Timer? _flashTimer;

    // 常驻 GDI 资源（避免每帧创建/销毁造成卡顿）
    private nint _screenDc;
    private nint _destDc;
    private nint _destDib;
    private nint _destBits;
    private int _destAllocW;
    private int _destAllocH;

    private NativePinWindow(byte[] pngBytes, int pixelWidth, int pixelHeight, DispatcherQueue uiQueue,
        int spawnOffsetX = 0, int spawnOffsetY = 0)
    {
        _pngBytes = pngBytes;
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _uiQueue = uiQueue;
        _spawnOffsetX = spawnOffsetX;
        _spawnOffsetY = spawnOffsetY;
    }

    /// <summary>把当前状态发布给管理器；只传不可变快照，管理器不直接碰贴图字段。</summary>
    /// <param name="notify">移动/缩放结束只更新注册表快照，不重建管理器卡片。</param>
    private void PublishPinState(bool notify = true)
    {
        if (_hwnd.IsNull)
            return;
        PinService.UpsertPin(new PinRecord(
            (nint)_hwnd.Value,
            _pngBytes,
            _pixelWidth,
            _pixelHeight,
            _winX,
            _winY,
            _winW,
            _winH,
            _opacity,
            _locked,
            _clickThrough,
            _hidden,
            _sourceScope,
            _navigation?.Index ?? 0,
            _navigation?.Files.Count ?? 0,
            _createdAt), notify);
    }

    /// <summary>新贴图/切换历史图片时的初始缩放：用户设定的原图百分比，再受工作区上限约束。</summary>
    private double ComputeInitialZoom()
    {
        var preferred = Math.Clamp(App.Config.Current.PinInitialScale, 80, 100) / 100.0;
        var fit = Math.Min(
            _maxWinW / (double)Math.Max(1, _pixelWidth),
            _maxWinH / (double)Math.Max(1, _pixelHeight));
        return Math.Max(0.01, Math.Min(preferred, fit));
    }

    /// <summary>当前所有贴图窗口句柄快照；用于临时让历史窗压过置顶贴图。</summary>
    public static nint[] SnapshotHandles()
    {
        lock (ClassLock)
            return [.. ByHwnd.Keys];
    }

    public static NativePinWindow? TryCreate(byte[] pngBytes, int pixelWidth, int pixelHeight, DispatcherQueue uiQueue,
        int spawnOffsetX = 0, int spawnOffsetY = 0)
    {
        var pin = new NativePinWindow(pngBytes, pixelWidth, pixelHeight, uiQueue, spawnOffsetX, spawnOffsetY);
        pin._thread = new Thread(pin.Run) { IsBackground = true, Name = "PrtScr.Pin" };
        pin._thread.SetApartmentState(ApartmentState.STA);
        pin._thread.Start();

        return pin._ready.Wait(TimeSpan.FromSeconds(3)) && pin._ok ? pin : null;
    }

    /// <summary>请求贴图线程进入标注模式（跨线程安全：只投递窗口消息）。</summary>
    public void RequestEditMode()
    {
        if (!_hwnd.IsNull)
            _ = PInvoke.PostMessage(_hwnd, WM_APP_ENTER_EDIT, default, default);
    }

    /// <summary>设置左右点击浏览的截图列表（截图历史分类或全部分类）。</summary>
    public void SetNavigation(PinService.PinNavigation navigation)
    {
        _navigation = navigation;
        _sourceScope = navigation.Scope;
        PublishPinState();
    }

    /// <summary>没有历史列表的贴图（例如文字贴图）也保留一个来源名，管理器里可区分。</summary>
    public void SetSourceScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return;
        _sourceScope = scope.Trim();
        PublishPinState();
    }

    /// <summary>在同一贴图窗内切换上一张/下一张截图，保持窗口中心不跳。</summary>
    private void Navigate(int direction)
    {
        if (_navigation is null || _navigation.Files.Count == 0 || _editMode || _locked)
            return;
        if (Environment.TickCount64 - _lastNavigationTick < 400)
            return;

        var index = _navigation.Index;
        var next = (index + direction) % _navigation.Files.Count;
        if (next < 0)
            next += _navigation.Files.Count;
        if (next == index)
            return;

        var path = _navigation.Files[next];
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            App.Log($"Pin navigate: 读取失败 {path} {ex.Message}");
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            using var image = Image.FromStream(new MemoryStream(bytes));
            if (image.Width <= 0 || image.Height <= 0)
                return;

            var centerX = _winX + _winW / 2;
            var centerY = _winY + _winH / 2;
            _pngBytes = bytes;
            _pixelWidth = image.Width;
            _pixelHeight = image.Height;

            _zoom = ComputeInitialZoom();
            _winW = Math.Max(16, (int)Math.Round(_pixelWidth * _zoom));
            _winH = Math.Max(16, (int)Math.Round(_pixelHeight * _zoom));
            _winX = centerX - _winW / 2;
            _winY = centerY - _winH / 2;

            _renderedW = -1;
            _renderedH = -1;
            _needsRedrawAfterZoom = true;
            // 先不擦旧画面，等新尺寸的内容准备好了再一次性 UpdateLayeredWindow，避免缩放中间帧。
            _ = PInvoke.SetWindowPos(_hwnd, default, _winX, _winY, _winW, _winH,
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOREDRAW);
            RenderAndPresent(highQuality: true, image);
            _navigation = _navigation with { Index = next };
            _lastNavigationTick = Environment.TickCount64;
            _sourceScope = _navigation.Scope;
            ShowNavigationHint(next + 1, _navigation.Files.Count, _navigation.Scope);
            PublishPinState();
            var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            App.Log($"Pin navigate: {_navigation.Scope} {next + 1}/{_navigation.Files.Count} {Path.GetFileName(path)} {ms}ms");
        }
        catch (Exception ex)
        {
            App.Log($"Pin navigate: 切换失败 {ex.Message}");
        }
    }

}

/// <summary>跨线程剪贴板写入（非 unsafe 上下文，供原生贴图窗口回调调用）。</summary>
internal static class PinClipboardHelper
{
    public static void OpenOcr(byte[] pngBytes, DispatcherQueue queue, nint ownerHwnd)
    {
        queue.TryEnqueue(async () =>
        {
            await PrtScr.App.Services.OcrUiService.RecognizeAndShowAsync(pngBytes, ownerHwnd);
        });
    }

    public static void OpenEditor(byte[] pngBytes, int pixelWidth, int pixelHeight, DispatcherQueue queue,
        Windows.Graphics.RectInt32 anchor)
    {
        queue.TryEnqueue(() =>
        {
            try
            {
                AnnotationWindow.Open(pngBytes, pixelWidth, pixelHeight, anchor);
            }
            catch (Exception ex)
            {
                App.Log($"打开标注编辑器失败: {ex}");
            }
        });
    }

    public static void Copy(byte[] pngBytes, DispatcherQueue queue)
    {
        queue.TryEnqueue(async () =>
        {
            await PrtScr.App.Services.ClipboardService.SetImageAsync(pngBytes);
        });
    }
}
