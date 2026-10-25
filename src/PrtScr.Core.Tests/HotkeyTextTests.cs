namespace PrtScr.Core.Tests;

public sealed class HotkeyTextTests
{
    [Theory]
    [InlineData("F1", 0x70u)]
    [InlineData("f12", 0x7Bu)]
    [InlineData(" Pause ", 0x13u)]
    [InlineData("Scroll", 0x91u)]
    [InlineData("not-a-key", 0x70u)]
    [InlineData(null, 0x70u)]
    public void KeyNameToVirtualKey_MapsLegacyNames(string? name, uint expected)
    {
        Assert.Equal(expected, HotkeyText.KeyNameToVirtualKey(name));
    }

    [Theory]
    [InlineData(0xDB, "[")]
    [InlineData(0xDD, "]")]
    [InlineData(0xBA, ";")]
    [InlineData(0xBB, "=")]
    [InlineData(0xBC, ",")]
    [InlineData(0xBD, "-")]
    [InlineData(0xBE, ".")]
    [InlineData(0xBF, "/")]
    [InlineData(0xC0, "`")]
    [InlineData(0xDC, "\\")]
    public void OemKeyLabel_MapsReadableKeycaps(int vk, string expected)
    {
        Assert.Equal(expected, HotkeyText.OemKeyLabel(vk));
    }

    [Fact]
    public void OemKeyLabel_ReturnsNullForNormalKey()
    {
        Assert.Null(HotkeyText.OemKeyLabel(0x41));
    }

    [Fact]
    public void Describe_NoKey_ReturnsUnset()
    {
        Assert.Equal("未设置", HotkeyText.Describe(0, 15, _ => "A"));
    }

    [Fact]
    public void Describe_OrdersModifiersCtrlAltShiftWin()
    {
        Assert.Equal("Ctrl + Alt + Shift + Win + A", HotkeyText.Describe(0x41, 15, _ => "A"));
    }

    [Fact]
    public void Describe_UsesOemLabelBeforeFallback()
    {
        Assert.Equal("Alt + [", HotkeyText.Describe(0xDB, 1, _ => "OEM_4"));
    }

    [Fact]
    public void Describe_UsesFallbackForKeyName()
    {
        Assert.Equal("Ctrl + A", HotkeyText.Describe(0x41, 2, _ => "A"));
    }
}
