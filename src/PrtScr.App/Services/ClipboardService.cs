using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace PrtScr.App.Services;

/// <summary>
/// 剪贴板写入（带重试）：其他程序短暂占用剪贴板时会抛 CLIPBRD_E_CANT_OPEN(0x800401D0)，
/// 之前只记失败日志导致偶尔粘贴不到；这里退避重试几次。
/// </summary>
public static class ClipboardService
{
    private const int MaxAttempts = 6;
    private const int RetryDelayMs = 80;

    public static async Task<bool> SetImageAsync(byte[] pngBytes)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                var stream = new InMemoryRandomAccessStream();
                var writer = new DataWriter(stream);
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                writer.DetachStream();
                stream.Seek(0);

                var package = new DataPackage();
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Clipboard.SetContent(package);
                Clipboard.Flush();
                return true;
            }
            catch (Exception ex)
            {
                App.Log($"Clipboard: 图片写入第 {attempt + 1} 次失败: {ex.Message}");
                await Task.Delay(RetryDelayMs);
            }
        }

        App.Log("Clipboard: 图片写入最终失败");
        return false;
    }

    public static async Task<bool> SetTextAsync(string text)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                Clipboard.Flush();
                return true;
            }
            catch (Exception ex)
            {
                App.Log($"Clipboard: 文本写入第 {attempt + 1} 次失败: {ex.Message}");
                await Task.Delay(RetryDelayMs);
            }
        }

        return false;
    }
}
