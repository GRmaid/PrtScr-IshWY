namespace PrtScr.Interop;

/// <summary>屏幕上的矩形（物理像素）。</summary>
public readonly record struct WindowRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Intersects(WindowRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// 一个顶层窗口的快照。悬浮元素屏蔽（M6）依据其中的样式位与进程名打分。
/// </summary>
public sealed record WindowInfo(
    nint Handle,
    string Title,
    string ClassName,
    string ProcessName,
    int ProcessId,
    WindowRect FrameBounds,
    bool IsToolWindow,
    bool IsTopmost,
    bool IsLayered,
    bool IsClickThrough,
    bool IsCloaked,
    bool IsChild = false)
{
    /// <summary>
    /// 悬浮类窗口（桌面歌词、桌宠、会议提示等）：置顶且带工具窗 / 点击穿透 / 分层特征。
    /// “纯净模式”吸附时会跳过它们；Tab 可切换识别。
    /// </summary>
    public bool IsOverlayClass =>
        IsTopmost && (IsToolWindow || IsClickThrough || IsLayered);
}
