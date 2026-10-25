using System.Globalization;

namespace PrtScr.Core;

/// <summary>#RRGGBB 颜色文本的纯规则；WinUI 颜色对象由 App 层自行构造。</summary>
public static class ColorRules
{
    /// <summary>接受带或不带 # 的 RRGGBB，返回大写 #RRGGBB；格式不对返回 null。</summary>
    public static string? NormalizeHexColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim();
        if (!value.StartsWith('#'))
            value = "#" + value;
        if (value.Length != 7 || !uint.TryParse(value[1..],
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return null;
        return value.ToUpperInvariant();
    }

    /// <summary>把 #RRGGBB 解析成 RGB；格式不对返回 null。</summary>
    public static (byte R, byte G, byte B)? ParseRgb(string? text)
    {
        var normalized = NormalizeHexColor(text);
        if (normalized is null)
            return null;
        var rgb = uint.Parse(normalized[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}
