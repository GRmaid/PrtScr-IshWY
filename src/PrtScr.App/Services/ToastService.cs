namespace PrtScr.App.Services;

/// <summary>按设置决定是否弹出、弹什么、用多大字号与屏幕位置；取景器外的统一提示入口。</summary>
public static class ToastService
{
    public static void ShowCaptureSuccess()
    {
        var config = App.Config.Current;
        if (!config.ToastSuccessEnabled)
            return;
        Show(config.ToastSuccessText, config);
    }

    public static void ShowCaptureFailure()
    {
        var config = App.Config.Current;
        if (!config.ToastFailureEnabled)
            return;
        Show(config.ToastFailureText, config);
    }

    /// <summary>切换截图档位时的提示，{mode} 替换为「常规取景」或「快捷全屏」。</summary>
    public static void ShowProfile(string modeName)
    {
        var config = App.Config.Current;
        if (!config.ToastProfileEnabled)
            return;
        var text = (config.ToastProfileText ?? "").Replace("{mode}", modeName, StringComparison.Ordinal);
        Show(text, config);
    }

    /// <summary>不受三类提示开关影响的即时消息（贴图/OCR/延时等），仍用同一套外观。</summary>
    public static void ShowInfo(string? text) => Show(text, App.Config.Current);

    /// <summary>设置页预览：不写配置，只按当前字号/位置显示一条示例文字。</summary>
    public static void Preview(string? text)
    {
        var config = App.Config.Current;
        var content = string.IsNullOrWhiteSpace(text) ? "提示预览" : text;
        Show(content, config);
    }

    private static void Show(string? text, AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        Views.ToastWindow.Show(text, config);
    }
}
