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

/// <summary>贴图窗口：创建、消息循环、类注册与任务栏遮挡判断。</summary>
public sealed unsafe partial class NativePinWindow
{
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

}
