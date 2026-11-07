namespace PrtScr.Core;

/// <summary>
/// 写历史时要落盘的采集来源信息（version 2 新字段）；字段为 null 表示不适用
/// （贴图裁剪无采集来源、全屏快照没有引擎）。
/// </summary>
public sealed record HistoryCaptureMeta(
    string? Source,
    string? Engine,
    string? FallbackReason,
    bool PrintWindowTimedOut,
    string? SessionId)
{
    /// <summary>一键全屏快照：只知是常规截图，没有引擎信息（本阶段不扩 QuickCaptureResult）。</summary>
    public static HistoryCaptureMeta ScreenQuick { get; } = new(
        CaptureHistoryRules.SourceNormal, null, null, false, null);

    /// <summary>用提交点手里的采集结果构造；sessionId 本阶段不入库，恒 null。</summary>
    public static HistoryCaptureMeta FromCapture(
        CaptureOrigin origin,
        CaptureEngine engine,
        bool printWindowTimedOut,
        string? notice)
    {
        return new HistoryCaptureMeta(
            CaptureHistoryRules.ToHistorySource(origin),
            CaptureHistoryRules.ToHistoryEngine(engine),
            CaptureHistoryRules.ExtractFallbackReason(notice, printWindowTimedOut),
            printWindowTimedOut,
            null);
    }
}
