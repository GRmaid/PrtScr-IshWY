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

    private void Run()
    {
        try
        {
            RegisterClassOnce();

            GetCursorPos(out var cursor);
            var monitor = MonitorFromPoint(cursor, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            unsafe
            {
                _ = PInvoke.GetMonitorInfo(monitor, &info);
            }

            _workArea = info.rcWork;
            var workW = info.rcWork.right - info.rcWork.left;
            var workH = info.rcWork.bottom - info.rcWork.top;
            // 滚轮放大上限 = 工作区；避免贴图 DIB/分层表面随缩放无限膨胀（旧实现可到 8× = 数百 MB）
            _maxWinW = Math.Min(8192, workW);
            _maxWinH = Math.Min(8192, workH);
            _zoom = ComputeInitialZoom();
            _winW = Math.Max(16, (int)Math.Round(_pixelWidth * _zoom));
            _winH = Math.Max(16, (int)Math.Round(_pixelHeight * _zoom));

            _winX = Math.Clamp(cursor.x - _winW / 2 + _spawnOffsetX, info.rcWork.left, Math.Max(info.rcWork.left, info.rcWork.right - _winW));
            _winY = Math.Clamp(cursor.y - _winH / 2 + _spawnOffsetY, info.rcWork.top, Math.Max(info.rcWork.top, info.rcWork.bottom - _winH));

            _hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                ClassName,
                $"{PrtScr.App.AppInfo.Name} 贴图",
                WINDOW_STYLE.WS_POPUP,
                _winX, _winY, _winW, _winH,
                default, default, default, default);

            if (_hwnd.IsNull)
                return;

            lock (ClassLock)
                ByHwnd[(nint)_hwnd.Value] = this;

            EscRouter.RegisterPin(
                (nint)_hwnd.Value,
                () => _editMode,
                () => _menuOpen,
                () => _ = PInvoke.PostMessage(_hwnd, EscRouter.ExitEditMessage, default, default),
                () => _ = PInvoke.PostMessage(_hwnd, WM_CLOSE, default, default));

            RenderAndPresent(highQuality: true);
            _ = PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOW);

            // 任务栏同样带 TOPMOST 标记，贴图失去激活态时可能被排到任务栏之下
            // （TranslucentTB 会重排任务栏窗口，更容易触发）。定期重新宣告 TOPMOST（300ms）。
            _topmostTimer = new System.Threading.Timer(_ =>
            {
                if (!_hwnd.IsNull)
                    _ = PInvoke.PostMessage(_hwnd, WM_APP_REASSERT_TOPMOST, default, default);
            }, null, 300, 300);
            _ = PInvoke.SetForegroundWindow(_hwnd);
            _ = PInvoke.SetFocus(_hwnd);
            StartEscPoll();
            _ok = true;
            PublishPinState();
            _ready.Set();

            while (PInvoke.GetMessage(out var msg, default, 0, 0).Value > 0)
            {
                _ = PInvoke.TranslateMessage(msg);
                _ = PInvoke.DispatchMessage(msg);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Pin 窗口异常: {ex}");
            _ready.Set();
        }
        finally
        {
            _settleTimer?.Dispose();
            _topmostTimer?.Dispose();
            _navHintTimer?.Dispose();
            _flashTimer?.Dispose();
            _escPollStop = true;
            ReleaseGdiResources();
            if (!_hwnd.IsNull)
            {
                EscRouter.Unregister((nint)_hwnd.Value);
                PinService.RemovePin((nint)_hwnd.Value);
                lock (ClassLock)
                    ByHwnd.Remove((nint)_hwnd.Value);
            }
        }
    }

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern nint LoadCursor(nint hInstance, nint lpCursorName);

    private static void RegisterClassOnce()
    {
        lock (ClassLock)
        {
            if (_classRegistered)
                return;

            var wndClass = new WNDCLASSW
            {
                style = (WNDCLASS_STYLES)WM_CLASS_STYLE,
                lpfnWndProc = &WndProc,
                hInstance = _hInstance = new HINSTANCE(PInvoke.GetModuleHandle(default).DangerousGetHandle()),
                // 必须给窗口类一个光标：hCursor 为 NULL 时 DefWindowProc 在 WM_SETCURSOR
                // 会把光标隐藏，表现为鼠标停在贴图上就“消失/被贴图盖住”。
                // 文字编辑框是独立 EDIT 窗口，自带 I 型光标，不受这里影响。
                hCursor = new HCURSOR(LoadCursor(0, 32512 /* IDC_ARROW */)),
            };

            unsafe
            {
                fixed (char* className = ClassName)
                    wndClass.lpszClassName = new PCWSTR(className);
                _ = PInvoke.RegisterClass(wndClass);
            }

            _classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        NativePinWindow? self = null;
        if (!hwnd.IsNull)
        {
            lock (ClassLock)
                ByHwnd.TryGetValue((nint)hwnd.Value, out self);
        }

        if (self is not null)
        {
            // 右键菜单关闭的那一下点击会继续落到贴图上；菜单打开期间由低级鼠标钩子置位，
            // 这里整段跳过左键，避免“关菜单”顺带切成另一张截图。
            if (!self._editMode && PinClickGuard.IsSuppressed &&
                msg is WM_LBUTTONDOWN or WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                if (msg == WM_LBUTTONUP && self._dragging)
                {
                    self._dragging = false;
                    _ = PInvoke.ReleaseCapture();
                }
                return default;
            }

            switch (msg)
            {
                case WmAppPinCommand:
                    self.HandlePinCommand((PinCommand)(int)wParam.Value);
                    return default;

                case WM_LBUTTONDBLCLK when !self._editMode:
                    if (!self._locked)
                        _ = PInvoke.DestroyWindow(hwnd);
                    return default;

                case WM_LBUTTONDOWN when self._editMode:
                    self.OnEditPointerDown(lParam);
                    return default;

                case WM_MOUSEMOVE when self._editMode && self._editPanning:
                    self.MoveEditPan();
                    return default;

                case WM_MOUSEMOVE when self._editMode:
                    self.OnEditPointerMove(lParam);
                    return default;

                case WM_LBUTTONUP when self._editMode:
                    self.OnEditPointerUp(lParam);
                    return default;

                case WM_MBUTTONDOWN when self._editMode:
                    self.BeginEditPan(lParam);
                    return default;

                case WM_MBUTTONUP when self._editPanning:
                    self.EndEditPan();
                    return default;

                case WM_RBUTTONDOWN when self._editMode:
                    self.BeginEditPan(lParam);
                    return default;

                case WM_RBUTTONUP when self._editMode && self._editPanning:
                    if (!self.EndEditPan())
                        self.ShowMenu();
                    return default;

                case WM_KEYDOWN when (int)wParam.Value == VK_ESCAPE:
                    // 不直接关窗：交给 EscRouter 判断该关贴图、关 OCR，还是只退出标注
                    EscRouter.Dispatch();
                    return default;

                case WM_KEYDOWN when self._editMode && (int)wParam.Value == 0x5A
                    && (GetKeyState(0x11) & 0x8000) != 0:
                    self.UndoEdit();
                    return default;

                case WM_KEYDOWN when self._editMode && (int)wParam.Value == 0x0D:
                    self.ApplyEdit();
                    return default;

                case WM_KEYDOWN when self._editMode && (int)wParam.Value == 0x53
                    && (GetKeyState(0x11) & 0x8000) != 0:
                    self.ApplyEdit(saveAs: true);
                    return default;

                case WM_LBUTTONDOWN:
                    if (self._locked)
                        return default;
                    GetCursorPos(out self._dragStartCursor);
                    self._clickDownCursor = self._dragStartCursor;
                    self._clickMoved = false;
                    self._dragStartX = self._winX;
                    self._dragStartY = self._winY;
                    self._dragging = true;
                    PinSnapGuide.Hide();
                    _ = PInvoke.SetCapture(hwnd);
                    _ = PInvoke.SetFocus(hwnd);
                    return default;

                case WM_MOUSEMOVE when self._dragging:
                    GetCursorPos(out var pt);
                    if (Math.Abs(pt.x - self._clickDownCursor.x) > 4 ||
                        Math.Abs(pt.y - self._clickDownCursor.y) > 4)
                        self._clickMoved = true;
                    self._winX = self._dragStartX + (pt.x - self._dragStartCursor.x);
                    self._winY = self._dragStartY + (pt.y - self._dragStartCursor.y);
                    if (self._clickMoved)
                        self.ApplySnap();
                    _ = PInvoke.SetWindowPos(hwnd, default, self._winX, self._winY, 0, 0,
                        SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOREDRAW);
                    return default;

                case WM_LBUTTONUP:
                    self._dragging = false;
                    PinSnapGuide.Hide();
                    _ = PInvoke.ReleaseCapture();
                    // 未拖动的单击：左半边看更新的一张，右半边看更旧的一张。
                    // 有分类上下文时只在当前分类内循环；Alt+S 贴图使用全部分类。
                    if (!self._clickMoved && self._navigation is not null && !self._editMode && !self._locked)
                    {
                        GetCursorPos(out var up);
                        var direction = up.x < self._winX + self._winW / 2 ? -1 : 1;
                        self.Navigate(direction);
                    }
                    self.PublishPinState(notify: false);
                    return default;

                case WM_RBUTTONUP:
                    self.ShowMenu();
                    return default;

                case WM_MOUSEWHEEL:
                    // WM_MOUSEWHEEL 的 lParam 是屏幕坐标；用它做缩放锚点，消息积压时也不会
                    // 因为此刻 GetCursorPos 已经移动而把锚点带偏。
                    var wheelPos = (long)lParam.Value;
                    self.OnWheel(
                        (short)((long)wParam.Value >> 16),
                        (short)(wheelPos & 0xFFFF),
                        (short)((wheelPos >> 16) & 0xFFFF));
                    return default;

                case WM_APP_ZOOM_SETTLED:
                    // 缩小后把曾经为最大尺寸分配的 DIB 换回当前尺寸，避免峰值内存长期驻留。
                    self.MaybeShrinkDestDib();
                    self.RenderAndPresent(highQuality: true);
                    self.PublishPinState(notify: false);
                    return default;

                case WM_APP_CLEAR_NAV_HINT:
                    self._navHint = null;
                    self._navHintUntil = 0;
                    self._needsRedrawAfterZoom = true;
                    self.RenderAndPresent(highQuality: true);
                    return default;

                case WM_APP_END_FLASH:
                    self._flashUntil = 0;
                    self._needsRedrawAfterZoom = true;
                    self.RenderAndPresent(highQuality: true);
                    return default;

                case WM_APP_ENTER_EDIT:
                    if (!self._editMode)
                        self.EnterEditMode();
                    return default;

                case EscRouter.ExitEditMessage:
                    if (self._editMode)
                        self.ExitEditMode(apply: false);
                    return default;

                case WM_APP_REASSERT_TOPMOST:
                    // 只在压到任务栏区域（工作区之外）时才补置顶：
                    // 远离任务栏时，避免和 bongo cat / 桌面歌词等同样抢 topmost 的悬浮窗
                    // 互相拉锯——那会表现为贴图闪烁
                    if (self.IsTaskbarCoveringUs())
                    {
                        _ = PInvoke.SetWindowPos(hwnd, new HWND(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
                            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
                    }
                    return default;

                case WM_CLOSE:
                    PinSnapGuide.Hide();
                    App.Log($"Pin: WM_CLOSE (edit={self._editMode})");
                    _ = PInvoke.DestroyWindow(hwnd);
                    return default;

                case WM_DESTROY:
                    PInvoke.PostQuitMessage(0);
                    return default;
            }
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 任务栏是否真的盖在贴图上面。
    /// 只看矩形相交会误判（大贴图常同时压着任务栏和悬浮窗），导致和 bongo cat/歌词
    /// 反复抢置顶而闪烁；这里用 WindowFromPoint 实测该点最上层窗口是不是任务栏。
    /// </summary>
    private bool IsTaskbarCoveringUs()
    {
        var center = new NativePoint { x = _winX + _winW / 2, y = _winY + _winH / 2 };
        var monitor = MonitorFromPoint(center, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        unsafe
        {
            if (!PInvoke.GetMonitorInfo(monitor, &info))
                return false;
        }

        var mon = info.rcMonitor;
        var work = info.rcWork;
        int winL = _winX, winT = _winY, winR = _winX + _winW, winB = _winY + _winH;

        // 依次检查四条任务栏条带，取第一个与窗口相交的，在交集中心取样
        if (!TryIntersect(winL, winT, winR, winB, mon.left, mon.top, mon.right, work.top, out var sample) &&
            !TryIntersect(winL, winT, winR, winB, mon.left, work.bottom, mon.right, mon.bottom, out sample) &&
            !TryIntersect(winL, winT, winR, winB, mon.left, mon.top, work.left, mon.bottom, out sample) &&
            !TryIntersect(winL, winT, winR, winB, work.right, mon.top, mon.right, mon.bottom, out sample))
        {
            return false;
        }

        var top = WindowFromPoint(sample);
        if (top == 0 || top == (nint)_hwnd.Value)
            return false;

        var name = new System.Text.StringBuilder(256);
        _ = GetClassName(top, name, name.Capacity);
        var cls = name.ToString();
        return cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static bool TryIntersect(int al, int at, int ar, int ab, int bl, int bt, int br, int bb, out NativePoint sample)
    {
        var l = Math.Max(al, bl);
        var t = Math.Max(at, bt);
        var r = Math.Min(ar, br);
        var b = Math.Min(ab, bb);
        if (l < r && t < b)
        {
            sample = new NativePoint { x = (l + r) / 2, y = (t + b) / 2 };
            return true;
        }

        sample = default;
        return false;
    }

    private void OnWheel(int delta, int screenX, int screenY)
    {
        if (delta == 0)
            return;
        if (_locked && !_editMode)
            return;
        if (_editMode)
        {
            // 标注模式下滚轮不缩放贴图；文字工具时用来调字号，和输入框里的滚轮一致。
            if (_toolId == 3)
                AdjustTextSize(delta > 0 ? TextSizeStep : -TextSizeStep);
            return;
        }

        var factor = delta > 0 ? 1.1 : 1 / 1.1;
        var maxZoom = Math.Min(8, Math.Min(
            _maxWinW / (double)Math.Max(1, _pixelWidth),
            _maxWinH / (double)Math.Max(1, _pixelHeight)));
        maxZoom = Math.Max(0.1, maxZoom);
        var newZoom = Math.Clamp(_zoom * factor, 0.1, maxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001)
            return;

        // 以鼠标指针为锚点缩放：记录指针在当前窗口里的归一化位置，
        // 尺寸变化后反推窗口左上角，让指针下的同一图像素留在指针下。
        // 之前只改 _winW/_winH 不动 _winX/_winY，所以永远从左上方固定缩放。
        var anchorX = Math.Clamp((screenX - _winX) / (double)Math.Max(1, _winW), 0, 1);
        var anchorY = Math.Clamp((screenY - _winY) / (double)Math.Max(1, _winH), 0, 1);

        _zoom = newZoom;
        _winW = Math.Max(16, (int)Math.Round(_pixelWidth * _zoom));
        _winH = Math.Max(16, (int)Math.Round(_pixelHeight * _zoom));
        _winX = (int)Math.Round(screenX - anchorX * _winW);
        _winY = (int)Math.Round(screenY - anchorY * _winH);

        // 不再先 MoveWindow 再 UpdateLayeredWindow：后者本身就会按 psize 调整窗口尺寸和位置。
        // 连续滚轮时每帧只提交一次分层表面，避免 DWM 为每个中间尺寸各留一张表面。
        // 交互期走占位边框；停手 350ms 后再高清精修（150ms 会在快速滚轮中途反复触发）。
        RenderAndPresent(highQuality: false);

        _settleTimer?.Dispose();
        _settleTimer = new System.Threading.Timer(_ =>
        {
            if (!_hwnd.IsNull)
                _ = PInvoke.PostMessage(_hwnd, WM_APP_ZOOM_SETTLED, default, default);
        }, null, 350, Timeout.Infinite);
    }

    private RECT CurrentWorkArea()
    {
        var monitor = new HMONITOR(MonitorFromWindow((nint)_hwnd.Value, 2 /* MONITOR_DEFAULTTONEAREST */));
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        unsafe
        {
            if (!PInvoke.GetMonitorInfo(monitor, &info))
                return _workArea;
        }
        return info.rcWork;
    }

    /// <summary>拖动时吸附到工作区边缘或其他贴图边缘，并在命中处显示 1px 参考线。</summary>
    private void ApplySnap()
    {
        var work = CurrentWorkArea();
        var threshold = Math.Max(3, (int)Math.Round(8 * Math.Max(1, _dpiScale)));
        var x = _winX;
        var y = _winY;
        int? guideX = null;
        int? guideY = null;
        var bestX = int.MaxValue;
        var bestY = int.MaxValue;

        void ConsiderX(int target, int guide, int distance)
        {
            if (distance <= threshold && distance < bestX)
            {
                bestX = distance;
                x = target;
                guideX = guide;
            }
        }

        void ConsiderY(int target, int guide, int distance)
        {
            if (distance <= threshold && distance < bestY)
            {
                bestY = distance;
                y = target;
                guideY = guide;
            }
        }

        ConsiderX(work.left, work.left, Math.Abs(_winX - work.left));
        ConsiderX(work.right - _winW, work.right, Math.Abs(_winX + _winW - work.right));
        ConsiderY(work.top, work.top, Math.Abs(_winY - work.top));
        ConsiderY(work.bottom - _winH, work.bottom, Math.Abs(_winY + _winH - work.bottom));

        foreach (var pin in PinService.SnapshotPins())
        {
            if (pin.Hwnd == (nint)_hwnd.Value || pin.Hidden)
                continue;
            ConsiderX(pin.X - _winW, pin.X, Math.Abs(_winX + _winW - pin.X));
            ConsiderX(pin.X + pin.Width, pin.X + pin.Width, Math.Abs(_winX - (pin.X + pin.Width)));
            ConsiderY(pin.Y - _winH, pin.Y, Math.Abs(_winY + _winH - pin.Y));
            ConsiderY(pin.Y + pin.Height, pin.Y + pin.Height, Math.Abs(_winY - (pin.Y + pin.Height)));
        }

        _winX = x;
        _winY = y;
        if (guideX.HasValue || guideY.HasValue)
            PinSnapGuide.Show(guideX, work.top, work.bottom, guideY, work.left, work.right);
        else
            PinSnapGuide.Hide();
    }

    private void ShowMenu()
    {
        App.Log("Pin: menu enter");
        GetCursorPos(out var pt);
        _ = PInvoke.SetForegroundWindow(_hwnd);

        // 菜单期间让 Esc 轮询让路：菜单线程自己处理 Esc 和点击外部。
        _menuOpen = true;
        int command;
        try
        {
            command = PinMenuWindow.Show((nint)_hwnd.Value, pt.x, pt.y, _opacity, _locked, _clickThrough);
        }
        finally
        {
            _menuOpen = false;
        }

        _ = PInvoke.PostMessage(_hwnd, WM_NULL, default, default);
        App.Log($"Pin: menu command={command}");

        switch (command)
        {
            case PinMenuWindow.CmdCopy:
                PinClipboardHelper.Copy(_pngBytes, _uiQueue);
                break;
            case PinMenuWindow.CmdEdit:
                EnterEditMode();
                break;
            case PinMenuWindow.CmdCrop:
                StartCrop();
                break;
            case PinMenuWindow.CmdOcr:
                App.Log("Pin: ocr menu clicked");
                PinClipboardHelper.OpenOcr(_pngBytes, _uiQueue, (nint)_hwnd.Value);
                break;
            case PinMenuWindow.CmdSaveAs:
                if (_editMode)
                    ApplyEdit(saveAs: true);
                else
                    SaveAs();
                break;
            case PinMenuWindow.CmdHistory:
                PinService.RequestHistory((nint)_hwnd.Value);
                break;
            case PinMenuWindow.CmdToggleLock:
                HandlePinCommand(PinCommand.ToggleLock);
                break;
            case PinMenuWindow.CmdToggleThrough:
                HandlePinCommand(PinCommand.ToggleThrough);
                break;
            case PinMenuWindow.CmdOpacity100:
                SetOpacity(1);
                break;
            case PinMenuWindow.CmdOpacity75:
                SetOpacity(0.75);
                break;
            case PinMenuWindow.CmdOpacity50:
                SetOpacity(0.5);
                break;
            case PinMenuWindow.CmdOpacity25:
                SetOpacity(0.25);
                break;
            case PinMenuWindow.CmdClose:
                _ = PInvoke.DestroyWindow(_hwnd);
                break;
        }
    }

    /// <summary>
    /// 裁剪：在贴图上方拖框，确认后按动作生成新贴图 / OCR / 保存，原贴图保留。
    /// 选区和原图立即返回，解码/裁剪/编码放到线程池，避免回车后贴图线程和 UI 一起卡住。
    /// </summary>
    private void StartCrop()
    {
        var result = PinCropWindow.Show((nint)_hwnd.Value, _winX, _winY, _winW, _winH,
            _pngBytes, _pixelWidth, _pixelHeight);
        if (result is null)
        {
            App.Log("Pin: crop cancelled");
            return;
        }

        App.Log($"Pin: crop {result.Action} {result.Width}x{result.Height} at {result.Left},{result.Top}");
        ThreadPool.QueueUserWorkItem(_ => ProcessCrop(result));
    }

    /// <summary>线程池里只做 GDI+ 解码/裁剪/PNG 编码，结果再回 UI 线程执行贴图/OCR/保存。</summary>
    private void ProcessCrop(PinCropResult result)
    {
        byte[]? bytes = null;
        try
        {
            var started = Environment.TickCount64;
            MemoryProbe.Log("crop-start");
            using var source = Image.FromStream(new MemoryStream(result.SourcePngBytes, writable: false));
            using var cropped = new Bitmap(result.Width, result.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cropped))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(source, new Rectangle(0, 0, cropped.Width, cropped.Height),
                    result.Left, result.Top, result.Width, result.Height, GraphicsUnit.Pixel);
            }
            using var stream = new MemoryStream();
            cropped.Save(stream, ImageFormat.Png);
            bytes = stream.ToArray();
            App.Log($"Pin crop: encoded {result.Width}x{result.Height} {bytes.Length / 1024}KB {Environment.TickCount64 - started}ms");
        }
        catch (Exception ex)
        {
            App.Log($"Pin crop: 生成失败 {ex}");
            return;
        }

        MemoryProbe.Log("crop-encoded");
        if (App.Config.Current.HistoryPinCrop)
            _ = CaptureHistoryService.Instance.AddAsync(bytes, HistoryCaptureMode.PinCrop);

        var hwnd = (nint)_hwnd.Value;
        _uiQueue.TryEnqueue(() =>
        {
            switch (result.Action)
            {
                case PinCropAction.Pin:
                    PinService.PastePngOffset(bytes, 24, 24);
                    break;
                case PinCropAction.Ocr:
                    PinClipboardHelper.OpenOcr(bytes, _uiQueue, hwnd);
                    break;
                case PinCropAction.Save:
                    _ = FileSaveService.SavePngAsync(hwnd, bytes, FileSaveService.SuggestedName("crop"));
                    break;
            }
            MemoryProbe.Log("crop-done");
        });
    }

    /// <summary>在当前贴图上打开系统另存为对话框。unsafe 类里不能写 async，因此只 fire-and-forget。</summary>
    private void SaveAs()
    {
        _ = PrtScr.App.Services.FileSaveService.SavePngAsync(
            (nint)_hwnd.Value, _pngBytes, PrtScr.App.Services.FileSaveService.SuggestedName("pin"));
    }

    private void SetOpacity(double value)
    {
        _opacity = value;
        _needsRedrawAfterZoom = true;
        RenderAndPresent(highQuality: true);
        PublishPinState();
    }

    /// <summary>管理器发来的贴图命令：只在这个贴图线程执行，避免跨线程碰窗口字段。</summary>
    private void HandlePinCommand(PinCommand command)
    {
        switch (command)
        {
            case PinCommand.Focus:
                if (_hidden)
                {
                    _hidden = false;
                    _ = PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
                }
                // 管理器打开时所有贴图已被临时降为 NOTOPMOST；这里只提到普通窗口顶端，
                // 不能重新宣布 TOPMOST，否则选中的贴图会盖住管理器。关闭管理器时统一恢复置顶。
                _ = PInvoke.SetWindowPos(_hwnd, default, 0, 0, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
                FlashPin(900);
                PublishPinState();
                break;

            case PinCommand.Hide:
                _hidden = true;
                _ = PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
                PublishPinState();
                break;

            case PinCommand.Show:
                _hidden = false;
                _ = PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
                _ = PInvoke.SetWindowPos(_hwnd, new HWND(-1), 0, 0, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
                PublishPinState();
                break;

            case PinCommand.ToggleLock:
                _locked = !_locked;
                if (_locked && _dragging)
                {
                    _dragging = false;
                    _ = PInvoke.ReleaseCapture();
                }
                PublishPinState();
                RefreshPinOverlays();
                break;

            case PinCommand.ToggleThrough:
                _clickThrough = !_clickThrough;
                ApplyClickThrough();
                PublishPinState();
                RefreshPinOverlays();
                break;

            case PinCommand.RestoreInteraction:
                _locked = false;
                _clickThrough = false;
                ApplyClickThrough();
                PublishPinState();
                RefreshPinOverlays();
                break;

            case PinCommand.Flash:
                FlashPin(1000);
                break;

            case PinCommand.Close:
                _ = PInvoke.DestroyWindow(_hwnd);
                break;
        }
    }

    private void ApplyClickThrough()
    {
        if (_hwnd.IsNull)
            return;
        var style = GetWindowLongPtr((nint)_hwnd.Value, GWL_EXSTYLE).ToInt64();
        style = _clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        _ = SetWindowLongPtr((nint)_hwnd.Value, GWL_EXSTYLE, (nint)style);
        _ = PInvoke.SetWindowPos(_hwnd, default, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
    }

    private void RefreshPinOverlays()
    {
        _needsRedrawAfterZoom = true;
        RenderAndPresent(highQuality: true);
    }

    private void FlashPin(int milliseconds)
    {
        _flashUntil = Environment.TickCount64 + milliseconds;
        RefreshPinOverlays();
        _flashTimer?.Dispose();
        _flashTimer = new System.Threading.Timer(_ =>
        {
            if (!_hwnd.IsNull)
                _ = PInvoke.PostMessage(_hwnd, WM_APP_END_FLASH, default, default);
        }, null, milliseconds + 40, Timeout.Infinite);
    }

    private void ShowNavigationHint(int index, int count, string scope)
    {
        if (_winW < 140 * (_dpiScale <= 0 ? 1.0 : _dpiScale))
            return;
        _navHint = $"{index} / {count} · {scope}";
        _navHintUntil = Environment.TickCount64 + 1200;
        RefreshPinOverlays();
        _navHintTimer?.Dispose();
        _navHintTimer = new System.Threading.Timer(_ =>
        {
            if (!_hwnd.IsNull)
                _ = PInvoke.PostMessage(_hwnd, WM_APP_CLEAR_NAV_HINT, default, default);
        }, null, 1250, Timeout.Infinite);
    }

    /// <summary>交互期：GDI StretchBlt 快速缩放；停手后 GDI+ 双三次高清精修。</summary>
    private void RenderAndPresent(bool highQuality, Image? sourceOverride = null)
    {
        if (_hwnd.IsNull || _winW <= 0 || _winH <= 0)
            return;
        if (_renderedW == _winW && _renderedH == _winH && highQuality && !_needsRedrawAfterZoom)
            return;

        try
        {
            EnsureScreenDc();

            if (highQuality)
                RenderHighQuality(sourceOverride);
            else
                RenderPlaceholder();

            Present();
            _renderedW = _winW;
            _renderedH = _winH;
            _needsRedrawAfterZoom = !highQuality;
        }
        catch (Exception ex)
        {
            App.Log($"Pin 渲染失败: {ex}");
        }
    }

    private void EnsureScreenDc()
    {
        if (_screenDc == 0)
            _screenDc = Gdi.GetDC(0);
    }

    /// <summary>源图按当前不透明度预乘进常驻源 DIB（只有透明度变化时才重建）。</summary>

    private void EnsureDestDib(int width, int height)
    {
        if (_destDib != 0 && width <= _destAllocW && height <= _destAllocH)
            return;

        ReleaseDestResources();

        _destAllocW = Math.Min(8192, Math.Max(width, 256));
        _destAllocH = Math.Max(height, 256);
        _destDib = CreateDib(_destAllocW, _destAllocH, out _destBits);
        if (_destDib == 0)
            return;

        _destDc = Gdi.CreateCompatibleDC(_screenDc);
        _ = Gdi.SelectObject(_destDc, _destDib);
    }

    /// <summary>
    /// 滚轮把窗口缩回去后，DIB 仍按历史最大尺寸常驻（2560×1600 约 16MB）。
    /// 只在窗口已小于分配面积的一半时重建，避免轻微缩放就反复申请 GDI 资源。
    /// </summary>
    private void MaybeShrinkDestDib()
    {
        if (_destDib == 0 || _winW <= 0 || _winH <= 0)
            return;

        var needW = Math.Min(8192, Math.Max(_winW, 256));
        var needH = Math.Max(_winH, 256);
        if (needW >= _destAllocW && needH >= _destAllocH)
            return;

        var used = (long)_winW * _winH;
        var allocated = (long)_destAllocW * _destAllocH;
        if (used * 2 > allocated)
            return;

        App.Log($"Pin zoom: 收缩 DIB {_destAllocW}×{_destAllocH} -> {needW}×{needH}");
        ReleaseDestResources();
        EnsureDestDib(_winW, _winH);
    }

    /// <summary>
    /// 交互期占位：只画目标尺寸的半透明边框（+极淡填充），不缩放假图——
    /// 既省资源，又避免 GDI StretchBlt 不保留 Alpha 导致的“背景透明”与逐帧闪烁。
    /// </summary>
    private void RenderPlaceholder()
    {
        EnsureDestDib(_winW, _winH);
        if (_destBits == 0)
            return;

        const int Thickness = 2;
        // 预乘 alpha：边框青色 0x00C8FF（A=0x90），内部极淡白（A=0x14）
        const uint BorderPixel = 0x90007190u;
        const uint Fill = 0x14141414u;

        var stride = _destAllocW;
        var basePtr = (uint*)_destBits;

        // 逐行向量化填充（Span.Fill 走 SIMD，大窗口也不慢）
        for (var y = 0; y < _winH; y++)
            new Span<uint>(basePtr + y * stride, _winW).Fill(Fill);

        for (var y = 0; y < _winH; y++)
        {
            var row = basePtr + y * stride;
            if (y < Thickness || y >= _winH - Thickness)
            {
                new Span<uint>(row, _winW).Fill(BorderPixel);
                continue;
            }

            for (var x = 0; x < Thickness; x++)
            {
                row[x] = BorderPixel;
                row[_winW - 1 - x] = BorderPixel;
            }
        }
    }

    /// <summary>
    /// 直接从 PNG 解码绘制到常驻 DIB，不再常驻一份全分辨率 Bitmap。
    /// 贴图闲置时只保留压缩 PNG + 目标 DIB（各一张），内存从 ~70MB 降到 ~25MB。
    /// </summary>
    private void RenderHighQuality(Image? sourceOverride = null)
    {
        EnsureScreenDc();
        EnsureDestDib(_winW, _winH);
        if (_destBits == 0)
        {
            RenderPlaceholder();
            return;
        }

        using var decoded = sourceOverride is null ? DecodeSource() : null;
        var image = sourceOverride ?? decoded?.Image;
        if (image is null)
        {
            RenderPlaceholder();
            return;
        }

        using var bitmap = CreateDestBitmap();
        if (bitmap is null)
            return;

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;

            if (_opacity >= 0.999)
            {
                // 不透明度 100% 是最常见路径：SourceCopy 不做逐像素混合，也不建 ColorMatrix 临时面，
                // 滚动缩放时的瞬时分配明显少于 SourceOver + ImageAttributes。
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(image, new Rectangle(0, 0, _winW, _winH),
                    0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
            }
            else
            {
                using var attributes = new ImageAttributes();
                graphics.CompositingMode = CompositingMode.SourceOver;
                var matrix = new ColorMatrix { Matrix33 = (float)_opacity };
                attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                graphics.DrawImage(image, new Rectangle(0, 0, _winW, _winH),
                    0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }

            DrawPinOverlays(graphics);
        }
    }

    /// <summary>锁定 / 点击穿透徽标、切换截图索引提示、管理器聚焦时的闪烁边框。</summary>
    private void DrawPinOverlays(Graphics graphics)
    {
        var scale = _hwnd.IsNull ? 1.0 : Math.Max(1.0, PInvoke.GetDpiForWindow(_hwnd) / 96.0);
        var size = 22 * scale;
        var gap = 5 * scale;
        var x = 8 * scale;
        var y = 8 * scale;

        using var badgeBrush = new SolidBrush(Color.FromArgb(0xCC, 0x1E, 0x1E, 0x20));
        using var badgeText = new SolidBrush(Color.White);
        using var badgeFont = new Font("Microsoft YaHei UI", 10 * (float)scale, FontStyle.Bold, GraphicsUnit.Pixel);
        if (_locked)
        {
            var rect = new RectangleF((float)x, (float)y, (float)size, (float)size);
            FillRoundedRectangle(graphics, badgeBrush, rect, 4 * (float)scale);
            DrawCenteredText(graphics, "锁", badgeFont, badgeText, rect);
            x += size + gap;
        }
        if (_clickThrough)
        {
            var rect = new RectangleF((float)x, (float)y, (float)size, (float)size);
            FillRoundedRectangle(graphics, badgeBrush, rect, 4 * (float)scale);
            DrawCenteredText(graphics, "穿", badgeFont, badgeText, rect);
        }

        if (_flashUntil > Environment.TickCount64)
        {
            using var pen = new Pen(Color.FromArgb(0xFF, 0x00, 0xC8, 0xFF), 3 * (float)scale);
            DrawRoundedBorder(graphics, pen, new RectangleF(1.5f, 1.5f, _winW - 3f, _winH - 3f), 4 * (float)scale);
        }

        if (_navHint is not null && _navHintUntil > Environment.TickCount64)
        {
            using var chipBrush = new SolidBrush(Color.FromArgb(0xD8, 0x1E, 0x1E, 0x20));
            using var chipText = new SolidBrush(Color.White);
            using var chipFont = new Font("Microsoft YaHei UI", 11 * (float)scale, FontStyle.Regular, GraphicsUnit.Pixel);
            var measured = graphics.MeasureString(_navHint, chipFont);
            var chipW = Math.Max(150 * (float)scale, measured.Width + 26 * (float)scale);
            var chipH = 28 * (float)scale;
            var chip = new RectangleF(
                (float)(_winW - chipW) / 2f,
                (float)(_winH - chipH) - 14 * (float)scale,
                chipW, chipH);
            FillRoundedRectangle(graphics, chipBrush, chip, 6 * (float)scale);
            DrawCenteredText(graphics, _navHint, chipFont, chipText, chip);
        }
    }

    /// <summary>按需解码 PNG，不再复制一份全分辨率位图（省约 16 MB 瞬时占用）。调用方负责 Dispose。</summary>
    private DecodedSource? DecodeSource()
    {
        MemoryStream? stream = null;
        try
        {
            stream = new MemoryStream(_pngBytes, writable: false);
            var image = Image.FromStream(stream);
            return new DecodedSource(image, stream);
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            App.Log($"Pin 解码源图失败: {ex}");
            return null;
        }
    }

    /// <summary>把常驻 DIB 包装成 Bitmap 直接绘制，避免每帧再分配一张全窗口位图。</summary>
    private Bitmap? CreateDestBitmap()
    {
        EnsureScreenDc();
        EnsureDestDib(_winW, _winH);
        if (_destBits == 0 || _destAllocW <= 0 || _destAllocH <= 0)
            return null;

        return new Bitmap(_winW, _winH, _destAllocW * 4, PixelFormat.Format32bppPArgb, _destBits);
    }

    private void Present()
    {
        if (_hwnd.IsNull || _destDc == 0)
            return;

        var dst = new NativePoint { x = _winX, y = _winY };
        var size = new NativeSize { cx = _winW, cy = _winH };
        var src = new NativePoint();
        var blend = new BlendFunction
        {
            BlendOp = 0,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1,
        };

        _ = UpdateLayeredWindow(
            (nint)_hwnd.Value, _screenDc, ref dst, ref size, _destDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
    }

    private nint CreateDib(int width, int height, out nint bits)
    {
        var info = new BitmapInfo
        {
            bmiHeader = new BitmapInfoHeader
            {
                biSize = Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height,   // 负数 = top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,    // BI_RGB
            },
        };
        return Gdi.CreateDIBSection(_screenDc, ref info, 0, out bits, 0, 0);
    }


    private void ReleaseDestResources()
    {
        if (_destDc != 0) _ = Gdi.DeleteDC(_destDc);
        if (_destDib != 0) _ = Gdi.DeleteObject(_destDib);
        _destDc = 0;
        _destDib = 0;
        _destBits = 0;
    }

    private void ReleaseGdiResources()
    {
        ReleaseDestResources();
        if (_screenDc != 0) _ = Gdi.ReleaseDC(0, _screenDc);
        _screenDc = 0;
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
