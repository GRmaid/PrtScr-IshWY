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

/// <summary>贴图入口结果状态；主窗口据此决定状态栏与提示文案。</summary>
public enum PinPasteStatus
{
    Success,
    ClipboardEmpty,
    HistoryEmpty,
    Disabled,
    Failed,
}

/// <summary>一次贴图尝试的结果；Success 为 true 时贴图窗口已经创建。</summary>
public sealed record PinPasteResult(PinPasteStatus Status, string Message)
{
    public bool Success => Status == PinPasteStatus.Success;
}

/// <summary>
/// 贴图入口：按设置从剪贴板（图片/文字）或截图历史里取内容创建贴图。
/// 文字会先渲染成 PNG 卡片，因此标注、裁剪、另存为和 OCR 都能复用同一套贴图窗口。
/// </summary>
public static class PinService
{
    public const string BehaviorClipboard = PinBehaviorRules.Clipboard;
    public const string BehaviorLatest = PinBehaviorRules.Latest;
    public const string BehaviorText = PinBehaviorRules.Text;
    public const string BehaviorOff = PinBehaviorRules.Off;

    private static readonly ConcurrentDictionary<nint, PinRecord> Registry = new();

    /// <summary>贴图出现、关闭、切换图片、编辑完成或状态变化时触发；移动/缩放不触发；可能来自任意线程。</summary>
    public static event Action? PinsChanged;

    /// <summary>贴图右键菜单请求打开截图历史；由主窗口接管，先让贴图退居次层。</summary>
    public static Action<nint>? HistoryRequested { get; set; }

    public static IReadOnlyList<PinRecord> SnapshotPins()
        => Registry.Values.OrderBy(p => p.CreatedAt).ThenBy(p => p.Hwnd).ToList();

    public static void UpsertPin(PinRecord record, bool notify = true)
    {
        Registry[record.Hwnd] = record;
        if (notify)
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

    /// <summary>把设置里的贴图行为归一化成四种已知模式。</summary>
    public static string NormalizeBehavior(string? value) => PinBehaviorRules.Normalize(value);

    /// <summary>设置下拉框 / 状态栏用的中文名。</summary>
    public static string DescribeBehavior(string? value) => PinBehaviorRules.Describe(value);

    /// <summary>首页贴图卡片与托盘菜单用的短说明。</summary>
    public static string DescribeBehaviorTarget(string? value) => PinBehaviorRules.DescribeTarget(value);

    /// <summary>贴图热键与托盘入口统一走这里：按设置决定取剪贴板还是最新截图。</summary>
    public static async Task<PinPasteResult> PasteByBehaviorAsync()
    {
        var behavior = NormalizeBehavior(App.Config.Current.PinBehavior);
        var queue = DispatcherQueue.GetForCurrentThread();

        try
        {
            return behavior switch
            {
                BehaviorLatest => await PasteLatestHistoryAsync(queue),
                BehaviorText => await PasteClipboardTextAsync(queue),
                BehaviorOff => new PinPasteResult(PinPasteStatus.Disabled,
                    "贴图已关闭。可在设置里的「贴图行为」重新打开。"),
                _ => await PasteClipboardPreferredAsync(queue),
            };
        }
        catch (Exception ex)
        {
            App.Log($"Pin: 入口失败 {ex}");
            return new PinPasteResult(PinPasteStatus.Failed, "贴图失败，请查看日志。");
        }
    }

    /// <summary>预览或外部入口直接把一段文字贴成卡片。</summary>
    public static PinPasteResult PasteText(string text)
    {
        try
        {
            return CreateTextPin(text, DispatcherQueue.GetForCurrentThread());
        }
        catch (Exception ex)
        {
            App.Log($"Pin: 文字贴图失败 {ex}");
            return new PinPasteResult(PinPasteStatus.Failed, "文字贴图失败，请查看日志。");
        }
    }

    private static async Task<PinPasteResult> PasteClipboardPreferredAsync(DispatcherQueue? queue)
    {
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.Bitmap))
        {
            var bytes = await ReadBitmapAsync(content);
            if (bytes is null || bytes.Length == 0)
                return new PinPasteResult(PinPasteStatus.Failed, "剪贴板图片读取失败，请重试。");

            var window = CreatePin(bytes, queue: queue);
            if (window is null)
                return new PinPasteResult(PinPasteStatus.Failed, "贴图窗口创建失败，请查看日志。");

            AttachHistoryNavigation(window);
            return new PinPasteResult(PinPasteStatus.Success, "已贴出剪贴板图片。");
        }

        if (content.Contains(StandardDataFormats.Text))
            return CreateTextPin(await content.GetTextAsync(), queue);

        return new PinPasteResult(PinPasteStatus.ClipboardEmpty,
            "剪贴板里没有图片或文字可贴（也可以把贴图行为改成「只贴最新截图」）。");
    }

    private static async Task<PinPasteResult> PasteClipboardTextAsync(DispatcherQueue? queue)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text))
            return new PinPasteResult(PinPasteStatus.ClipboardEmpty,
                "剪贴板里没有文字可贴；当前是「只贴剪贴板文字」。");

        return CreateTextPin(await content.GetTextAsync(), queue);
    }

    private static async Task<PinPasteResult> PasteLatestHistoryAsync(DispatcherQueue? queue)
    {
        var all = CaptureHistoryService.Instance.GetItems();
        if (all.Count == 0)
            return new PinPasteResult(PinPasteStatus.HistoryEmpty,
                "截图历史里没有可贴的最新截图（先截一张图，或检查「保存截图历史」是否关闭）。");

        var bytes = await CaptureHistoryService.Instance.ReadAsync(all[0]);
        if (bytes is null || bytes.Length == 0)
            return new PinPasteResult(PinPasteStatus.Failed, "最新截图读取失败，请查看日志。");

        var window = CreatePin(bytes, queue: queue);
        if (window is null)
            return new PinPasteResult(PinPasteStatus.Failed, "贴图窗口创建失败，请查看日志。");

        // 只贴最新截图时仍允许在贴图里左右切换，从最新一张开始按全部历史浏览。
        window.SetNavigation(new PinNavigation(all.Select(i => i.FilePath).ToList(), 0, "全部"));
        return new PinPasteResult(PinPasteStatus.Success, "已贴出最新截图。");
    }

    private static PinPasteResult CreateTextPin(string? text, DispatcherQueue? queue)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new PinPasteResult(PinPasteStatus.ClipboardEmpty, "剪贴板里没有文字可贴。");

        var png = PinTextCardRenderer.Render(text, App.Config.Current.PinTextBackground, out _, out _);
        if (png is null || png.Length == 0)
            return new PinPasteResult(PinPasteStatus.Failed, "文字卡片渲染失败，请查看日志。");

        var window = CreatePin(png, queue: queue);
        if (window is null)
            return new PinPasteResult(PinPasteStatus.Failed, "贴图窗口创建失败，请查看日志。");

        window.SetSourceScope("文字");
        return new PinPasteResult(PinPasteStatus.Success, "已贴出文字卡片。");
    }

    private static async Task<byte[]?> ReadBitmapAsync(DataPackageView content)
    {
        var reference = await content.GetBitmapAsync();
        using var source = await reference.OpenReadAsync();
        var length = (int)source.Size;
        if (length <= 0)
            return null;

        var bytes = new byte[length];
        await source.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    private static void AttachHistoryNavigation(NativePinWindow window)
    {
        // Alt+S 贴图没有分类上下文：左右点击按全部分类的最新顺序浏览。
        var all = CaptureHistoryService.Instance.GetItems();
        if (all.Count > 0)
            window.SetNavigation(new PinNavigation(all.Select(i => i.FilePath).ToList(), 0, "全部"));
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

    private static NativePinWindow? CreatePin(
        byte[] bytes,
        bool edit = false,
        int offsetX = 0,
        int offsetY = 0,
        DispatcherQueue? queue = null)
    {
        using var image = Image.FromStream(new MemoryStream(bytes));
        var pixelWidth = image.Width;
        var pixelHeight = image.Height;
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return null;

        queue ??= DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            App.Log("Pin: 当前线程没有 DispatcherQueue，无法创建贴图");
            return null;
        }

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
