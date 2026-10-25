using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Views;

/// <summary>
/// 贴图拖动时的 1px 对齐参考线：两个分层置顶小窗，横向/纵向各一条。
/// 线程独立于贴图，避免拖动中的贴图线程再承担额外的窗口消息。
/// </summary>
internal static unsafe class PinSnapGuide
{
    private const uint WM_APP_REFRESH = 0x8000 + 70;
    private const string ClassName = "PrtScrSnapGuide";
    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern nint LoadCursorW(nint hInstance, nint lpCursorName);

    [DllImport("user32.dll", EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("gdi32.dll")]
    private static extern nint CreateSolidBrush(uint color);

    [DllImport("user32.dll", EntryPoint = "SetLayeredWindowAttributes")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int cmd);

    private static readonly object Gate = new();
    private static readonly ManualResetEventSlim Ready = new(false);
    private static Thread? _thread;
    private static nint _vertical;
    private static nint _horizontal;
    private static bool _verticalVisible;
    private static bool _horizontalVisible;
    private static int _vx, _vTop, _vBottom;
    private static int _hy, _hLeft, _hRight;

    public static void Show(int? verticalX, int top, int bottom, int? horizontalY, int left, int right)
    {
        EnsureStarted();
        lock (Gate)
        {
            _verticalVisible = verticalX.HasValue;
            _horizontalVisible = horizontalY.HasValue;
            if (verticalX.HasValue)
            {
                _vx = verticalX.Value;
                _vTop = top;
                _vBottom = bottom;
            }
            if (horizontalY.HasValue)
            {
                _hy = horizontalY.Value;
                _hLeft = left;
                _hRight = right;
            }
        }
        Post();
    }

    public static void Hide()
    {
        lock (Gate)
        {
            _verticalVisible = false;
            _horizontalVisible = false;
        }
        Post();
    }

    private static void Post()
    {
        if (_vertical != 0)
            _ = PInvoke.PostMessage(new HWND(_vertical), WM_APP_REFRESH, default, default);
        if (_horizontal != 0)
            _ = PInvoke.PostMessage(new HWND(_horizontal), WM_APP_REFRESH, default, default);
    }

    private static void EnsureStarted()
    {
        if (_thread is not null)
            return;
        lock (Gate)
        {
            if (_thread is not null)
                return;
            _thread = new Thread(Run) { IsBackground = true, Name = "PrtScr.SnapGuide" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
        Ready.Wait(TimeSpan.FromSeconds(2));
    }

    private static void Run()
    {
        try
        {
            fixed (char* className = ClassName)
            {
                var wndClass = new WNDCLASSW
                {
                    style = (WNDCLASS_STYLES)3, // CS_HREDRAW | CS_VREDRAW
                    lpfnWndProc = &WndProc,
                    hInstance = new HINSTANCE(GetModuleHandleW(null)),
                    hCursor = new HCURSOR(LoadCursorW(0, 32512)),
                    hbrBackground = new HBRUSH(CreateSolidBrush(0x00FFC800)),
                    lpszClassName = new PCWSTR(className),
                };
                _ = RegisterClassW(ref wndClass);
            }

            _vertical = CreateWindowExW(WsExLayered | WsExTopmost | WsExToolWindow | WsExTransparent | WsExNoActivate,
                ClassName, "", WsPopup, 0, 0, 1, 1, 0, 0, 0, 0);
            _horizontal = CreateWindowExW(WsExLayered | WsExTopmost | WsExToolWindow | WsExTransparent | WsExNoActivate,
                ClassName, "", WsPopup, 0, 0, 1, 1, 0, 0, 0, 0);
            if (_vertical == 0 || _horizontal == 0)
                return;
            _ = SetLayeredWindowAttributes(_vertical, 0, 220, 2 /* LWA_ALPHA */);
            _ = SetLayeredWindowAttributes(_horizontal, 0, 220, 2);

            Ready.Set();
            while (PInvoke.GetMessage(out var msg, default, 0, 0).Value > 0)
            {
                _ = PInvoke.TranslateMessage(msg);
                _ = PInvoke.DispatchMessage(msg);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Snap guide: 线程异常 {ex.Message}");
            Ready.Set();
        }
    }

    private static void Apply(nint hwnd, bool visible, int x, int y, int w, int h)
    {
        if (hwnd == 0)
            return;
        if (!visible)
        {
            _ = ShowWindow(hwnd, SwHide);
            return;
        }

        _ = SetWindowPos(hwnd, -1, x, y, w, h, SwpNoActivate | SwpShowWindow);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == WM_APP_REFRESH)
        {
            lock (Gate)
            {
                var h = (nint)hwnd.Value;
                if (h == _vertical)
                    Apply(h, _verticalVisible, _vx, _vTop, 1, Math.Max(1, _vBottom - _vTop));
                else if (h == _horizontal)
                    Apply(h, _horizontalVisible, _hLeft, _hy, Math.Max(1, _hRight - _hLeft), 1);
            }
            return default;
        }

        return new LRESULT(DefWindowProcW((nint)hwnd.Value, msg, (nint)wParam.Value, (nint)lParam.Value));
    }
}
