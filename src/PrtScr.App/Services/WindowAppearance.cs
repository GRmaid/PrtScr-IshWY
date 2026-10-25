using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace PrtScr.App.Services;

/// <summary>让系统标题栏跟随应用实际明暗主题，避免白标题栏 + 深色内容。</summary>
internal static class WindowAppearance
{
    public static void ApplyTitleBar(Window window, FrameworkElement root)
    {
        Update(window, root.ActualTheme);
        root.ActualThemeChanged += (_, _) => Update(window, root.ActualTheme);
    }

    private static void Update(Window window, ElementTheme theme)
    {
        try
        {
            window.AppWindow.TitleBar.PreferredTheme = theme == ElementTheme.Light
                ? TitleBarTheme.Light
                : TitleBarTheme.Dark;
        }
        catch (Exception ex)
        {
            App.Log($"WindowAppearance: 标题栏主题失败 {ex.Message}");
        }
    }
}
