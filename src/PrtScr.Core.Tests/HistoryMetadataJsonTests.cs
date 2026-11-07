using System.Text.Json;

namespace PrtScr.Core.Tests;

/// <summary>历史元数据 version 2 的 JSON 落盘契约：键名、版本、null 省略与旧文件默认值。</summary>
public sealed class HistoryMetadataJsonTests
{
    private static HistoryMetadata CreateFullMetadata() => new()
    {
        Version = HistoryMetadata.CurrentVersion,
        Mode = HistoryCaptureMode.Normal.ToString(),
        ProcessName = "chrome",
        WindowTitle = "Docs",
        CapturedAt = new DateTime(2026, 10, 30, 12, 34, 56, DateTimeKind.Local),
        Source = CaptureHistoryRules.SourceScreen,
        Engine = CaptureHistoryRules.EngineFallback,
        FallbackReason = "PrintWindow 超时",
        PrintWindowTimedOut = false,
        SessionId = null,
    };

    [Fact]
    public void CurrentVersion_IsTwo()
        => Assert.Equal(2, HistoryMetadata.CurrentVersion);

    [Fact]
    public void Serialize_WritesVersionTwoAndAllNewKeys()
    {
        var json = HistoryMetadata.Serialize(CreateFullMetadata());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("version").GetInt32());
        Assert.Equal("Normal", root.GetProperty("mode").GetString());
        Assert.Equal("chrome", root.GetProperty("processName").GetString());
        Assert.Equal("Docs", root.GetProperty("windowTitle").GetString());
        Assert.Equal("screen", root.GetProperty("source").GetString());
        Assert.Equal("fallback", root.GetProperty("engine").GetString());
        Assert.Equal("PrintWindow 超时", root.GetProperty("fallbackReason").GetString());
        Assert.False(root.GetProperty("printWindowTimedOut").GetBoolean());
    }

    [Fact]
    public void Serialize_OmitsNullOptionalFields()
    {
        var metadata = CreateFullMetadata();
        metadata.Source = null;
        metadata.Engine = null;
        metadata.FallbackReason = null;
        metadata.SessionId = null;

        var json = HistoryMetadata.Serialize(metadata);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("source", out _));
        Assert.False(root.TryGetProperty("engine", out _));
        Assert.False(root.TryGetProperty("fallbackReason", out _));
        Assert.False(root.TryGetProperty("sessionId", out _));
    }

    [Fact]
    public void Serialize_AlwaysWritesPrintWindowTimedOutFalse()
    {
        var metadata = CreateFullMetadata();
        metadata.PrintWindowTimedOut = false;
        metadata.Source = null;

        var json = HistoryMetadata.Serialize(metadata);
        using var document = JsonDocument.Parse(json);
        var property = document.RootElement.GetProperty("printWindowTimedOut");

        Assert.Equal(JsonValueKind.False, property.ValueKind);
    }

    [Fact]
    public void Deserialize_LegacyJsonWithoutNewFields_UsesSafeDefaults()
    {
        const string legacyJson = """{"version":1,"mode":"Window","processName":"chrome","windowTitle":"Docs","capturedAt":"2026-10-30T10:00:00"}""";

        var metadata = HistoryMetadata.Deserialize(legacyJson);

        Assert.NotNull(metadata);
        Assert.Equal(1, metadata!.Version);
        Assert.Equal("Window", metadata.Mode);
        Assert.Equal("chrome", metadata.ProcessName);
        Assert.Null(metadata.Source);
        Assert.Null(metadata.Engine);
        Assert.Null(metadata.FallbackReason);
        Assert.False(metadata.PrintWindowTimedOut);
        Assert.Null(metadata.SessionId);
    }

    [Fact]
    public void Deserialize_MissingVersion_LeavesZeroForCallerToTreatAsLegacy()
    {
        const string json = """{"mode":"Normal","processName":null,"windowTitle":"","capturedAt":"2026-10-30T10:00:00"}""";

        var metadata = HistoryMetadata.Deserialize(json);

        Assert.NotNull(metadata);
        Assert.Equal(0, metadata!.Version);
    }

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var original = CreateFullMetadata();
        original.Source = CaptureHistoryRules.SourceWindow;
        original.Engine = CaptureHistoryRules.EngineWgc;
        original.FallbackReason = CaptureHistoryRules.PrintWindowTimeoutToken;
        original.PrintWindowTimedOut = true;

        var restored = HistoryMetadata.Deserialize(HistoryMetadata.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(original.Version, restored!.Version);
        Assert.Equal(original.Mode, restored.Mode);
        Assert.Equal(original.ProcessName, restored.ProcessName);
        Assert.Equal(original.WindowTitle, restored.WindowTitle);
        Assert.Equal(original.CapturedAt, restored.CapturedAt);
        Assert.Equal(original.Source, restored.Source);
        Assert.Equal(original.Engine, restored.Engine);
        Assert.Equal(original.FallbackReason, restored.FallbackReason);
        Assert.Equal(original.PrintWindowTimedOut, restored.PrintWindowTimedOut);
        Assert.Equal(original.SessionId, restored.SessionId);
    }

    [Fact]
    public void Serialize_NullMetadata_Throws()
        => Assert.Throws<ArgumentNullException>(() => HistoryMetadata.Serialize(null!));
}
