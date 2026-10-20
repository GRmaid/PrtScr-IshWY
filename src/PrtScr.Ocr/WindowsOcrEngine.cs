using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinOcr = Windows.Media.Ocr;

namespace PrtScr.Ocr;

/// <summary>
/// Windows 内置 OCR：零依赖、毫秒级，随系统语言包工作；中文精度一般，作为默认快速引擎。
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    public string Id => "windows";

    public string DisplayName => "Windows 内置 OCR";

    public static (bool Available, int LanguageCount) GetStatus()
    {
        try
        {
            var languages = WinOcr.OcrEngine.AvailableRecognizerLanguages;
            return (languages.Count > 0 && WinOcr.OcrEngine.TryCreateFromUserProfileLanguages() is not null, languages.Count);
        }
        catch
        {
            return (false, 0);
        }
    }

    public Task<bool> IsAvailableAsync() => Task.FromResult(GetStatus().Available);

    public async Task<OcrResult> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        WinOcr.OcrEngine? engine;
        try
        {
            engine = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex)
        {
            return new OcrResult(false, "", Error: ex.Message, Engine: Id);
        }

        if (engine is null)
            return new OcrResult(false, "", Error: "系统未安装 OCR 语言包（设置 → 时间和语言 → 语言）", Engine: Id);

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(stream);
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync();
            writer.DetachStream();
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);

            var text = new StringBuilder();
            foreach (var line in result.Lines)
                text.AppendLine(CleanCjkSpaces(line.Text));

            return new OcrResult(true, text.ToString().TrimEnd(), Engine: Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OcrResult(false, "", Error: ex.Message, Engine: Id);
        }
    }

    /// <summary>
    /// Windows OCR 会在汉字之间插入空格（"截 图 热 键"），去掉中日文字符两侧的空格；
    /// 英文单词之间的空格保留。
    /// </summary>
    private static string CleanCjkSpaces(string line)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        var sb = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == ' ')
            {
                var prev = i > 0 ? line[i - 1] : '\0';
                var next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (IsCjk(prev) || IsCjk(next) || IsCjkPunctuation(prev) || IsCjkPunctuation(next))
                    continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||
        (c >= 0x3400 && c <= 0x4DBF) ||
        (c >= 0xF900 && c <= 0xFAFF) ||
        (c >= 0x3040 && c <= 0x30FF);   // 日文假名

    private static bool IsCjkPunctuation(char c) =>
        "，。！？；：、（）《》「」『』【】…—“”‘’".IndexOf(c) >= 0;
}