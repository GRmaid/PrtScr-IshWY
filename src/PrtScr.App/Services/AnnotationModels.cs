using PrtScr.Core.Edit;

namespace PrtScr.App.Services;

public enum AnnotationTool
{
    Rectangle,
    Arrow,
    Pen,
    Text,
    Mosaic,
    Number,
}

public struct PointD
{
    public double X;
    public double Y;

    public PointD(double x, double y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>
/// 一条标注。所有坐标都是**原始图片像素**，与屏幕缩放无关。
/// </summary>
public sealed class Annotation : IEditNumbered
{
    public AnnotationTool Tool { get; init; }

    public List<PointD> Points { get; } = new();

    /// <summary>ARGB。</summary>
    public uint Color { get; set; } = 0xFFFF3B30;

    /// <summary>线宽 / 文字字号 / 序号半径（图片像素）。</summary>
    public float Size { get; set; } = 4;

    public string Text { get; set; } = "";

    public int Number { get; set; }

    /// <summary>马赛克的预览图（已打码的小图），仅用于编辑器显示。</summary>
    public Microsoft.UI.Xaml.Media.Imaging.BitmapSource? PreviewImage { get; set; }

    public PointD Start => Points.Count > 0 ? Points[0] : default;

    public PointD End => Points.Count > 0 ? Points[^1] : default;
}
