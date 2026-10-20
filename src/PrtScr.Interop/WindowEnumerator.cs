using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.Interop;

/// <summary>
/// 窗口枚举：Z 序遍历 + DWM 精确矩形 + 样式/进程信息。
/// 顶层窗口之外，还会把可见的 #32770 子对话框（例如 Electron 应用弹出的原生错误框）
/// 插到父窗口前面，保证取景器能单独命中它。
/// </summary>
public static class WindowEnumerator
{
    [ThreadStatic]
    private static List<HWND>? _collecting;

    [ThreadStatic]
    private static List<HWND>? _collectingChildren;

    public static IReadOnlyList<WindowInfo> EnumerateVisibleWindows()
    {
        var topLevel = CollectTopLevel();
        var result = new List<WindowInfo>(topLevel.Count);
        var seen = new HashSet<nint>();

        foreach (var hwnd in topLevel)
        {
            if (!PInvoke.IsWindowVisible(hwnd) || !seen.Add((nint)hwnd))
                continue;

            var parentInfo = TryBuild(hwnd, isChild: false);
            var childInfos = new List<WindowInfo>();

            foreach (var child in CollectChildren(hwnd))
            {
                if (!PInvoke.IsWindowVisible(child) || !seen.Add((nint)child))
                    continue;

                if (TryBuild(child, isChild: true) is { } childInfo && IsDialogLike(childInfo))
                    childInfos.Add(childInfo);
            }

            var insertAt = result.Count;
            if (parentInfo is not null)
            {
                result.Add(parentInfo);
                insertAt = result.Count - 1;   // 子对话框排在父窗口之前 = 命中优先级更高
            }

            if (childInfos.Count > 0)
                result.InsertRange(insertAt, childInfos);
        }

        return result;
    }

    private static List<HWND> CollectTopLevel()
    {
        var windows = new List<HWND>();
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

        return windows;
    }

    private static List<HWND> CollectChildren(HWND parent)
    {
        var children = new List<HWND>();
        _collectingChildren = children;
        try
        {
            unsafe
            {
                _ = PInvoke.EnumChildWindows(parent, &EnumChildProc, default);
            }
        }
        finally
        {
            _collectingChildren = null;
        }

        return children;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumWindowsProc(HWND hwnd, LPARAM lParam)
    {
        _collecting?.Add(hwnd);
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumChildProc(HWND hwnd, LPARAM lParam)
    {
        _collectingChildren?.Add(hwnd);
        return true;
    }

    private static WindowInfo? TryBuild(HWND hwnd, bool isChild)
    {
        try
        {
            var info = BuildInfo(hwnd, isChild);
            return info.FrameBounds.Width > 0 && info.FrameBounds.Height > 0 ? info : null;
        }
        catch
        {
            // 单个窗口信息读取失败不影响整体枚举
            return null;
        }
    }

    /// <summary>只把真正的对话框子窗加进来，避免按钮/编辑框/渲染子窗污染候选。</summary>
    private static bool IsDialogLike(WindowInfo info) =>
        info.ClassName == "#32770" &&
        info.FrameBounds.Width >= 120 &&
        info.FrameBounds.Height >= 60;

    private static WindowInfo BuildInfo(HWND hwnd, bool isChild)
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

            // DWM 对某些窗口（尤其是子对话框、刚创建的弹窗）会失败或返回空矩形；
            // 这时退回 GetWindowRect，否则该窗口会被 Prepare 的 Width > 0 过滤掉。
            if (frameRect.right <= frameRect.left || frameRect.bottom <= frameRect.top)
                _ = PInvoke.GetWindowRect(hwnd, &frameRect);
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
            IsCloaked: cloaked != 0,
            IsChild: isChild);
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
