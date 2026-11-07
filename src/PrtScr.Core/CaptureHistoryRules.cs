namespace PrtScr.Core;

/// <summary>
/// 历史元数据（version 2）的纯映射规则：采集来源 / 引擎字符串与降级原因提取。
/// 只做字符串转换，不读文件、不碰 UI，便于 Core.Tests 覆盖 JSON 契约。
/// </summary>
public static class CaptureHistoryRules
{
    /// <summary>窗口内容（整窗或窗口局部）。</summary>
    public const string SourceWindow = "window";

    /// <summary>常规取景：显示器冻结帧或屏幕区域裁切。</summary>
    public const string SourceNormal = "normal";

    /// <summary>窗口捕获失败后降级为屏幕区域裁切。</summary>
    public const string SourceScreen = "screen";

    public const string EngineWgc = "wgc";
    public const string EnginePrintWindow = "printwindow";
    public const string EngineDxgi = "dxgi";
    public const string EngineBitBlt = "bitblt";
    public const string EngineFallback = "fallback";

    /// <summary>PrintWindow 超时但 WGC 取到窗口内容时落盘的固定标记。</summary>
    public const string PrintWindowTimeoutToken = "printwindow-timeout";

    /// <summary>降级原因落盘上限；超长说明截断，避免元数据膨胀。</summary>
    public const int MaxFallbackReasonLength = 200;

    private const string ScreenFallbackPrefix = "窗口捕获失败，已按常规截图保存（";
    private const string ScreenFallbackSuffix = "）";

    /// <summary>CaptureOrigin → source；None 表示不是采集来源（如贴图裁剪），返回 null。</summary>
    public static string? ToHistorySource(CaptureOrigin origin) => origin switch
    {
        CaptureOrigin.Window => SourceWindow,
        CaptureOrigin.Normal => SourceNormal,
        CaptureOrigin.Screen => SourceScreen,
        _ => null,
    };

    /// <summary>
    /// 读取侧的 source 解析：v2 已落盘的值优先；旧 JSON（version 1 或键缺失）按 mode 推导，
    /// 只推导 window/normal，绝不把旧文件判成降级（screen）。本阶段只落盘，展示留给后续视觉单。
    /// </summary>
    public static string? ResolveSource(HistoryCaptureMode mode, string? storedSource)
    {
        if (!string.IsNullOrEmpty(storedSource))
            return storedSource;

        return mode switch
        {
            HistoryCaptureMode.Window => SourceWindow,
            HistoryCaptureMode.Normal => SourceNormal,
            _ => null,
        };
    }

    /// <summary>CaptureEngine → engine；None/未知返回 null。</summary>
    public static string? ToHistoryEngine(CaptureEngine engine) => engine switch
    {
        CaptureEngine.Wgc => EngineWgc,
        CaptureEngine.PrintWindow => EnginePrintWindow,
        CaptureEngine.Dxgi => EngineDxgi,
        CaptureEngine.BitBlt => EngineBitBlt,
        CaptureEngine.Fallback => EngineFallback,
        _ => null,
    };

    /// <summary>
    /// 提取要落盘的降级原因：PrintWindow 超时优先固定标记；屏幕降级剥掉面向用户的固定外壳，
    /// 只留真实原因；其余返回 null（不写盘）。
    /// </summary>
    public static string? ExtractFallbackReason(string? notice, bool printWindowTimedOut)
    {
        if (printWindowTimedOut)
            return PrintWindowTimeoutToken;

        if (string.IsNullOrWhiteSpace(notice))
            return null;

        var text = notice.Trim();
        if (text.Length >= ScreenFallbackPrefix.Length + ScreenFallbackSuffix.Length
            && text.StartsWith(ScreenFallbackPrefix, StringComparison.Ordinal)
            && text.EndsWith(ScreenFallbackSuffix, StringComparison.Ordinal))
        {
            text = text[ScreenFallbackPrefix.Length..^ScreenFallbackSuffix.Length];
        }

        text = text.Trim();
        if (text.Length == 0)
            return null;

        return text.Length > MaxFallbackReasonLength ? text[..MaxFallbackReasonLength] : text;
    }
}
