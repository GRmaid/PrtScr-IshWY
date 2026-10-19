namespace PrtScr.Ocr;

/// <summary>
/// OCR 门面：默认按“内置优先、可选引擎探测可用后启用”的顺序识别。
/// </summary>
public sealed class OcrService
{
    private readonly List<IOcrEngine> _engines;

    /// <param name="preferUmi">true 时优先 Umi-OCR（在线才生效），否则先用 Windows 内置 OCR。</param>
    public OcrService(bool preferUmi = false, int umiPort = 1224)
    {
        var windows = new WindowsOcrEngine();
        var umi = new UmiOcrClient(umiPort);
        _engines = preferUmi ? [umi, windows] : [windows, umi];
    }

    public IReadOnlyList<IOcrEngine> Engines => _engines;

    public async Task<OcrResult> RecognizeAsync(
        byte[] imageBytes, string? preferredEngineId = null, CancellationToken cancellationToken = default)
    {
        if (preferredEngineId is not null)
        {
            var preferred = _engines.FirstOrDefault(e => e.Id == preferredEngineId);
            if (preferred is not null)
            {
                var preferredResult = await preferred.RecognizeAsync(imageBytes, cancellationToken);
                if (preferredResult.Success)
                    return preferredResult;
            }
        }

        foreach (var engine in _engines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await engine.IsAvailableAsync())
                continue;

            var result = await engine.RecognizeAsync(imageBytes, cancellationToken);
            if (result.Success)
                return result;
        }

        return new OcrResult(false, "", Error: "没有可用的 OCR 引擎");
    }
}
