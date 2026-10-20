using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PrtScr.App.Services;

/// <summary>
/// 统一的“另存为”入口：任意 HWND（WinUI 窗口、原生贴图窗口）都能作为 FileSavePicker 的 owner。
/// 支持 PNG / JPEG；取消或失败返回 null，不抛给调用方。
/// </summary>
internal static class FileSaveService
{
    public static string SuggestedName(string prefix)
        => $"IshWY-{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}";

    /// <summary>把 PNG 字节另存为文件。ownerHwnd 必须是调用线程可见的窗口句柄。</summary>
    public static async Task<string?> SavePngAsync(nint ownerHwnd, byte[] pngBytes, string? suggestedName = null)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = suggestedName ?? SuggestedName("shot"),
                DefaultFileExtension = ".png",
            };
            picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
            picker.FileTypeChoices.Add("JPEG 图片", new List<string> { ".jpg", ".jpeg" });

            InitializeWithWindow.Initialize(picker, ownerHwnd);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                App.Log("Save: 用户取消");
                return null;
            }

            var isJpeg = file.FileType.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || file.FileType.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
            var bytes = isJpeg ? ToJpeg(pngBytes) : pngBytes;
            await FileIO.WriteBytesAsync(file, bytes);
            App.Log($"Save: {file.Path} ({bytes.Length} bytes, {(isJpeg ? "jpeg" : "png")})");
            return file.Path;
        }
        catch (Exception ex)
        {
            App.Log($"Save: 失败 {ex}");
            return null;
        }
    }

    private static byte[] ToJpeg(byte[] pngBytes)
    {
        using var input = new MemoryStream(pngBytes);
        using var image = Image.FromStream(input);
        using var output = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders()
            .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
        image.Save(output, encoder, parameters);
        return output.ToArray();
    }
}
