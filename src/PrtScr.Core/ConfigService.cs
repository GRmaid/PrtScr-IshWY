using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrtScr.Core;

public sealed class AppConfig
{
    /// <summary>配置结构版本，用于默认值迁移（当前 2；null/缺失表示旧配置）。</summary>
    [JsonPropertyName("configVersion")]
    public int? ConfigVersion { get; set; }

    /// <summary>旧版截图键名（F1..F12、Pause、Scroll），仅在 hotkeyVk=0 时兜底。</summary>
    [JsonPropertyName("hotkeyKey")]
    public string HotkeyKey { get; set; } = "F1";

    /// <summary>旧版全屏快速截图键名（F1..F12），仅在 fullHotkeyVk=0 时兜底。</summary>
    [JsonPropertyName("fullScreenHotkeyKey")]
    public string FullScreenHotkeyKey { get; set; } = "F2";

    /// <summary>全屏截图热键：虚拟键码；默认 Alt+D，0 表示按旧字段解析，-1 表示用户已删除。</summary>
    [JsonPropertyName("fullHotkeyVk")]
    public int FullHotkeyVk { get; set; } = 0x44;

    /// <summary>修饰键位：Alt=1 / Ctrl=2 / Shift=4 / Win=8。</summary>
    [JsonPropertyName("fullHotkeyModifiers")]
    public int FullHotkeyModifiers { get; set; } = 1;

    /// <summary>截图热键：虚拟键码；默认 Alt+A，0 表示按旧字段 hotkeyKey 解析，-1 表示用户已删除。</summary>
    [JsonPropertyName("hotkeyVk")]
    public int HotkeyVk { get; set; } = 0x41;

    /// <summary>热键修饰键位掩码：MOD_ALT=1 / MOD_CONTROL=2 / MOD_SHIFT=4 / MOD_WIN=8。</summary>
    [JsonPropertyName("hotkeyModifiers")]
    public uint HotkeyModifiers { get; set; } = 1;

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

    /// <summary>优先使用 Umi-OCR（默认关闭；开启后在线才用，不在线自动回退 Windows OCR）。</summary>
    [JsonPropertyName("preferUmiOcr")]
    public bool PreferUmiOcr { get; set; } = false;

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

    /// <summary>前台是全屏游戏/全屏应用时，直接全屏快照，避免弹取景器抢焦点导致音频中断。默认开启。</summary>
    [JsonPropertyName("gameFullscreenQuickCapture")]
    public bool GameFullscreenQuickCapture { get; set; } = true;

    /// <summary>截图档位：normal = 常规取景（冻屏后框选/选窗），quick = 快捷全屏（直接进剪贴板）。</summary>
    [JsonPropertyName("captureProfile")]
    public string CaptureProfile { get; set; } = "normal";

    /// <summary>「切换截图模式」全局热键：默认 Alt+Shift+C。</summary>
    [JsonPropertyName("modeHotkeyVk")]
    public int ModeHotkeyVk { get; set; } = 0x43;

    [JsonPropertyName("modeHotkeyModifiers")]
    public int ModeHotkeyModifiers { get; set; } = 5;

    /// <summary>默认取景模式：normal = 常规截图（所见即所得，新用户默认），window = 窗口截图（抗遮挡）。</summary>
    [JsonPropertyName("defaultCaptureMode")]
    public string DefaultCaptureMode { get; set; } = "normal";

    /// <summary>新贴图初始大小（原图百分比，80–100）。超过工作区时仍会自动缩到工作区。</summary>
    [JsonPropertyName("pinInitialScale")]
    public int PinInitialScale { get; set; } = 100;

    /// <summary>贴图行为：clipboard = 剪贴板优先（图片/文字，空则最新截图）；latest = 只贴最新截图；text = 只贴剪贴板文字；off = 关闭。</summary>
    [JsonPropertyName("pinBehavior")]
    public string PinBehavior { get; set; } = "clipboard";

    /// <summary>文字贴图卡片背景色（#RRGGBB）；文字颜色按背景明暗自动选择。</summary>
    [JsonPropertyName("pinTextBackground")]
    public string PinTextBackground { get; set; } = "#1F1F1F";

    /// <summary>贴图裁剪后是否写入截图历史（分类「贴图裁剪」）。</summary>
    [JsonPropertyName("historyPinCrop")]
    public bool HistoryPinCrop { get; set; } = true;

    /// <summary>取景器内「上一个窗口」按键：默认 [；0 = 未设置。</summary>
    [JsonPropertyName("windowPrevKeyVk")]
    public int WindowPrevKeyVk { get; set; } = 0xDB;

    [JsonPropertyName("windowPrevKeyModifiers")]
    public int WindowPrevKeyModifiers { get; set; } = 0;

    /// <summary>取景器内「下一个窗口」按键：默认 ]；0 = 未设置。</summary>
    [JsonPropertyName("windowNextKeyVk")]
    public int WindowNextKeyVk { get; set; } = 0xDD;

    [JsonPropertyName("windowNextKeyModifiers")]
    public int WindowNextKeyModifiers { get; set; } = 0;

    /// <summary>选区边框与缩放手柄是否跟随系统主题色；关闭后才使用 SelectionBorderColor。</summary>
    [JsonPropertyName("selectionBorderFollowTheme")]
    public bool SelectionBorderFollowTheme { get; set; } = true;

    /// <summary>选区边框与缩放手柄颜色（#RRGGBB）。深色画面可改成亮色，浅色画面可改成深色。</summary>
    [JsonPropertyName("selectionBorderColor")]
    public string SelectionBorderColor { get; set; } = "#FFFFFF";

    /// <summary>纯文字提示的字号（DIP，11–32）。</summary>
    [JsonPropertyName("toastFontSize")]
    public int ToastFontSize { get; set; } = 14;

    /// <summary>提示背景材质：solid / acrylic / mica / liquid。</summary>
    [JsonPropertyName("toastMaterial")]
    public string ToastMaterial { get; set; } = "acrylic";

    /// <summary>提示背景不透明度（0.35–1.0）。</summary>
    [JsonPropertyName("toastOpacity")]
    public double ToastOpacity { get; set; } = 0.9;

    /// <summary>提示淡入时长（毫秒，0–1000）。</summary>
    [JsonPropertyName("toastFadeInMs")]
    public int ToastFadeInMs { get; set; } = 60;

    /// <summary>提示完全显示后的停留时长（毫秒，500–10000）。</summary>
    [JsonPropertyName("toastVisibleMs")]
    public int ToastVisibleMs { get; set; } = 3200;

    /// <summary>提示淡出时长（毫秒，0–2000）。</summary>
    [JsonPropertyName("toastFadeOutMs")]
    public int ToastFadeOutMs { get; set; } = 350;

    /// <summary>提示在屏幕工作区中的九宫格位置；custom = 使用下面的自定义矩形。</summary>
    [JsonPropertyName("toastPosition")]
    public string ToastPosition { get; set; } = "bottom-center";

    /// <summary>自定义提示位置（相对主显示器工作区的 DIP 坐标）。</summary>
    [JsonPropertyName("toastCustomX")]
    public double ToastCustomX { get; set; }

    [JsonPropertyName("toastCustomY")]
    public double ToastCustomY { get; set; }

    [JsonPropertyName("toastCustomWidth")]
    public double ToastCustomWidth { get; set; } = 360;

    [JsonPropertyName("toastCustomHeight")]
    public double ToastCustomHeight { get; set; } = 72;

    [JsonPropertyName("toastSuccessEnabled")]
    public bool ToastSuccessEnabled { get; set; } = true;

    [JsonPropertyName("toastSuccessText")]
    public string ToastSuccessText { get; set; } = "截图已复制到剪贴板";

    [JsonPropertyName("toastFailureEnabled")]
    public bool ToastFailureEnabled { get; set; } = true;

    [JsonPropertyName("toastFailureText")]
    public string ToastFailureText { get; set; } = "截图失败，请查看日志";

    [JsonPropertyName("toastProfileEnabled")]
    public bool ToastProfileEnabled { get; set; } = true;

    /// <summary>切换截图档位时的提示文本；{mode} 会替换成「常规取景」或「快捷全屏」。</summary>
    [JsonPropertyName("toastProfileText")]
    public string ToastProfileText { get; set; } = "已切换为「{mode}」";
}

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Action<string>? _log;

    /// <param name="baseDirectory">数据根目录；测试可传临时目录。默认程序目录，保持便携布局。</param>
    /// <param name="log">日志回调；由 App 注入，Core 不反向依赖 UI。</param>
    public ConfigService(string? baseDirectory = null, Action<string>? log = null)
    {
        BaseDirectory = baseDirectory ?? AppContext.BaseDirectory;
        _log = log;
    }

    public string BaseDirectory { get; }

    // 便携式布局（Snipaste 同款）：配置放在程序目录 data/config.json，随软件走
    public string FilePath => Path.Combine(BaseDirectory, "data", "config.json");

    public AppConfig Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions) ?? new AppConfig();

            // 手改配置可能写出越界值；统一夹到设置页允许的 1–200，避免数字框显示非法配额。
            Current.HistoryPerCategory = Math.Clamp(Current.HistoryPerCategory, 1, 200);

            // Umi-OCR 端口同样防手改越界：1–65535，避免拼出非法 URL 或状态页显示错误端口。
            Current.UmiOcrPort = Math.Clamp(Current.UmiOcrPort, 1, 65535);

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
                _log?.Invoke($"配置迁移到 v2: 贴图 {Current.PinHotkeyVk:X} / OCR {Current.OcrHotkeyVk:X}");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"配置读取失败，使用默认值: {ex.Message}");
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
            _log?.Invoke($"配置保存失败: {ex.Message}");
        }
    }
}
