namespace PrtScr.Core.Tests;

public sealed class ColorRulesTests
{
    [Theory]
    [InlineData("#1f1f1f", "#1F1F1F")]
    [InlineData("1f1f1f", "#1F1F1F")]
    [InlineData("  #ABC123  ", "#ABC123")]
    public void NormalizeHexColor_AcceptsWithOrWithoutHash(string input, string expected)
    {
        Assert.Equal(expected, ColorRules.NormalizeHexColor(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("#1234567")]
    public void NormalizeHexColor_RejectsInvalid(string? input)
    {
        Assert.Null(ColorRules.NormalizeHexColor(input));
    }

    [Fact]
    public void ParseRgb_ReturnsComponents()
    {
        var rgb = ColorRules.ParseRgb("#123456");

        Assert.NotNull(rgb);
        Assert.Equal((byte)0x12, rgb!.Value.R);
        Assert.Equal((byte)0x34, rgb.Value.G);
        Assert.Equal((byte)0x56, rgb.Value.B);
    }

    [Fact]
    public void ParseRgb_Invalid_ReturnsNull()
    {
        Assert.Null(ColorRules.ParseRgb("nope"));
    }
}
