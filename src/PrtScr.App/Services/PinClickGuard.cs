namespace PrtScr.App.Services;

/// <summary>
/// 贴图右键菜单打开期间，屏蔽贴图对鼠标按下的响应。
/// 原因：WinUI MenuFlyout 点击外部关闭时，这一下点击会继续落到下面的贴图窗口，
/// 触发左右半屏切换。菜单开启期间由低级鼠标钩子提前置位，贴图的 WndProc 看到就跳过。
/// </summary>
internal static class PinClickGuard
{
    private static long _until;

    public static void Suppress(int milliseconds = 450)
    {
        var until = Environment.TickCount64 + Math.Max(50, milliseconds);
        var current = Volatile.Read(ref _until);
        if (until > current)
            Interlocked.Exchange(ref _until, until);
    }

    public static bool IsSuppressed => Environment.TickCount64 < Volatile.Read(ref _until);
}
