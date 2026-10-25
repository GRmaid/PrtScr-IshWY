using System.IO;

namespace PrtScr.Core;

/// <summary>截图历史的分组模式：窗口截图按进程再分组，常规截图单独一组，早期无元数据文件归入未分类。</summary>
public enum HistoryCaptureMode
{
    Window,
    Normal,
    Legacy,
    PinCrop,
}

/// <summary>一条本机截图历史。</summary>
public sealed record HistoryItem(
    string FilePath,
    DateTime CreatedAt,
    long SizeBytes,
    int Width,
    int Height,
    HistoryCaptureMode Mode,
    string ProcessName,
    string WindowTitle)
{
    public string FileName => Path.GetFileName(FilePath);

    public string DisplayProcess => string.IsNullOrWhiteSpace(ProcessName) ? "未知进程" : ProcessName;

    public string Source => Mode switch
    {
        HistoryCaptureMode.Window => $"窗口 · {DisplayProcess}",
        HistoryCaptureMode.Normal => "常规截图",
        HistoryCaptureMode.PinCrop => "贴图裁剪",
        _ => "早期截图",
    };

    public string CategoryKey => Mode == HistoryCaptureMode.Window
        ? $"window:{DisplayProcess}"
        : $"mode:{Mode}";
}
