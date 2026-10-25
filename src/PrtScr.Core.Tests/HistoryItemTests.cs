namespace PrtScr.Core.Tests;

public sealed class HistoryItemTests
{
    private static HistoryItem Item(HistoryCaptureMode mode, string process = "chrome.exe") =>
        new("C:/data/history/a.png", DateTime.Now, 1024, 800, 600, mode, process, "title");

    [Fact]
    public void WindowMode_CategoryUsesProcess()
    {
        Assert.Equal("window:chrome.exe", Item(HistoryCaptureMode.Window).CategoryKey);
    }

    [Fact]
    public void WindowMode_SourceUsesProcessName()
    {
        Assert.Equal("窗口 · chrome.exe", Item(HistoryCaptureMode.Window).Source);
    }

    [Fact]
    public void EmptyProcess_DisplayFallsBackToUnknown()
    {
        var item = Item(HistoryCaptureMode.Window, "  ");
        Assert.Equal("未知进程", item.DisplayProcess);
        Assert.Equal("window:未知进程", item.CategoryKey);
    }

    [Theory]
    [InlineData(HistoryCaptureMode.Normal, "mode:Normal", "常规截图")]
    [InlineData(HistoryCaptureMode.PinCrop, "mode:PinCrop", "贴图裁剪")]
    [InlineData(HistoryCaptureMode.Legacy, "mode:Legacy", "早期截图")]
    public void NonWindowModes_UseModeCategoryAndLabel(
        HistoryCaptureMode mode, string category, string source)
    {
        var item = Item(mode);
        Assert.Equal(category, item.CategoryKey);
        Assert.Equal(source, item.Source);
    }

    [Fact]
    public void FileName_ComesFromPath()
    {
        Assert.Equal("a.png", Item(HistoryCaptureMode.Normal).FileName);
    }
}
