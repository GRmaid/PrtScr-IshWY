using System.Drawing;
using Microsoft.UI.Dispatching;
using PrtScr.App.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PrtScr.App.Services;

/// <summary>
/// 从剪贴板读取图片并创建贴图（Snipaste 同逻辑：截图先落剪贴板，贴图再从剪贴板取）。
/// </summary>
public static class PinService
{
    /// <summary>读取剪贴板里的图片字节（供 OCR 使用）。</summary>
    public static async Task<byte[]?> ReadClipboardImageBytesAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap))
                return null;

            var reference = await content.GetBitmapAsync();
            using var source = await reference.OpenReadAsync();
            var bytes = new byte[(int)source.Size];
            await source.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            return bytes;
        }
        catch (Exception ex)
        {
            App.Log($"读取剪贴板图片失败: {ex}");
            return null;
        }
    }

    public static async Task<bool> PasteFromClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap))
            {
                App.Log("Pin: 剪贴板里没有图片");
                return false;
            }

            var reference = await content.GetBitmapAsync();
            using var source = await reference.OpenReadAsync();
            var bytes = new byte[(int)source.Size];
            await source.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);

            return CreatePin(bytes) is not null;
        }
        catch (Exception ex)
        {
            App.Log($"Pin: 失败 {ex}");
            return false;
        }
    }

    /// <summary>用一张 PNG 直接创建贴图（截图历史等入口复用）。</summary>
    public static bool PastePng(byte[] bytes) => PastePng(bytes, edit: false);

    /// <summary>用一张 PNG 创建贴图并直接进入标注模式（截图历史「标注」入口）。</summary>
    public static bool PastePngAndEdit(byte[] bytes) => PastePng(bytes, edit: true);

    private static bool PastePng(byte[] bytes, bool edit)
    {
        try
        {
            return CreatePin(bytes, edit) is not null;
        }
        catch (Exception ex)
        {
            App.Log($"Pin: 失败 {ex}");
            return false;
        }
    }

    private static NativePinWindow? CreatePin(byte[] bytes, bool edit = false)
    {
        using var image = Image.FromStream(new MemoryStream(bytes));
        var pixelWidth = image.Width;
        var pixelHeight = image.Height;
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return null;

        var queue = DispatcherQueue.GetForCurrentThread();
        var window = NativePinWindow.TryCreate(bytes, pixelWidth, pixelHeight, queue);
        if (window is null)
        {
            App.Log("Pin: 创建贴图窗口失败");
            return null;
        }

        App.Log($"Pin: created {pixelWidth}x{pixelHeight}");
        if (edit)
            window.RequestEditMode();
        return window;
    }
}
