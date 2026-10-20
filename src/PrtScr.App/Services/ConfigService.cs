using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrtScr.App.Services;

public sealed class AppConfig
{
    /// <summary>配置结构版本，用于默认值迁移（当前 2；null/缺失表示旧配置）。</summary>
    [JsonPropertyName("configVersion")]
    public int? ConfigVersion { get; set; }

    /// <summary>截图热键的虚拟键名（F1..F12、Pause、Scroll）。</summary>
    [JsonPropertyName("hotkeyKey")]
    public string HotkeyKey { get; set; } = "F1";

    /// <summary>全屏快速截图热键的虚拟键名（F1..F12）。</summary>
    [JsonPropertyName("fullScreenHotkeyKey")]
    public string FullScreenHotkeyKey { get; set; } = "F2";

    /// <summary>全屏截图热键：虚拟键码（0 表示按旧字段解析）。</summary>
    [JsonPropertyName("fullHotkeyVk")]
    public int FullHotkeyVk { get; set; }

    /// <summary>修饰键位：Alt=1 / Ctrl=2 / Shift=4 / Win=8。</summary>
    [JsonPropertyName("fullHotkeyModifiers")]
    public int FullHotkeyModifiers { get; set; }

    /// <summary>截图热键：虚拟键码（0 表示按旧字段 hotkeyKey 解析）。</summary>
    [JsonPropertyName("hotkeyVk")]
    public int HotkeyVk { get; set; }

    /// <summary>热键修饰键位掩码：MOD_ALT=1 / MOD_CONTROL=2 / MOD_SHIFT=4 / MOD_WIN=8。</summary>
    [JsonPropertyName("hotkeyModifiers")]
    public uint HotkeyModifiers { get; set; } = 0;

    /// <summary>「从剪贴板贴图」热键：默认 Ctrl+Alt+V（V = paste）。</summary>
    [JsonPropertyName("pinHotkeyVk")]
    public int PinHotkeyVk { get; set; } = 0x56;

    [JsonPropertyName("pinHotkeyModifiers")]
    public int PinHotkeyModifiers { get; set; } = 3;

    /// <summary>「从剪贴板识别文字」热键：默认 Ctrl+Alt+S（S = scan）。</summary>
    [JsonPropertyName("ocrHotkeyVk")]
    public int OcrHotkeyVk { get; set; } = 0x53;

    [JsonPropertyName("ocrHotkeyModifiers")]
    public int OcrHotkeyModifiers { get; set; } = 3;

    /// <summary>自定义背景图路径（空 = 默认外观）。</summary>
    [JsonPropertyName("backgroundImagePath")]
    public string BackgroundImagePath { get; set; } = "";

    [JsonPropertyName("backgroundOpacity")]
    public double BackgroundOpacity { get; set; } = 0.25;

    /// <summary>优先使用 Umi-OCR（在线时；不在线自动回退 Windows OCR）。</summary>
    [JsonPropertyName("preferUmiOcr")]
    public bool PreferUmiOcr { get; set; } = true;

    /// <summary>Umi-OCR 端口（可选引擎）。</summary>
    [JsonPropertyName("umiOcrPort")]
    public int UmiOcrPort { get; set; } = 1224;

    /// <summary>是否把截好的图另存一份到程序目录 data/history（仅本机）。</summary>
    [JsonPropertyName("saveHistory")]
    public bool SaveHistory { get; set; } = true;

    /// <summary>每个历史分类保留张数：窗口截图按进程各算一组，常规/早期各一组；1–200。</summary>
    [JsonPropertyName("historyPerCategory")]
    public int HistoryPerCategory { get; set; } = 30;

    /// <summary>按下截图热键后等待多少秒再冻结画面（0 = 不延时；支持 3/5/10）。</summary>
    [JsonPropertyName("captureDelaySeconds")]
    public int CaptureDelaySeconds { get; set; } = 0;

    /// <summary>冻结画面时是否把鼠标指针一起画进截图。</summary>
    [JsonPropertyName("captureCursor")]
    public bool CaptureCursor { get; set; } = false;

    /// <summary>默认取景模式：window = 窗口截图（抗遮挡），normal = 常规截图（所见即所得）。</summary>
    [JsonPropertyName("defaultCaptureMode")]
    public string DefaultCaptureMode { get; set; } = "window";
}

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // 便携式布局（Snipaste 同款）：配置放在程序目录 data/config.json，随软件走
    private static string FilePath => Path.Combine(
        AppContext.BaseDirectory, "data", "config.json");

    public AppConfig Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions) ?? new AppConfig();

            // v2：旧默认 Ctrl+Alt+P / Ctrl+Alt+O 在本机被其他程序占用（RegisterHotKey 1409），
            // 迁移到 Ctrl+Alt+V（贴图）/ Ctrl+Alt+S（OCR）；只改仍是旧默认值的配置。
            if ((Current.ConfigVersion ?? 0) < 2)
            {
                if ((Current.PinHotkeyVk is 0 or 0x50) && Current.PinHotkeyModifiers == 3)
                {
                    Current.PinHotkeyVk = 0x56;
                    Current.PinHotkeyModifiers = 3;
                }
                if ((Current.OcrHotkeyVk is 0 or 0x4F) && Current.OcrHotkeyModifiers == 3)
                {
                    Current.OcrHotkeyVk = 0x53;
                    Current.OcrHotkeyModifiers = 3;
                }
                Current.ConfigVersion = 2;
                Save();
                App.Log($"配置迁移到 v2: 贴图 {Current.PinHotkeyVk:X} / OCR {Current.OcrHotkeyVk:X}");
            }
        }
        catch (Exception ex)
        {
            App.Log($"配置读取失败，使用默认值: {ex.Message}");
            Current = new AppConfig();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            App.Log($"配置保存失败: {ex.Message}");
        }
    }
}
