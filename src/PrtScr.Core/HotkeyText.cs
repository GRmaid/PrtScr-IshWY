namespace PrtScr.Core;

/// <summary>热键文本的纯规则：键名映射、OEM 键帽与“修饰键 + 键名”拼接。</summary>
public static class HotkeyText
{
    /// <summary>旧配置键名转虚拟键码；无法识别时沿用历史默认 F1。</summary>
    public static uint KeyNameToVirtualKey(string? name) => (name ?? "").Trim().ToUpperInvariant() switch
    {
        "F1" => 0x70,
        "F2" => 0x71,
        "F3" => 0x72,
        "F4" => 0x73,
        "F5" => 0x74,
        "F6" => 0x75,
        "F7" => 0x76,
        "F8" => 0x77,
        "F9" => 0x78,
        "F10" => 0x79,
        "F11" => 0x7A,
        "F12" => 0x7B,
        "PAUSE" => 0x13,
        "SCROLL" => 0x91,
        _ => 0x70,
    };

    /// <summary>OEM 键（[ ] ; = , - . / 反斜杠等）在中文系统上 VirtualKey 名称不可读，统一映射成键帽字符。</summary>
    public static string? OemKeyLabel(int vk) => vk switch
    {
        0xDB => "[",
        0xDD => "]",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDC => "\\",
        _ => null,
    };

    /// <summary>把 vk + 修饰键位掩码转成可展示文本，例如 Ctrl + Alt + P；非 OEM 键名由调用方提供。</summary>
    public static string Describe(int vk, int modifiers, Func<int, string> keyName)
    {
        ArgumentNullException.ThrowIfNull(keyName);
        if (vk == 0)
            return "未设置";

        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        parts.Add(OemKeyLabel(vk) ?? keyName(vk));
        return string.Join(" + ", parts);
    }
}
