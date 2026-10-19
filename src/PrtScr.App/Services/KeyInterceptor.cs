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

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint prev, nint hwnd, uint msg, nint wParam, nint lParam);

    private static readonly Dictionary<nint, KeyInterceptor> Map = new();
    private static readonly object MapLock = new();

    private readonly nint _hwnd;
    private nint _oldProc;
    private bool _disposed;
    private readonly bool _interceptO;

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
