using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PrtScr.App.Services;

/// <summary>提示窗的材质与底色：设置页编辑器预览和真实提示窗共用同一套取值。</summary>
public static class ToastAppearance
{
    public const double MinOpacity = 0.35;
    public const double MaxOpacity = 1.0;

    public static string NormalizeMaterial(string? material) => material?.Trim().ToLowerInvariant() switch
    {
        "solid" => "solid",
        "mica" => "mica",
        "liquid" => "liquid",
        _ => "acrylic",
    };

    public static double ClampOpacity(double opacity) =>
        double.IsNaN(opacity) ? 0.9 : Math.Clamp(opacity, MinOpacity, MaxOpacity);

    /// <summary>提示面板底色；纯色与截屏失败时的兜底。真实材质由 ToastBackdrop 合成。</summary>
    public static Brush CreatePanelBrush(string? material, double opacity)
    {
        var normalized = NormalizeMaterial(material);
        var o = ClampOpacity(opacity);
        if (normalized == "solid")
        {
            return new SolidColorBrush(Color.FromArgb(Alpha(o), 0x20, 0x20, 0x20));
        }

        var start = normalized switch
        {
            "mica" => Color.FromArgb(Alpha(0.66 * o), 0x1C, 0x20, 0x26),
            "liquid" => Color.FromArgb(Alpha(0.42 * o), 0x30, 0x34, 0x44),
            _ => Color.FromArgb(Alpha(0.56 * o), 0x20, 0x20, 0x20),
        };
        var middle = normalized switch
        {
            "liquid" => Color.FromArgb(Alpha(0.20 * o), 0x9A, 0xC8, 0xFF),
            "mica" => Color.FromArgb(Alpha(0.44 * o), 0x26, 0x2C, 0x36),
            _ => Color.FromArgb(Alpha(0.30 * o), 0x1A, 0x1E, 0x26),
        };
        var end = normalized switch
        {
            "liquid" => Color.FromArgb(Alpha(0.54 * o), 0x0A, 0x3A, 0x55),
            "mica" => Color.FromArgb(Alpha(0.64 * o), 0x16, 0x1A, 0x22),
            _ => Color.FromArgb(Alpha(0.50 * o), 0x15, 0x18, 0x1F),
        };

        return new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = start, Offset = 0 },
                new GradientStop { Color = middle, Offset = 0.48 },
                new GradientStop { Color = end, Offset = 1 },
            },
        };
    }

    public static string MaterialLabel(string? material) => NormalizeMaterial(material) switch
    {
        "solid" => "纯色",
        "mica" => "云母",
        "liquid" => "液态玻璃",
        _ => "亚克力",
    };

    private static byte Alpha(double factor) =>
        (byte)Math.Clamp((int)Math.Round(255 * factor), 0, 255);
}
