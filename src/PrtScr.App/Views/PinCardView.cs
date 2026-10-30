using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using PrtScr.App.Services;

namespace PrtScr.App.Views;

/// <summary>贴图管理器卡片：缩略图在创建时解码一次，避免 GridView 反复解码原图。</summary>
public sealed class PinCardView
{
    private PinCardView(PinRecord item, BitmapImage? thumbnail)
    {
        Item = item;
        Thumbnail = thumbnail;
        Caption = $"贴图 · {(string.IsNullOrWhiteSpace(item.SourceScope) ? "贴图" : item.SourceScope)}";
        Detail = $"{item.PixelWidth} × {item.PixelHeight} · 不透明度 {item.Opacity * 100:0}%";
        var states = new List<string>();
        if (item.Hidden) states.Add("已隐藏");
        if (item.Locked) states.Add("锁定");
        if (item.ClickThrough) states.Add("点击穿透");
        StateText = states.Count > 0 ? string.Join(" · ", states) : "可拖动 · 滚轮缩放";
        RestoreVisibility = item.Hidden || item.Locked || item.ClickThrough
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public PinRecord Item { get; }
    public BitmapImage? Thumbnail { get; }
    public string Caption { get; }
    public string Detail { get; }
    public string StateText { get; }
    public Visibility RestoreVisibility { get; }

    /// <summary>卡片按钮的无障碍名，带上是哪一张贴图，读屏时四张按钮不歧义。</summary>
    public string CopyCaption => "复制：" + Caption;

    /// <summary>另存按钮的无障碍名。</summary>
    public string SaveCaption => "另存：" + Caption;

    /// <summary>识别按钮的无障碍名。</summary>
    public string OcrCaption => "识别：" + Caption;

    /// <summary>关闭按钮的无障碍名。</summary>
    public string CloseCaption => "关闭：" + Caption;

    public static async Task<PinCardView> CreateAsync(PinRecord item)
    {
        BitmapImage? thumbnail = null;
        try
        {
            using var stream = new MemoryStream(item.PngBytes, writable: false);
            thumbnail = new BitmapImage { DecodePixelWidth = 360 };
            using var random = stream.AsRandomAccessStream();
            await thumbnail.SetSourceAsync(random);
        }
        catch (Exception ex)
        {
            App.Log($"Pin manager: 缩略图失败 {ex.Message}");
        }

        return new PinCardView(item, thumbnail);
    }
}
