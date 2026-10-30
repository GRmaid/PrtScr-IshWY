namespace PrtScr.Core.Tests;

public sealed class CaptureResultTests
{
    private static readonly Guid Session = Guid.Parse("0f7c2f4e8a1b4c3d9e5f60718293a4b5");

    [Fact]
    public void Success_CarriesSessionOriginAndEngine()
    {
        var result = CaptureResult.Success(Session, CaptureOrigin.Window, CaptureEngine.Wgc);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsCanceled);
        Assert.False(result.IsRecoverable);
        Assert.Null(result.Notice);
        Assert.Equal(Session, result.SessionId);
        Assert.Equal(CaptureOrigin.Window, result.Origin);
        Assert.Equal(CaptureEngine.Wgc, result.Engine);
        Assert.Equal(HistoryCaptureMode.Window, result.HistoryMode);
    }

    [Fact]
    public void Success_WithPrintWindowTimeout_KeepsFlagAndDetail()
    {
        var result = CaptureResult.Success(
            Session, CaptureOrigin.Window, CaptureEngine.Wgc,
            printWindowTimedOut: true, detail: "PrintWindow 超过 2000ms，已改用 WGC");

        Assert.True(result.PrintWindowTimedOut);
        Assert.Equal("PrintWindow 超过 2000ms，已改用 WGC", result.Detail);
        Assert.Null(result.Notice);
    }

    [Fact]
    public void Failed_ExposesFailureAndNotice()
    {
        var result = CaptureResult.Failed(
            Session, CaptureFailure.Busy, CaptureOrigin.Window, CaptureEngine.None);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsCanceled);
        Assert.True(result.IsRecoverable);
        Assert.Equal("该窗口上一张截图还在处理中，请稍后重试", result.Notice);
        Assert.Equal(HistoryCaptureMode.Window, result.HistoryMode);
    }

    [Fact]
    public void Canceled_IsQuietAndNotRecoverable()
    {
        var result = CaptureResult.Canceled(Session, CaptureOrigin.Normal, CaptureEngine.None, "取景已取消");

        Assert.True(result.IsCanceled);
        Assert.True(CaptureRules.CountsAsCancel(result.Failure));
        Assert.False(result.IsRecoverable);
        Assert.Null(result.Notice);
        Assert.Equal("取景已取消", result.Detail);
        Assert.Equal(HistoryCaptureMode.Normal, result.HistoryMode);
    }

    [Fact]
    public void Failed_WithNoneFailure_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CaptureResult.Failed(Session, CaptureFailure.None, CaptureOrigin.Window, CaptureEngine.Wgc));
    }

    [Fact]
    public void Failed_WithCanceledFailure_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CaptureResult.Failed(Session, CaptureFailure.Canceled, CaptureOrigin.Normal, CaptureEngine.None));
    }

    [Theory]
    [InlineData(CaptureOrigin.Window, HistoryCaptureMode.Window)]
    [InlineData(CaptureOrigin.Normal, HistoryCaptureMode.Normal)]
    [InlineData(CaptureOrigin.Screen, HistoryCaptureMode.Normal)]
    public void HistoryMode_FollowsOrigin(CaptureOrigin origin, HistoryCaptureMode expected)
    {
        var result = CaptureResult.Success(Session, origin, CaptureEngine.BitBlt);

        Assert.Equal(expected, result.HistoryMode);
    }
}
