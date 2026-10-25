namespace PrtScr.Core.Tests;

public sealed class PinBehaviorRulesTests
{
    [Theory]
    [InlineData(null, PinBehaviorRules.Clipboard)]
    [InlineData("", PinBehaviorRules.Clipboard)]
    [InlineData("   ", PinBehaviorRules.Clipboard)]
    [InlineData("LATEST", PinBehaviorRules.Latest)]
    [InlineData(" Text ", PinBehaviorRules.Text)]
    [InlineData("off", PinBehaviorRules.Off)]
    [InlineData("unknown", PinBehaviorRules.Clipboard)]
    public void Normalize_ReturnsKnownMode(string? input, string expected)
    {
        Assert.Equal(expected, PinBehaviorRules.Normalize(input));
    }

    [Theory]
    [InlineData(PinBehaviorRules.Clipboard, "剪贴板优先")]
    [InlineData(PinBehaviorRules.Latest, "只贴最新截图")]
    [InlineData(PinBehaviorRules.Text, "只贴剪贴板文字")]
    [InlineData(PinBehaviorRules.Off, "关闭贴图")]
    public void Describe_ReturnsSettingsLabel(string mode, string expected)
    {
        Assert.Equal(expected, PinBehaviorRules.Describe(mode));
    }

    [Theory]
    [InlineData(PinBehaviorRules.Clipboard, "剪贴板有图贴图、有字贴字。")]
    [InlineData(PinBehaviorRules.Latest, "直接贴出截图历史里最新的一张。")]
    [InlineData(PinBehaviorRules.Text, "把剪贴板里的文字贴成卡片。")]
    [InlineData(PinBehaviorRules.Off, "已关闭贴图；可在设置「贴图」中开启。")]
    public void DescribeTarget_ReturnsHomeLabel(string mode, string expected)
    {
        Assert.Equal(expected, PinBehaviorRules.DescribeTarget(mode));
    }
}
