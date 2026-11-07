namespace PrtScr.Core.Tests;

/// <summary>历史元数据来源/引擎映射与降级原因提取（version 2 契约）。</summary>
public sealed class CaptureHistoryRulesTests
{
    [Theory]
    [InlineData(CaptureOrigin.Window, CaptureHistoryRules.SourceWindow)]
    [InlineData(CaptureOrigin.Normal, CaptureHistoryRules.SourceNormal)]
    [InlineData(CaptureOrigin.Screen, CaptureHistoryRules.SourceScreen)]
    [InlineData(CaptureOrigin.None, null)]
    public void ToHistorySource_MapsEachOrigin(CaptureOrigin origin, string? expected)
        => Assert.Equal(expected, CaptureHistoryRules.ToHistorySource(origin));

    [Theory]
    [InlineData(CaptureEngine.Wgc, CaptureHistoryRules.EngineWgc)]
    [InlineData(CaptureEngine.PrintWindow, CaptureHistoryRules.EnginePrintWindow)]
    [InlineData(CaptureEngine.Dxgi, CaptureHistoryRules.EngineDxgi)]
    [InlineData(CaptureEngine.BitBlt, CaptureHistoryRules.EngineBitBlt)]
    [InlineData(CaptureEngine.Fallback, CaptureHistoryRules.EngineFallback)]
    [InlineData(CaptureEngine.None, null)]
    public void ToHistoryEngine_MapsEachEngine(CaptureEngine engine, string? expected)
        => Assert.Equal(expected, CaptureHistoryRules.ToHistoryEngine(engine));

    [Theory]
    [InlineData(HistoryCaptureMode.Window, null, CaptureHistoryRules.SourceWindow)]
    [InlineData(HistoryCaptureMode.Normal, null, CaptureHistoryRules.SourceNormal)]
    [InlineData(HistoryCaptureMode.PinCrop, null, null)]
    [InlineData(HistoryCaptureMode.Legacy, null, null)]
    [InlineData(HistoryCaptureMode.Window, CaptureHistoryRules.SourceScreen, CaptureHistoryRules.SourceScreen)]
    [InlineData(HistoryCaptureMode.Normal, CaptureHistoryRules.SourceWindow, CaptureHistoryRules.SourceWindow)]
    public void ResolveSource_StoredValueWinsOtherwiseInfersFromMode(
        HistoryCaptureMode mode, string? storedSource, string? expected)
        => Assert.Equal(expected, CaptureHistoryRules.ResolveSource(mode, storedSource));

    [Fact]
    public void ExtractFallbackReason_NullNotice_ReturnsNull()
        => Assert.Null(CaptureHistoryRules.ExtractFallbackReason(null, printWindowTimedOut: false));

    [Fact]
    public void ExtractFallbackReason_BlankNotice_ReturnsNull()
        => Assert.Null(CaptureHistoryRules.ExtractFallbackReason("   ", printWindowTimedOut: false));

    [Fact]
    public void ExtractFallbackReason_PlainNotice_IsTrimmedAndKept()
        => Assert.Equal("PrintWindow 返回空白图",
            CaptureHistoryRules.ExtractFallbackReason("  PrintWindow 返回空白图  ", printWindowTimedOut: false));

    [Fact]
    public void ExtractFallbackReason_ScreenDowngrade_StripsUserFacingWrapper()
        => Assert.Equal("PrintWindow 超时",
            CaptureHistoryRules.ExtractFallbackReason(
                "窗口捕获失败，已按常规截图保存（PrintWindow 超时）", printWindowTimedOut: false));

    [Fact]
    public void ExtractFallbackReason_EmptyWrapperReason_ReturnsNull()
        => Assert.Null(CaptureHistoryRules.ExtractFallbackReason(
            "窗口捕获失败，已按常规截图保存（）", printWindowTimedOut: false));

    [Fact]
    public void ExtractFallbackReason_LongReason_TruncatesToLimit()
    {
        var longReason = new string((char)120, CaptureHistoryRules.MaxFallbackReasonLength + 50);
        var reason = CaptureHistoryRules.ExtractFallbackReason(longReason, printWindowTimedOut: false);
        Assert.NotNull(reason);
        Assert.Equal(CaptureHistoryRules.MaxFallbackReasonLength, reason!.Length);
    }

    [Fact]
    public void ExtractFallbackReason_PrintWindowTimedOut_WinsOverNotice()
        => Assert.Equal(CaptureHistoryRules.PrintWindowTimeoutToken,
            CaptureHistoryRules.ExtractFallbackReason("窗口响应较慢，已改用 WGC 抓取窗口内容", printWindowTimedOut: true));

    [Fact]
    public void ExtractFallbackReason_TimedOutWithoutNotice_StillReturnsToken()
        => Assert.Equal(CaptureHistoryRules.PrintWindowTimeoutToken,
            CaptureHistoryRules.ExtractFallbackReason(null, printWindowTimedOut: true));

    [Fact]
    public void FromCapture_WindowViaPrintWindow_KeepsWindowSourceAndEngine()
    {
        var meta = HistoryCaptureMeta.FromCapture(CaptureOrigin.Window, CaptureEngine.PrintWindow, printWindowTimedOut: false, notice: null);
        Assert.Equal(CaptureHistoryRules.SourceWindow, meta.Source);
        Assert.Equal(CaptureHistoryRules.EnginePrintWindow, meta.Engine);
        Assert.Null(meta.FallbackReason);
        Assert.False(meta.PrintWindowTimedOut);
        Assert.Null(meta.SessionId);
    }

    [Fact]
    public void FromCapture_WindowTimedOut_UsesFixedToken()
    {
        var meta = HistoryCaptureMeta.FromCapture(
            CaptureOrigin.Window, CaptureEngine.Wgc, printWindowTimedOut: true,
            notice: "窗口响应较慢，已改用 WGC 抓取窗口内容");
        Assert.Equal(CaptureHistoryRules.SourceWindow, meta.Source);
        Assert.Equal(CaptureHistoryRules.PrintWindowTimeoutToken, meta.FallbackReason);
        Assert.True(meta.PrintWindowTimedOut);
    }

    [Fact]
    public void FromCapture_ScreenDowngrade_KeepsPureReason()
    {
        var meta = HistoryCaptureMeta.FromCapture(
            CaptureOrigin.Screen, CaptureEngine.Fallback, printWindowTimedOut: false,
            notice: "窗口捕获失败，已按常规截图保存（PrintWindow 返回假）");
        Assert.Equal(CaptureHistoryRules.SourceScreen, meta.Source);
        Assert.Equal(CaptureHistoryRules.EngineFallback, meta.Engine);
        Assert.Equal("PrintWindow 返回假", meta.FallbackReason);
        Assert.False(meta.PrintWindowTimedOut);
    }

    [Fact]
    public void FromCapture_NormalScreenCrop_HasNoFallbackReason()
    {
        var meta = HistoryCaptureMeta.FromCapture(CaptureOrigin.Normal, CaptureEngine.Fallback, printWindowTimedOut: false, notice: null);
        Assert.Equal(CaptureHistoryRules.SourceNormal, meta.Source);
        Assert.Equal(CaptureHistoryRules.EngineFallback, meta.Engine);
        Assert.Null(meta.FallbackReason);
    }

    [Fact]
    public void ScreenQuick_IsNormalWithoutEngine()
    {
        Assert.Equal(CaptureHistoryRules.SourceNormal, HistoryCaptureMeta.ScreenQuick.Source);
        Assert.Null(HistoryCaptureMeta.ScreenQuick.Engine);
        Assert.Null(HistoryCaptureMeta.ScreenQuick.FallbackReason);
        Assert.False(HistoryCaptureMeta.ScreenQuick.PrintWindowTimedOut);
        Assert.Null(HistoryCaptureMeta.ScreenQuick.SessionId);
    }
}
