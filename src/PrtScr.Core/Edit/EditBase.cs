namespace PrtScr.Core.Edit;

/// <summary>
/// 编辑基底（不可变快照）：提交后的 PNG 字节、像素尺寸与版本号。
/// <see cref="PngBytes"/> 的所有权交给文档，构造后调用方不得再改写数组内容。
/// </summary>
public sealed record EditBase
{
    public EditBase(byte[] pngBytes, int pixelWidth, int pixelHeight, EditRevision revision)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        PngBytes = pngBytes;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Revision = revision;
    }

    /// <summary>基底 PNG 字节；只读使用，不在原地修改。</summary>
    public byte[] PngBytes { get; }

    /// <summary>基底原始像素宽度（与屏幕缩放无关）。</summary>
    public int PixelWidth { get; }

    /// <summary>基底原始像素高度（与屏幕缩放无关）。</summary>
    public int PixelHeight { get; }

    /// <summary>基底版本号；每次提交成功加一。</summary>
    public EditRevision Revision { get; }
}
