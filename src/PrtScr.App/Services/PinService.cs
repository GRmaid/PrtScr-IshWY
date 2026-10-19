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

            using var image = Image.FromStream(new MemoryStream(bytes));
            var pixelWidth = image.Width;
            var pixelHeight = image.Height;
            if (pixelWidth <= 0 || pixelHeight <= 0)
                return false;

            var queue = DispatcherQueue.GetForCurrentThread();
            var window = NativePinWindow.TryCreate(bytes, pixelWidth, pixelHeight, queue);
            if (window is null)
            {
                App.Log("Pin: 创建贴图窗口失败");
                return false;
            }

            App.Log($"Pin: created {pixelWidth}x{pixelHeight}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"Pin: 失败 {ex}");
            return false;
        }
    }
}
