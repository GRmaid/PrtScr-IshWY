namespace PrtScr.App.Services;

/// <summary>读取 Windows 主题强调色，供选区边框与设置页预览使用。</summary>
public static class ThemeColorService
{
    public static Windows.UI.Color Accent
    {
        get
        {
            try
            {
                var color = new Windows.UI.ViewManagement.UISettings()
                    .GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
                color.A = 0xFF;
                return color;
            }
            catch
            {
                return Windows.UI.Color.FromArgb(0xFF, 0x00, 0xC8, 0xFF);
            }
        }
    }

    public static string AccentHex
    {
        get
        {
            var color = Accent;
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
    }
}
