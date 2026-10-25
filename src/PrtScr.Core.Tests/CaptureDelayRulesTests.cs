namespace PrtScr.Core.Tests;

public sealed class CaptureDelayRulesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(7, 0)]
    [InlineData(-1, 0)]
    public void Normalize_OnlyAllowsSupportedDelays(int input, int expected)
    {
        Assert.Equal(expected, CaptureDelayRules.Normalize(input));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 1)]
    [InlineData(5, 2)]
    [InlineData(10, 3)]
    [InlineData(99, 0)]
    public void ToIndex_MapsSecondsToComboIndex(int seconds, int expected)
    {
        Assert.Equal(expected, CaptureDelayRules.ToIndex(seconds));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 5)]
    [InlineData(3, 10)]
    [InlineData(9, 0)]
    public void FromIndex_MapsComboIndexToSeconds(int index, int expected)
    {
        Assert.Equal(expected, CaptureDelayRules.FromIndex(index));
    }

    [Fact]
    public void IndexRoundTrip_PreservesSupportedDelays()
    {
        foreach (var seconds in new[] { 0, 3, 5, 10 })
            Assert.Equal(seconds, CaptureDelayRules.FromIndex(CaptureDelayRules.ToIndex(seconds)));
    }
}
