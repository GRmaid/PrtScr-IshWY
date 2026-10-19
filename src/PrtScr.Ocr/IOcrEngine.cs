namespace PrtScr.Ocr;

/// <summary>
/// OCR 引擎抽象。引擎不可用时 IsAvailableAsync 返回 false，调用方跳过即可——
/// 这是“Umi-OCR 可选、没装就跳过”的关键约束。
/// </summary>
public interface IOcrEngine
{
    string Id { get; }

    string DisplayName { get; }

    Task<bool> IsAvailableAsync();

    Task<OcrResult> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken = default);
}
