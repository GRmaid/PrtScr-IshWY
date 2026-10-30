namespace PrtScr.Ocr;

/// <summary>
/// OCR 门面：默认只用 Windows 内置 OCR；显式启用 Umi-OCR 时才把它加入候选。
/// </summary>
public sealed class OcrService
{
    private readonly List<IOcrEngine> _engines;

    /// <param name="preferUmi">true 时优先 Umi-OCR（在线才生效，失败回退 Windows OCR）；false 时完全不使用、不探测 Umi-OCR。</param>
    public OcrService(bool preferUmi = false, int umiPort = 1224)
    {
        var windows = new WindowsOcrEngine();
        // 关闭 Umi 时连候选都不放入：识别失败不会回退到 Umi，也不会产生任何探测请求。
        _engines = preferUmi ? [new UmiOcrClient(umiPort), windows] : [windows];
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
