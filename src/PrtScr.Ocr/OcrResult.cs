namespace PrtScr.Ocr;

public sealed record OcrResult(bool Success, string Text, string? Error = null, string? Engine = null);
