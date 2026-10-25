using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PrtScr.App.Views;

/// <summary>
/// 永久删除前的二次确认。默认按钮是「取消」，回车 / Esc 都不会误删；
/// 主题跟随宿主窗口，浅色/深色下与其他控件一致。
/// </summary>
internal static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(UIElement host, string title, string message, string confirmText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = host.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var style) && style is Style s)
            dialog.Style = s;
        if (host is FrameworkElement element)
            dialog.RequestedTheme = element.ActualTheme;

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
