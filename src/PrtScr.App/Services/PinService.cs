using System.Collections.Concurrent;
using System.Drawing;
using Microsoft.UI.Dispatching;
using PrtScr.App.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PrtScr.App.Services;

/// <summary>贴图窗口可执行的跨线程命令（管理器 / 菜单通过 PostMessage 发）。</summary>
public enum PinCommand
{
    Focus,
    Hide,
    Show,
    ToggleLock,
    ToggleThrough,
    RestoreInteraction,
    Flash,
    Close,
}

/// <summary>管理器用的贴图快照；由贴图窗口在自己的线程更新，避免跨线程读字段。</summary>
public sealed record PinRecord(
    nint Hwnd,
    byte[] PngBytes,
    int PixelWidth,
    int PixelHeight,
    int X,
    int Y,
    int Width,
    int Height,
    double Opacity,
    bool Locked,
    bool ClickThrough,
    bool Hidden,
    string SourceScope,
    int NavIndex,
    int NavCount,
    DateTime CreatedAt);

/// <summary>
/// 从剪贴板读取图片并创建贴图（Snipaste 同逻辑：截图先落剪贴板，贴图再从剪贴板取）。
/// </summary>
public static class PinService
{
    private static readonly ConcurrentDictionary<nint, PinRecord> Registry = new();

    /// <summary>贴图出现、移动、缩放、切换图片或状态变化时触发；可能来自任意线程。</summary>
    public static event Action? PinsChanged;

    /// <summary>贴图右键菜单请求打开截图历史；由主窗口接管，先让贴图退居次层。</summary>
    public static Action<nint>? HistoryRequested { get; set; }

    public static IReadOnlyList<PinRecord> SnapshotPins()
        => Registry.Values.OrderBy(p => p.CreatedAt).ThenBy(p => p.Hwnd).ToList();

    public static void UpsertPin(PinRecord record)
    {
        Registry[record.Hwnd] = record;
        NotifyPinsChanged();
    }

    public static void RemovePin(nint hwnd)
    {
        if (Registry.TryRemove(hwnd, out _))
            NotifyPinsChanged();
    }

    public static void SendCommand(nint hwnd, PinCommand command)
    {
        if (hwnd == 0)
            return;
        _ = Windows.Win32.PInvoke.PostMessage(
            new Windows.Win32.Foundation.HWND(hwnd),
            NativePinWindow.WmAppPinCommand,
            new Windows.Win32.Foundation.WPARAM((nuint)command),
            0);
    }

    private static void NotifyPinsChanged()
    {
        try
        {
            PinsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log($"Pin: 管理器刷新失败 {ex.Message}");
        }
    }

    public static void RequestHistory(nint pinOwner)
    {
        try
        {
            HistoryRequested?.Invoke(pinOwner);
        }
        catch (Exception ex)
        {
            App.Log($"Pin: 打开历史失败 {ex.Message}");
        }
    }

    /// <summary>读取剪贴板里的图片字节（供 OCR 使用）。</summary>
    public static async Task<byte[]?> ReadClipboardImageBytesAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap))
                return null;

            var reference = await content.GetBitmapAsync();
            using var source = await reference.OpenReadAsync();
            var bytes = new byte[(int)source.Size];
            await source.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            return bytes;
        }
        catch (Exception ex)
        {
            App.Log($"读取剪贴板图片失败: {ex}");
            return null;
        }
    }

    public static async Task<bool> PasteFromClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap))
            {
                App.Log("Pin: 剪贴板里没有图片");
                return false;
            }

            var reference = await content.GetBitmapAsync();
            using var source = await reference.OpenReadAsync();
            var bytes = new byte[(int)source.Size];
            await source.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);

            var window = CreatePin(bytes);
            if (window is null)
                return false;

            // Alt+S 贴图没有分类上下文：左右点击按全部分类的最新顺序浏览。
            var all = CaptureHistoryService.Instance.GetItems();
            if (all.Count > 0)
                window.SetNavigation(new PinNavigation(all.Select(i => i.FilePath).ToList(), 0, "全部"));
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"Pin: 失败 {ex}");
            return false;
        }
    }

    /// <summary>贴图内左右点击浏览的列表（文件路径按显示顺序，Index 为当前项）。</summary>
    public sealed record PinNavigation(IReadOnlyList<string> Files, int Index, string Scope);

    /// <summary>用一张 PNG 直接创建贴图（截图历史等入口复用）。</summary>
    public static bool PastePng(byte[] bytes) => PastePng(bytes, edit: false);

    /// <summary>从截图历史贴图：左右点击只在当前分类内切换。</summary>
    public static bool PastePngWithNavigation(byte[] bytes, IReadOnlyList<string> files, int index, string scope)
    {
        try
        {
            var window = CreatePin(bytes, edit: false);
            if (window is null)
                return false;
            window.SetNavigation(new PinNavigation(files, index, scope));
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"Pin: 失败 {ex}");
            return false;
        }
    }

    /// <summary>裁剪生成的新贴图稍微错开，避免和原贴图完全重合。</summary>
    public static bool PastePngOffset(byte[] bytes, int offsetX, int offsetY)
    {
        try
        {
            return CreatePin(bytes, edit: false, offsetX, offsetY) is not null;
        }
        catch (Exception ex)
        {
            App.Log($"Pin: 失败 {ex}");
            return false;
        }
    }

    /// <summary>用一张 PNG 创建贴图并直接进入标注模式（截图历史「标注」入口）。</summary>
    public static bool PastePngAndEdit(byte[] bytes) => PastePng(bytes, edit: true);

    private static bool PastePng(byte[] bytes, bool edit)
    {
        try
        {
            return CreatePin(bytes, edit) is not null;
        }
        catch (Exception ex)
        {
            App.Log($"Pin: 失败 {ex}");
            return false;
        }
    }

    private static NativePinWindow? CreatePin(byte[] bytes, bool edit = false, int offsetX = 0, int offsetY = 0)
    {
        using var image = Image.FromStream(new MemoryStream(bytes));
        var pixelWidth = image.Width;
        var pixelHeight = image.Height;
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return null;

        var queue = DispatcherQueue.GetForCurrentThread();
        var window = NativePinWindow.TryCreate(bytes, pixelWidth, pixelHeight, queue, offsetX, offsetY);
        if (window is null)
        {
            App.Log("Pin: 创建贴图窗口失败");
            return null;
        }

        App.Log($"Pin: created {pixelWidth}x{pixelHeight}");
        if (edit)
            window.RequestEditMode();
        return window;
    }
}
