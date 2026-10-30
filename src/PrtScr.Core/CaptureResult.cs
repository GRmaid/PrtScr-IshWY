namespace PrtScr.Core;

/// <summary>
/// 一次取景的结果元数据：会话身份 + 来源 + 引擎 + 失败分类 + 诊断细节。
/// </summary>
/// <remarks>
/// 第一阶段只承载元数据，不持 Bitmap（Core 不引用 System.Drawing）；
/// 带位图的所有权类型在 P-14b 接引擎时按设计稿放到 PrtScr.Capture。
/// </remarks>
public sealed record CaptureResult(
    Guid SessionId,
    CaptureOrigin Origin,
    CaptureEngine Engine,
    CaptureFailure Failure,
    bool PrintWindowTimedOut = false,
    int HResult = 0,
    string? Detail = null)
{
    /// <summary>是否成功拿到画面。</summary>
    public bool IsSuccess => Failure == CaptureFailure.None;

    /// <summary>是否属于用户取消（不写剪贴板/历史，不发失败提示）。</summary>
    public bool IsCanceled => Failure == CaptureFailure.Canceled;

    /// <summary>失败后是否应恢复取景窗（仅 Busy/Timeout）。</summary>
    public bool IsRecoverable => CaptureRules.IsRecoverable(Failure);

    /// <summary>该来源对应的历史分类。</summary>
    public HistoryCaptureMode HistoryMode => CaptureRules.ToHistoryMode(Origin);

    /// <summary>界面提示文案；成功与取消为 null。</summary>
    public string? Notice => CaptureRules.NoticeFor(Failure, HResult, Detail);

    /// <summary>成功结果。</summary>
    public static CaptureResult Success(
        Guid sessionId,
        CaptureOrigin origin,
        CaptureEngine engine,
        bool printWindowTimedOut = false,
        string? detail = null)
        => new(sessionId, origin, engine, CaptureFailure.None, printWindowTimedOut, 0, detail);

    /// <summary>失败结果；<paramref name="failure"/> 不能是 None 或 Canceled（取消用 <see cref="Canceled"/> 工厂）。</summary>
    public static CaptureResult Failed(
        Guid sessionId,
        CaptureFailure failure,
        CaptureOrigin origin,
        CaptureEngine engine,
        int hresult = 0,
        string? detail = null,
        bool printWindowTimedOut = false)
    {
        if (failure is CaptureFailure.None or CaptureFailure.Canceled)
            throw new ArgumentOutOfRangeException(nameof(failure), failure, "失败结果必须带非 None/Canceled 的失败分类");

        return new(sessionId, origin, engine, failure, printWindowTimedOut, hresult, detail);
    }

    /// <summary>取消结果。</summary>
    public static CaptureResult Canceled(Guid sessionId, CaptureOrigin origin, CaptureEngine engine, string? detail = null)
        => new(sessionId, origin, engine, CaptureFailure.Canceled, false, 0, detail);
}
