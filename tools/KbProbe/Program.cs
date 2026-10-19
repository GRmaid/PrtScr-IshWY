using System.Runtime.InteropServices;

internal static unsafe class Program
{
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, nint lpfn, nint hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG msg, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandleW(nint name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private static nint _hook;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && lParam != 0)
        {
            var data = (KbdLlHookStruct*)lParam;
            Console.WriteLine($"evt msg=0x{(uint)wParam:X} vk=0x{data->vkCode:X} scan=0x{data->scanCode:X} flags=0x{data->flags:X} asyncShift=0x{GetAsyncKeyState(0x10):X}");
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
    }

    private static void Main()
    {
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, (nint)(delegate* unmanaged[Stdcall]<int, nint, nint, nint>)&HookProc, GetModuleHandleW(0), 0);
        Console.WriteLine($"hook=0x{_hook:X} err={Marshal.GetLastWin32Error()}");
        if (_hook == 0) return;

        // 后台线程注入：Shift 按下 → X 按下/抬起 → Shift 抬起
        var t = new Thread(() =>
        {
            Thread.Sleep(500);
            Console.WriteLine("injecting...");
            keybd_event(0x10, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            keybd_event(0x58, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            keybd_event(0x58, 0, 2, UIntPtr.Zero);
            keybd_event(0x10, 0, 2, UIntPtr.Zero);
            Thread.Sleep(1500);
            PostQuitMessage(0);
        });
        t.IsBackground = true;
        t.Start();

        while (GetMessage(out var msg, 0, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        Console.WriteLine("exit loop");
    }

    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG msg);
}
