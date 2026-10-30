namespace PrtScr.Core;

/// <summary>设计稿结果矩阵的 12 个场景；每个场景一条测试，避免矩阵只活在文档里。</summary>
public enum CaptureScenario
{
    /// <summary>常规取景：屏幕裁切。</summary>
    ScreenSelection = 0,

    /// <summary>窗口整窗捕获成功。</summary>
    WindowWhole = 1,

    /// <summary>窗口局部捕获成功。</summary>
    WindowRegion = 2,

    /// <summary>PrintWindow 成功。</summary>
    PrintWindow = 3,

    /// <summary>PrintWindow 超时，WGC 成功兜底。</summary>
    PrintWindowTimeoutWgc = 4,

    /// <summary>PrintWindow 超时，WGC 也失败。</summary>
    PrintWindowTimeoutFailed = 5,

    /// <summary>同一窗口上一张截图仍在处理中（Busy）。</summary>
    Busy = 6,

    /// <summary>用户取消。</summary>
    UserCancel = 7,

    /// <summary>PNG 编码失败。</summary>
    EncodeFailed = 8,

    /// <summary>剪贴板写入失败（历史已成功）。</summary>
    ClipboardFailed = 9,

    /// <summary>历史写入失败（剪贴板已成功）。</summary>
    HistoryFailed = 10,

    /// <summary>取景内识别文字或另存为出口。</summary>
    OcrOrSave = 11,
}

/// <summary>
/// 一个矩阵场景的期望：终态、失败分类、是否恢复取景窗、是否写历史/剪贴板、是否需要提示、是否发生 PrintWindow 超时。
/// <see cref="WritesHistory"/> 与 <see cref="WritesClipboard"/> 为 null 表示「由具体出口决定」（OCR 不写、另存写）。
/// </summary>
public readonly record struct CaptureExpectation(
    CaptureSessionState State,
    CaptureFailure Failure,
    bool RestoresOverlay,
    bool? WritesHistory,
    bool? WritesClipboard,
    bool ExpectsNotice,
    bool PrintWindowTimedOut = false);

/// <summary>结果矩阵：场景到期望的纯映射，测试与后续接线共用同一份事实。</summary>
public static partial class CaptureRules
{
    /// <summary>返回设计稿第 4.2 节结果矩阵中该场景的期望。</summary>
    public static CaptureExpectation ExpectationFor(CaptureScenario scenario) => scenario switch
    {
        CaptureScenario.ScreenSelection
            => new(CaptureSessionState.Completed, CaptureFailure.None, false, true, true, false),
        CaptureScenario.WindowWhole
            => new(CaptureSessionState.Completed, CaptureFailure.None, false, true, true, false),
        CaptureScenario.WindowRegion
            => new(CaptureSessionState.Completed, CaptureFailure.None, false, true, true, false),
        CaptureScenario.PrintWindow
            => new(CaptureSessionState.Completed, CaptureFailure.None, false, true, true, false),
        CaptureScenario.PrintWindowTimeoutWgc
            => new(CaptureSessionState.Completed, CaptureFailure.None, false, true, true, true, PrintWindowTimedOut: true),
        CaptureScenario.PrintWindowTimeoutFailed
            => new(CaptureSessionState.Failed, CaptureFailure.Timeout, true, false, false, true, PrintWindowTimedOut: true),
        CaptureScenario.Busy
            => new(CaptureSessionState.Selecting, CaptureFailure.Busy, true, false, false, true),
        CaptureScenario.UserCancel
            => new(CaptureSessionState.Canceled, CaptureFailure.Canceled, false, false, false, false),
        CaptureScenario.EncodeFailed
            => new(CaptureSessionState.Failed, CaptureFailure.EncodeFailed, true, false, false, true),
        CaptureScenario.ClipboardFailed
            => new(CaptureSessionState.Completed, CaptureFailure.ClipboardFailed, false, true, false, true),
        CaptureScenario.HistoryFailed
            => new(CaptureSessionState.Completed, CaptureFailure.HistoryFailed, false, false, true, true),
        CaptureScenario.OcrOrSave
            => new(CaptureSessionState.Completed, CaptureFailure.None, false, null, null, false),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "未知的截图结果场景"),
    };
}
