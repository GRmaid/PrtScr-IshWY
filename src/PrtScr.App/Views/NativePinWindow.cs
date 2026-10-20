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
    private const uint WM_HOTKEY = 0x0312;


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
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        nint hwnd, nint hdcDst, ref NativePoint pptDst, ref NativeSize psize,
        nint hdcSrc, ref NativePoint pptSrc, uint crKey, ref BlendFunction pblend, uint dwFlags);

    private static readonly object ClassLock = new();
    private static bool _classRegistered;
    private static HINSTANCE _hInstance;
    private const string ClassName = "PrtScrPinWindow";
    private static readonly Dictionary<nint, NativePinWindow> ByHwnd = new();

    private byte[] _pngBytes;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
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
    private int _maxWinW = 8192;
    private int _maxWinH = 8192;
    private bool _dragging;
    private NativePoint _dragStartCursor;
    private int _dragStartX;
    private int _dragStartY;
    private bool _ok;

    private System.Threading.Timer? _settleTimer;
    private System.Threading.Timer? _topmostTimer;
    private int _renderedW;
    private int _renderedH;
    private bool _needsRedrawAfterZoom;

    // 常驻 GDI 资源（避免每帧创建/销毁造成卡顿）
    private nint _screenDc;
    private nint _destDc;
    private nint _destDib;
    private nint _destBits;
    private int _destAllocW;
    private int _destAllocH;

    private NativePinWindow(byte[] pngBytes, int pixelWidth, int pixelHeight, DispatcherQueue uiQueue)
    {
        _pngBytes = pngBytes;
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _uiQueue = uiQueue;
    }

    public static NativePinWindow? TryCreate(byte[] pngBytes, int pixelWidth, int pixelHeight, DispatcherQueue uiQueue)
    {
        var pin = new NativePinWindow(pngBytes, pixelWidth, pixelHeight, uiQueue);
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

            var workW = info.rcWork.right - info.rcWork.left;
            var workH = info.rcWork.bottom - info.rcWork.top;
            var maxW = workW * 0.8;
            var maxH = workH * 0.8;
            // 滚轮放大上限 = 工作区；避免贴图 DIB/分层表面随缩放无限膨胀（旧实现可到 8× = 数百 MB）
            _maxWinW = Math.Min(8192, workW);
            _maxWinH = Math.Min(8192, workH);
            _zoom = Math.Min(1, Math.Min(maxW / _pixelWidth, maxH / _pixelHeight));
            _winW = Math.Max(16, (int)Math.Round(_pixelWidth * _zoom));
            _winH = Math.Max(16, (int)Math.Round(_pixelHeight * _zoom));

            _winX = Math.Clamp(cursor.x - _winW / 2, info.rcWork.left, Math.Max(info.rcWork.left, info.rcWork.right - _winW));
            _winY = Math.Clamp(cursor.y - _winH / 2, info.rcWork.top, Math.Max(info.rcWork.top, info.rcWork.bottom - _winH));

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
            _escPollStop = true;
            ReleaseGdiResources();
            if (!_hwnd.IsNull)
            {
                EscRouter.Unregister((nint)_hwnd.Value);
                lock (ClassLock)
                    ByHwnd.Remove((nint)_hwnd.Value);
            }
        }
    }

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
            switch (msg)
            {
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

                case WM_LBUTTONDOWN:
                    GetCursorPos(out self._dragStartCursor);
                    self._dragStartX = self._winX;
                    self._dragStartY = self._winY;
                    self._dragging = true;
                    _ = PInvoke.SetCapture(hwnd);
                    _ = PInvoke.SetFocus(hwnd);
                    return default;

                case WM_MOUSEMOVE when self._dragging:
                    GetCursorPos(out var pt);
                    self._winX = self._dragStartX + (pt.x - self._dragStartCursor.x);
                    self._winY = self._dragStartY + (pt.y - self._dragStartCursor.y);
                    _ = PInvoke.SetWindowPos(hwnd, default, self._winX, self._winY, 0, 0,
                        SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                    return default;

                case WM_LBUTTONUP:
                    self._dragging = false;
                    _ = PInvoke.ReleaseCapture();
                    return default;

                case WM_LBUTTONDBLCLK when !self._editMode:
                    _ = PInvoke.DestroyWindow(hwnd);
                    return default;

                case WM_RBUTTONUP:
                    self.ShowMenu();
                    return default;

                case WM_MOUSEWHEEL:
                    self.OnWheel((short)((long)wParam.Value >> 16));
                    return default;

                case WM_APP_ZOOM_SETTLED:
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

    private void OnWheel(int delta)
    {
        if (delta == 0 || _editMode)
            return;

        var factor = delta > 0 ? 1.1 : 1 / 1.1;
        var maxZoom = Math.Min(8, Math.Min(
            _maxWinW / (double)Math.Max(1, _pixelWidth),
            _maxWinH / (double)Math.Max(1, _pixelHeight)));
        maxZoom = Math.Max(0.1, maxZoom);
        var newZoom = Math.Clamp(_zoom * factor, 0.1, maxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001)
            return;

        _zoom = newZoom;
        _winW = Math.Max(16, (int)Math.Round(_pixelWidth * _zoom));
        _winH = Math.Max(16, (int)Math.Round(_pixelHeight * _zoom));
        _ = PInvoke.MoveWindow(_hwnd, _winX, _winY, _winW, _winH, false);

        // 交互期走 GDI StretchBlt 快路径；停手 150ms 后高清精修
        RenderAndPresent(highQuality: false);

        _settleTimer?.Dispose();
        _settleTimer = new System.Threading.Timer(_ =>
        {
            if (!_hwnd.IsNull)
                _ = PInvoke.PostMessage(_hwnd, WM_APP_ZOOM_SETTLED, default, default);
        }, null, 150, Timeout.Infinite);
    }

    private void ShowMenu()
    {
        App.Log("Pin: menu enter");
        var menu = PInvoke.CreatePopupMenu();
        var opacityMenu = PInvoke.CreatePopupMenu();
        if (menu.IsNull)
            return;

        unsafe
        {
            fixed (char* copy = "复制图片")
            fixed (char* edit = "标注…")
            fixed (char* ocr = "识别文字")
            fixed (char* saveAs = "另存为…")
            fixed (char* close = "关闭贴图")
            fixed (char* opacity = "不透明度")
            fixed (char* p100 = "100%")
            fixed (char* p75 = "75%")
            fixed (char* p50 = "50%")
            fixed (char* p25 = "25%")
            {
                _ = PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, CmdCopy, new PCWSTR(copy));
                _ = PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, CmdEdit, new PCWSTR(edit));
                _ = PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, CmdOcr, new PCWSTR(ocr));
                _ = PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, CmdSaveAs, new PCWSTR(saveAs));
                _ = PInvoke.AppendMenu(opacityMenu, MENU_ITEM_FLAGS.MF_STRING, CmdOpacity100, new PCWSTR(p100));
                _ = PInvoke.AppendMenu(opacityMenu, MENU_ITEM_FLAGS.MF_STRING, CmdOpacity75, new PCWSTR(p75));
                _ = PInvoke.AppendMenu(opacityMenu, MENU_ITEM_FLAGS.MF_STRING, CmdOpacity50, new PCWSTR(p50));
                _ = PInvoke.AppendMenu(opacityMenu, MENU_ITEM_FLAGS.MF_STRING, CmdOpacity25, new PCWSTR(p25));
                _ = PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_POPUP, (nuint)opacityMenu.Value, new PCWSTR(opacity));
                _ = PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, default);
                _ = PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, CmdClose, new PCWSTR(close));
            }
        }

        GetCursorPos(out var pt);
        _ = PInvoke.SetForegroundWindow(_hwnd);

        // 菜单打开期间轮询让路：TrackPopupMenu 是模态循环，Esc 会先关菜单
        _menuOpen = true;
        int command;
        try
        {
            command = (int)PInvoke.TrackPopupMenu(
                menu,
                TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_LEFTALIGN | TRACK_POPUP_MENU_FLAGS.TPM_TOPALIGN,
                pt.x, pt.y, 0, _hwnd, default).Value;
        }
        finally
        {
            _menuOpen = false;
        }

        _ = PInvoke.DestroyMenu(menu);
        _ = PInvoke.PostMessage(_hwnd, WM_NULL, default, default);
        App.Log($"Pin: menu command={command}");

        switch (command)
        {
            case CmdCopy:
                PinClipboardHelper.Copy(_pngBytes, _uiQueue);
                break;
            case CmdEdit:
                EnterEditMode();
                break;
            case CmdOcr:
                App.Log("Pin: ocr menu clicked");
                PinClipboardHelper.OpenOcr(_pngBytes, _uiQueue, (nint)_hwnd.Value);
                break;
            case CmdSaveAs:
                if (_editMode)
                    ApplyEdit(saveAs: true);
                else
                    SaveAs();
                break;
            case CmdOpacity100:
                SetOpacity(1);
                break;
            case CmdOpacity75:
                SetOpacity(0.75);
                break;
            case CmdOpacity50:
                SetOpacity(0.5);
                break;
            case CmdOpacity25:
                SetOpacity(0.25);
                break;
            case CmdClose:
                _ = PInvoke.DestroyWindow(_hwnd);
                break;
        }
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
    }

    /// <summary>交互期：GDI StretchBlt 快速缩放；停手后 GDI+ 双三次高清精修。</summary>
    private void RenderAndPresent(bool highQuality)
    {
        if (_hwnd.IsNull || _winW <= 0 || _winH <= 0)
            return;
        if (_renderedW == _winW && _renderedH == _winH && highQuality && !_needsRedrawAfterZoom)
            return;

        try
        {
            EnsureScreenDc();

            if (highQuality)
                RenderHighQuality();
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
    private void RenderHighQuality()
    {
        EnsureScreenDc();
        EnsureDestDib(_winW, _winH);
        if (_destBits == 0)
        {
            RenderPlaceholder();
            return;
        }

        using var source = DecodeSource();
        if (source is null)
        {
            RenderPlaceholder();
            return;
        }

        using var bitmap = CreateDestBitmap();
        if (bitmap is null)
            return;

        using (var graphics = Graphics.FromImage(bitmap))
        using (var attributes = new ImageAttributes())
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            var matrix = new ColorMatrix { Matrix33 = (float)_opacity };
            attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            graphics.DrawImage(source.Image, new Rectangle(0, 0, _winW, _winH),
                0, 0, source.Image.Width, source.Image.Height, GraphicsUnit.Pixel, attributes);
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
