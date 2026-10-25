namespace PrtScr.Core;

/// <summary>贴图来源的纯规则：归一化、中文名与首页短说明；不依赖剪贴板/窗口。</summary>
public static class PinBehaviorRules
{
    public const string Clipboard = "clipboard";
    public const string Latest = "latest";
    public const string Text = "text";
    public const string Off = "off";

    /// <summary>把设置里的贴图行为归一化成四种已知模式；空值/未知值回落到剪贴板优先。</summary>
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Latest => Latest,
        Text => Text,
        Off => Off,
        _ => Clipboard,
    };

    /// <summary>设置下拉框 / 状态栏用的中文名。</summary>
    public static string Describe(string? value) => Normalize(value) switch
    {
        Latest => "只贴最新截图",
        Text => "只贴剪贴板文字",
        Off => "关闭贴图",
        _ => "剪贴板优先",
    };

    /// <summary>首页贴图卡片与托盘菜单用的短说明。</summary>
    public static string DescribeTarget(string? value) => Normalize(value) switch
    {
        Latest => "直接贴出截图历史里最新的一张。",
        Text => "把剪贴板里的文字贴成卡片。",
        Off => "已关闭贴图；可在设置「贴图」中开启。",
        _ => "剪贴板有图贴图、有字贴字。",
    };
}
