using System.IO;
using System.Text.Json;

namespace PrtScr.Core.Tests;

public sealed class ConfigServiceTests
{
    [Fact]
    public void Load_MissingFile_UsesDefaultsAndMigratesToV2()
    {
        using var temp = new TempDirectory();
        var service = new ConfigService(temp.Path);

        service.Load();

        Assert.Equal("normal", service.Current.DefaultCaptureMode);
        Assert.Equal("clipboard", service.Current.PinBehavior);
        Assert.Equal("#1F1F1F", service.Current.PinTextBackground);
        Assert.Equal(30, service.Current.HistoryPerCategory);
        Assert.Equal(2, service.Current.ConfigVersion);
        Assert.Equal(0x56, service.Current.PinHotkeyVk);
        Assert.True(File.Exists(service.FilePath));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsCustomValues()
    {
        using var temp = new TempDirectory();
        var first = new ConfigService(temp.Path);
        first.Current.ConfigVersion = 2;
        first.Current.PinBehavior = "text";
        first.Current.PinTextBackground = "#123456";
        first.Current.HistoryPerCategory = 42;
        first.Current.ToastFontSize = 18;
        first.Save();

        var second = new ConfigService(temp.Path);
        second.Load();

        Assert.Equal("text", second.Current.PinBehavior);
        Assert.Equal("#123456", second.Current.PinTextBackground);
        Assert.Equal(42, second.Current.HistoryPerCategory);
        Assert.Equal(18, second.Current.ToastFontSize);
    }

    [Fact]
    public void Load_OldDefaultHotkeys_MigratesPinAndOcr()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(
            System.IO.Path.Combine(temp.Path, "data", "config.json"))!);
        File.WriteAllText(
            System.IO.Path.Combine(temp.Path, "data", "config.json"),
            """
            {"configVersion":1,"pinHotkeyVk":80,"pinHotkeyModifiers":3,"ocrHotkeyVk":79,"ocrHotkeyModifiers":3}
            """);

        var service = new ConfigService(temp.Path);
        service.Load();

        Assert.Equal(0x56, service.Current.PinHotkeyVk);
        Assert.Equal(0x53, service.Current.OcrHotkeyVk);
        Assert.Equal(2, service.Current.ConfigVersion);
    }

    [Fact]
    public void Load_CustomHotkeys_AreNotMigrated()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "data"));
        File.WriteAllText(
            System.IO.Path.Combine(temp.Path, "data", "config.json"),
            """
            {"configVersion":2,"pinHotkeyVk":65,"pinHotkeyModifiers":2,"ocrHotkeyVk":81,"ocrHotkeyModifiers":2}
            """);

        var service = new ConfigService(temp.Path);
        service.Load();

        Assert.Equal(65, service.Current.PinHotkeyVk);
        Assert.Equal(2, service.Current.PinHotkeyModifiers);
        Assert.Equal(81, service.Current.OcrHotkeyVk);
        Assert.Equal(2, service.Current.OcrHotkeyModifiers);
    }

    [Fact]
    public void Load_InvalidJson_FallsBackToDefaults()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "data"));
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "data", "config.json"), "{ not json");

        var service = new ConfigService(temp.Path);
        service.Load();

        Assert.Null(service.Current.ConfigVersion);
        Assert.Equal("clipboard", service.Current.PinBehavior);
        Assert.Equal(30, service.Current.HistoryPerCategory);
    }

    [Fact]
    public void FilePath_IsInsideGivenBaseDirectory()
    {
        var service = new ConfigService(@"C:\Temp\PrtScrTests");

        Assert.Equal(@"C:\Temp\PrtScrTests\data\config.json", service.FilePath);
    }


    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(200, 200)]
    [InlineData(999, 200)]
    public void Load_HistoryPerCategory_IsClampedToAllowedRange(int stored, int expected)
    {
        using var temp = new TempDirectory();
        WriteConfig(temp, $"{{\"configVersion\":2,\"historyPerCategory\":{stored}}}");

        var service = new ConfigService(temp.Path);
        service.Load();

        Assert.Equal(expected, service.Current.HistoryPerCategory);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(65535, 65535)]
    [InlineData(70000, 65535)]
    public void Load_UmiOcrPort_IsClampedToAllowedRange(int stored, int expected)
    {
        using var temp = new TempDirectory();
        WriteConfig(temp, $"{{\"configVersion\":2,\"umiOcrPort\":{stored}}}");

        var service = new ConfigService(temp.Path);
        service.Load();

        Assert.Equal(expected, service.Current.UmiOcrPort);
    }

    [Fact]
    public void Load_MissingFile_KeepsOcrDefaultsOffAndPort1224()
    {
        using var temp = new TempDirectory();
        var service = new ConfigService(temp.Path);

        service.Load();

        Assert.False(service.Current.PreferUmiOcr);
        Assert.Equal(1224, service.Current.UmiOcrPort);
        Assert.Equal(30, service.Current.HistoryPerCategory);
    }

    [Fact]
    public void OcrAndHistoryFields_KeepDocumentedJsonNamesAndDefaults()
    {
        var config = new AppConfig();

        Assert.False(config.PreferUmiOcr);
        Assert.Equal(1224, config.UmiOcrPort);
        Assert.Equal(30, config.HistoryPerCategory);
        Assert.True(config.HistoryPinCrop);
        Assert.True(config.SaveHistory);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(config));
        var root = doc.RootElement;
        Assert.False(root.GetProperty("preferUmiOcr").GetBoolean());
        Assert.Equal(1224, root.GetProperty("umiOcrPort").GetInt32());
        Assert.Equal(30, root.GetProperty("historyPerCategory").GetInt32());
        Assert.True(root.GetProperty("historyPinCrop").GetBoolean());
        Assert.True(root.GetProperty("saveHistory").GetBoolean());
    }

    private static void WriteConfig(TempDirectory temp, string json)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "data"));
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "data", "config.json"), json);
    }

    [Fact]
    public void Load_Migration_LogsThroughInjectedCallback()
    {
        using var temp = new TempDirectory();
        var messages = new List<string>();
        var service = new ConfigService(temp.Path, messages.Add);

        service.Load();

        Assert.Contains(messages, m => m.Contains("配置迁移到 v2", StringComparison.Ordinal));
    }
}
