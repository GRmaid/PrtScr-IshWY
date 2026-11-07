using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrtScr.Core;

/// <summary>
/// 截图历史 .png.json 元数据（version 2）。属性上的 JSON 键名即落盘契约，
/// 改名会破坏既有历史读取，需同步 Core.Tests 的契约用例。
/// </summary>
public sealed class HistoryMetadata
{
    /// <summary>当前写入版本；version 1 的旧文件仍按缺字段默认值读取，不回填。</summary>
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("processName")]
    public string? ProcessName { get; set; }

    [JsonPropertyName("windowTitle")]
    public string? WindowTitle { get; set; }

    [JsonPropertyName("capturedAt")]
    public DateTime CapturedAt { get; set; }

    /// <summary>实际来源：window / normal / screen；非采集来源为 null。</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>产出画面的引擎：wgc / printwindow / dxgi / bitblt / fallback；未知为 null。</summary>
    [JsonPropertyName("engine")]
    public string? Engine { get; set; }

    /// <summary>降级原因（已剥壳、不超过 200 字符），或 PrintWindow 超时的固定标记。</summary>
    [JsonPropertyName("fallbackReason")]
    public string? FallbackReason { get; set; }

    [JsonPropertyName("printWindowTimedOut")]
    public bool PrintWindowTimedOut { get; set; }

    /// <summary>本阶段恒 null：会话号属易变身份，不进用户数据。</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    /// <summary>序列化为落盘 JSON；null 字段不写出。</summary>
    public static string Serialize(HistoryMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    /// <summary>从落盘 JSON 读取；缺字段按属性默认值（version=0、新增字段 null/false）。</summary>
    public static HistoryMetadata? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<HistoryMetadata>(json, JsonOptions);
    }
}
