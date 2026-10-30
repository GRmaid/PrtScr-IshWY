namespace PrtScr.Core;

/// <summary>一次取景会话的状态。会话由 CaptureService 发起，取景窗在隐藏/重试/收尾时推进。</summary>
public enum CaptureSessionState
{
    /// <summary>没有活动会话。</summary>
    Idle = 0,

    /// <summary>已发起，正在取冻结帧/枚举窗口。</summary>
    Preparing = 1,

    /// <summary>取景窗可见，等待主人选择目标。</summary>
    Selecting = 2,

    /// <summary>已确认目标，正在等待窗口/屏幕捕获结果。</summary>
    Capturing = 3,

    /// <summary>画面已拿到，正在编码、写剪贴板与历史。</summary>
    Committing = 4,

    /// <summary>成功收尾。</summary>
    Completed = 5,

    /// <summary>用户取消或会话被取代；不写剪贴板/历史，不发失败提示。</summary>
    Canceled = 6,

    /// <summary>失败收尾；是否恢复取景窗见 <see cref="CaptureRules.IsRecoverable"/>。</summary>
    Failed = 7,
}

/// <summary>会话收尾原因；<see cref="CaptureRules.EndReasonText"/> 负责映射为日志里的 reason 文本。</summary>
public enum CaptureEndReason
{
    /// <summary>未指定（仅用于占位）。</summary>
    None = 0,

    /// <summary>用户主动取消（Esc、取景中再按截图热键、飞行期被取消）。</summary>
    Cancel = 1,

    /// <summary>确认截图成功收尾。</summary>
    ConfirmSuccess = 2,

    /// <summary>确认流程结束（成功或失败都由 finally 收尾）。</summary>
    ConfirmEnd = 3,

    /// <summary>取景内识别文字收尾。</summary>
    Ocr = 4,

    /// <summary>取景内另存为收尾。</summary>
    Save = 5,

    /// <summary>启动阶段失败收尾。</summary>
    StartFailed = 6,
}

/// <summary>截图结果的实际来源：决定历史分类，也决定界面要不要提示降级。</summary>
public enum CaptureOrigin
{
    /// <summary>尚未确定来源。</summary>
    None = 0,

    /// <summary>窗口内容（整窗或窗口局部），历史归入窗口类。</summary>
    Window = 1,

    /// <summary>常规取景：显示器冻结帧或屏幕区域裁切，历史归入常规类。</summary>
    Normal = 2,

    /// <summary>窗口捕获失败后降级为屏幕区域裁切；历史归入常规类，并需要提示降级原因。</summary>
    Screen = 3,
}

/// <summary>真正产出画面的采集引擎；仅用于诊断与日志，不参与历史分类。</summary>
public enum CaptureEngine
{
    /// <summary>未确定。</summary>
    None = 0,

    /// <summary>Windows.Graphics.Capture 窗口/显示器捕获。</summary>
    Wgc = 1,

    /// <summary>PrintWindow 窗口内容抓取。</summary>
    PrintWindow = 2,

    /// <summary>DXGI Desktop Duplication 屏幕捕获。</summary>
    Dxgi = 3,

    /// <summary>GDI BitBlt 屏幕捕获。</summary>
    BitBlt = 4,

    /// <summary>采集链兜底（多层降级后的最终路径），具体引擎无法单独归因时使用。</summary>
    Fallback = 5,
}

/// <summary>失败分类。取消、忙碌、超时与真正的失败分开，避免再被静默降级。</summary>
public enum CaptureFailure
{
    /// <summary>没有失败。</summary>
    None = 0,

    /// <summary>目标窗口/区域无效。</summary>
    InvalidTarget = 1,

    /// <summary>同一窗口上一张截图仍在处理中；可重试，不得降级。</summary>
    Busy = 2,

    /// <summary>捕获超时（含 PrintWindow 超时后 WGC 也失败）；可重试。</summary>
    Timeout = 3,

    /// <summary>取消（用户操作或 CancellationToken），不算失败。</summary>
    Canceled = 4,

    /// <summary>PrintWindow 不可用（返回假或纯黑），允许降级到屏幕裁切。</summary>
    PrintWindowUnavailable = 5,

    /// <summary>WGC 无法为窗口创建捕获项。</summary>
    WgcItemCreationFailed = 6,

    /// <summary>WGC/D3D11 设备不可用。</summary>
    WgcDeviceUnavailable = 7,

    /// <summary>WGC 在时限内没有拿到首帧。</summary>
    WgcNoFrame = 8,

    /// <summary>WGC 捕获/帧拷贝失败。</summary>
    WgcCaptureFailed = 9,

    /// <summary>显示器常驻池不可用。</summary>
    MonitorUnavailable = 10,

    /// <summary>通用不可用，尚未细分到具体引擎。</summary>
    Unavailable = 11,

    /// <summary>PNG 编码失败。</summary>
    EncodeFailed = 12,

    /// <summary>剪贴板写入失败（图片与历史可能已成功）。</summary>
    ClipboardFailed = 13,

    /// <summary>历史写入失败（剪贴板可能已成功）。</summary>
    HistoryFailed = 14,

    /// <summary>未归类的内部错误。</summary>
    Internal = 15,
}

/// <summary>Terminate（唯一收尾漏斗）的判定结果：允许、重复（幂等忽略）、非法。</summary>
public enum CaptureTerminateDecision
{
    /// <summary>正常收尾。</summary>
    Allowed = 0,

    /// <summary>已是终态，重复收尾只记日志、不产生副作用（幂等）。</summary>
    Duplicate = 1,

    /// <summary>Idle 且原因不是 StartFailed，属于非法收尾。</summary>
    Invalid = 2,
}

/// <summary>取景结果的纯规则：状态迁移、收尾原因、来源到历史分类、提示文案与可恢复判定。</summary>
/// <remarks>不依赖 System.Drawing / WinUI，可在 Core.Tests 直接覆盖。</remarks>
public static partial class CaptureRules
{
    /// <summary>终态没有出边。</summary>
    public static bool IsTerminal(CaptureSessionState state)
        => state is CaptureSessionState.Completed or CaptureSessionState.Canceled or CaptureSessionState.Failed;

    /// <summary>是否仍在活动（非 Idle 且非终态）。</summary>
    public static bool IsActive(CaptureSessionState state)
        => state is CaptureSessionState.Preparing
            or CaptureSessionState.Selecting
            or CaptureSessionState.Capturing
            or CaptureSessionState.Committing;

    /// <summary>
    /// Terminate 的唯一判定：终态重复收尾返回 Duplicate（幂等，只记 finish ignored）；
    /// Idle 只允许 StartFailed，其余原因属于 invalid transition。
    /// </summary>
    public static CaptureTerminateDecision TerminateDecision(CaptureSessionState state, CaptureEndReason reason)
    {
        if (IsTerminal(state))
            return CaptureTerminateDecision.Duplicate;

        if (state == CaptureSessionState.Idle)
            return reason == CaptureEndReason.StartFailed
                ? CaptureTerminateDecision.Allowed
                : CaptureTerminateDecision.Invalid;

        return CaptureTerminateDecision.Allowed;
    }

    /// <summary>会话状态机是否允许从 <paramref name="from"/> 迁移到 <paramref name="to"/>。</summary>
    public static bool CanTransition(CaptureSessionState from, CaptureSessionState to) => (from, to) switch
    {
        (CaptureSessionState.Idle, CaptureSessionState.Preparing) => true,
        (CaptureSessionState.Preparing, CaptureSessionState.Selecting) => true,
        (CaptureSessionState.Preparing, CaptureSessionState.Canceled) => true,
        (CaptureSessionState.Preparing, CaptureSessionState.Failed) => true,
        (CaptureSessionState.Selecting, CaptureSessionState.Capturing) => true,
        (CaptureSessionState.Selecting, CaptureSessionState.Canceled) => true,
        (CaptureSessionState.Capturing, CaptureSessionState.Committing) => true,
        (CaptureSessionState.Capturing, CaptureSessionState.Selecting) => true,
        (CaptureSessionState.Capturing, CaptureSessionState.Canceled) => true,
        (CaptureSessionState.Capturing, CaptureSessionState.Failed) => true,
        (CaptureSessionState.Committing, CaptureSessionState.Completed) => true,
        (CaptureSessionState.Committing, CaptureSessionState.Selecting) => true,
        (CaptureSessionState.Committing, CaptureSessionState.Failed) => true,
        _ => false,
    };

    /// <summary>收尾原因到日志 reason 文本的映射；None 返回空串。</summary>
    public static string EndReasonText(CaptureEndReason reason) => reason switch
    {
        CaptureEndReason.Cancel => "cancel",
        CaptureEndReason.ConfirmSuccess => "confirm-success",
        CaptureEndReason.ConfirmEnd => "confirm-end",
        CaptureEndReason.Ocr => "ocr",
        CaptureEndReason.Save => "save",
        CaptureEndReason.StartFailed => "start-failed",
        _ => "",
    };

    /// <summary>来源到历史分类：只有窗口来源进窗口类，其余都按常规记录。</summary>
    public static HistoryCaptureMode ToHistoryMode(CaptureOrigin origin)
        => origin == CaptureOrigin.Window ? HistoryCaptureMode.Window : HistoryCaptureMode.Normal;

    /// <summary>是否属于「窗口捕获失败后降级为屏幕裁切」；这类结果必须带降级提示。</summary>
    public static bool IsDowngrade(CaptureOrigin origin) => origin == CaptureOrigin.Screen;

    /// <summary>
    /// 屏幕裁切来源判定：尝试过窗口捕获才记为 Screen（降级），没有窗口目标则仍是 Normal。
    /// </summary>
    public static CaptureOrigin ResolveScreenOrigin(bool windowCaptureAttempted)
        => windowCaptureAttempted ? CaptureOrigin.Screen : CaptureOrigin.Normal;

    /// <summary>取消的唯一判定：只有 Canceled 算取消，超时/忙碌/写入失败都不算。</summary>
    public static bool CountsAsCancel(CaptureFailure failure) => failure == CaptureFailure.Canceled;

    /// <summary>失败后是否应恢复取景窗让用户重试（仅忙碌与超时）。</summary>
    public static bool IsRecoverable(CaptureFailure failure)
        => failure is CaptureFailure.Busy or CaptureFailure.Timeout;

    /// <summary>
    /// 失败对应的可见提示；取消与成功返回 null（不打扰用户）。
    /// <paramref name="hresult"/> 预留给诊断文案，<paramref name="detail"/> 用于内部错误的细节。
    /// </summary>
    public static string? NoticeFor(CaptureFailure failure, int hresult = 0, string? detail = null) => failure switch
    {
        CaptureFailure.None => null,
        CaptureFailure.Canceled => null,
        CaptureFailure.Busy => "该窗口上一张截图还在处理中，请稍后重试",
        CaptureFailure.Timeout => "窗口响应超时，本次未截图；请稍后重试",
        CaptureFailure.InvalidTarget => "目标窗口已失效，请重新选择",
        CaptureFailure.PrintWindowUnavailable => "窗口内容抓取不可用，已按常规截图",
        CaptureFailure.WgcItemCreationFailed
            or CaptureFailure.WgcDeviceUnavailable
            or CaptureFailure.WgcNoFrame
            or CaptureFailure.WgcCaptureFailed => "窗口内容抓取失败，已按常规截图",
        CaptureFailure.MonitorUnavailable or CaptureFailure.Unavailable => "采集引擎暂时不可用，请稍后重试",
        CaptureFailure.EncodeFailed => "图片编码失败，请重试",
        CaptureFailure.ClipboardFailed => "复制失败：剪贴板被占用，请稍后重试",
        CaptureFailure.HistoryFailed => "历史保存失败，请检查磁盘空间",
        CaptureFailure.Internal => string.IsNullOrWhiteSpace(detail) ? "截图失败，请重试" : $"截图失败：{detail}",
        _ => "截图失败，请重试",
    };
}
