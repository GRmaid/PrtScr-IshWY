using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.Interop;

/// <summary>
/// 顶层窗口枚举：Z 序遍历 + DWM 精确矩形 + 样式/进程信息。
/// </summary>
public static class WindowEnumerator
{
    [ThreadStatic]
    private static List<HWND>? _collecting;

    public static IReadOnlyList<WindowInfo> EnumerateVisibleWindows()
    {
        var windows = new List<HWND>();
        var result = new List<WindowInfo>();

        _collecting = windows;
        try
        {
            unsafe
            {
                _ = PInvoke.EnumWindows(&EnumWindowsProc, default);
            }
        }
        finally
        {
            _collecting = null;
        }

        foreach (var hwnd in windows)
        {
            if (!PInvoke.IsWindowVisible(hwnd))
                continue;

            try
            {
                if (BuildInfo(hwnd) is { } info)
                    result.Add(info);
            }
            catch
            {
                // 单个窗口信息读取失败不影响整体枚举
            }
        }

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumWindowsProc(HWND hwnd, LPARAM lParam)
    {
        _collecting?.Add(hwnd);
        return true;
    }

    private static WindowInfo? BuildInfo(HWND hwnd)
    {
        var titleLength = PInvoke.GetWindowTextLength(hwnd);

        string title;
        string className;
        uint processId;
        RECT frameRect = default;
        uint cloaked = 0;

        unsafe
        {
            char* titleBuffer = stackalloc char[titleLength + 1];
            _ = PInvoke.GetWindowText(hwnd, new PWSTR(titleBuffer), titleLength + 1);
            title = new string(titleBuffer);

            char* classBuffer = stackalloc char[256];
            _ = PInvoke.GetClassName(hwnd, new PWSTR(classBuffer), 256);
            className = new string(classBuffer);

            _ = PInvoke.GetWindowThreadProcessId(hwnd, &processId);

            _ = PInvoke.DwmGetWindowAttribute(
                hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frameRect, (uint)sizeof(RECT));
            _ = PInvoke.DwmGetWindowAttribute(
                hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, 4u);
        }

        var exStyle = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        return new WindowInfo(
            Handle: hwnd,
            Title: title,
            ClassName: className,
            ProcessName: QueryProcessName(processId),
            ProcessId: (int)processId,
            FrameBounds: new WindowRect(
                frameRect.left, frameRect.top, frameRect.right - frameRect.left, frameRect.bottom - frameRect.top),
            IsToolWindow: (exStyle & (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0,
            IsTopmost: (exStyle & (uint)WINDOW_EX_STYLE.WS_EX_TOPMOST) != 0,
            IsLayered: (exStyle & (uint)WINDOW_EX_STYLE.WS_EX_LAYERED) != 0,
            IsClickThrough: (exStyle & (uint)WINDOW_EX_STYLE.WS_EX_TRANSPARENT) != 0,
            IsCloaked: cloaked != 0);
    }

    private static string QueryProcessName(uint processId)
    {
        var process = PInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsNull)
            return "";

        try
        {
            unsafe
            {
                char* buffer = stackalloc char[1024];
                uint size = 1024;
                if (PInvoke.QueryFullProcessImageName(
                        process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, new PWSTR(buffer), &size))
                {
                    return Path.GetFileName(new string(buffer, 0, (int)size));
                }
            }

            return "";
        }
        finally
        {
            _ = PInvoke.CloseHandle(process);
        }
    }
}
