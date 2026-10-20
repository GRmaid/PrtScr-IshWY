using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PrtScr.App.Services;

/// <summary>
/// 在 Win32 消息层拦截 Enter / Esc / Tab，绕开 XAML 焦点体系——
/// XAML 的 KeyboardAccelerator 依赖元素获得焦点，取景窗/编辑器偶发“按键没反应”即源于此。
/// </summary>
internal sealed unsafe class KeyInterceptor : IDisposable
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCHITTEST = 0x0084;
    private const nint HTCLIENT = 1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint prev, nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly Dictionary<nint, KeyInterceptor> Map = new();
    private static readonly object MapLock = new();

    private readonly nint _hwnd;
    private nint _oldProc;
    private bool _disposed;
    private readonly bool _interceptO;
    private bool _borderlessClient;

    public event Action<int>? KeyDown;

    /// <summary>窗口线程上收到的 WM_HOTKEY（参数为热键 id）。</summary>
    public event Action<int>? HotKey;

    public KeyInterceptor(nint hwnd, bool interceptO = false)
    {
        _hwnd = hwnd;
        _interceptO = interceptO;
        lock (MapLock)
            Map[hwnd] = this;

        _oldProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProc);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        KeyInterceptor? self;
        lock (MapLock)
            Map.TryGetValue(hwnd, out self);

        // 无边框全屏窗：WinUI 仍会留 1px 非客户区，鼠标贴边按下会收到 HTBORDER
        // 而进不了 XAML（框选从屏幕边缘起手失效）。把客户区扩到整窗并强制命中客户区。
        if (self is not null && self._borderlessClient)
        {
            if (msg == WM_NCCALCSIZE && wParam != 0)
                return 0;
            if (msg == WM_NCHITTEST)
                return HTCLIENT;
        }

        if (self is not null && msg == WM_HOTKEY)
        {
            try
            {
                self.HotKey?.Invoke((int)wParam);
            }
            catch
            {
            }
            return 0;
        }

        if (self is not null && msg == WM_KEYDOWN)
        {
            var vk = (int)wParam;
            if (vk is 0x0D or 0x1B or 0x09 || (self._interceptO && vk == 0x4F))   // Enter / Esc / Tab / O(按需)
            {
                try
                {
                    self.KeyDown?.Invoke(vk);
                }
                catch
                {
                    // 拦截层异常不能影响窗口消息循环
                }
                return 0;
            }
        }

        return CallWindowProc(self?._oldProc ?? 0, hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 把窗口客户区扩到整窗，并让所有位置都命中 HTCLIENT。
    /// 取景窗靠它保证屏幕最外 1px 的按下也能进入 XAML 框选。
    /// </summary>
    public void MakeClientAreaFullWindow()
    {
        if (_disposed || _hwnd == 0)
            return;

        _borderlessClient = true;
        _ = SetWindowPos(
            _hwnd, 0, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (MapLock)
            Map.Remove(_hwnd);

        if (_hwnd != 0 && _oldProc != 0)
            _ = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldProc);
        _oldProc = 0;
    }
}
