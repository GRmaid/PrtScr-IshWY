using System.Drawing.Imaging;
using PrtScr.App.Services;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace PrtScr.App.Views;

/// <summary>出口取图状态。</summary>
internal enum PinExportStatus
{
    /// <summary>贴图不在标注态，返回的就是已提交图片。</summary>
    NotEditing,

    /// <summary>标注态，返回「原图 + 当前标注」的合成 PNG。</summary>
    Composed,

    /// <summary>贴图线程未响应或合成失败；调用方必须报错，不得退回标注前的旧图。</summary>
    Unavailable,
}

/// <summary>一张贴图当前应被导出的 PNG（见 <see cref="NativePinWindow.TryGetCurrentPng"/>）。</summary>
/// <param name="Status">取图结果分类。</param>
/// <param name="Bytes">PNG 字节；<see cref="PinExportStatus.Unavailable"/> 时为 null。</param>
internal readonly record struct PinExportResult(PinExportStatus Status, byte[]? Bytes);

/// <summary>
/// 贴图编辑出口：复制 / 识别 / 裁剪 / 管理器一律从这里取图。
/// 标注态必须交出「原图 + 当前标注」的合成结果，不能读标注前的 <c>_pngBytes</c>（B01）；
/// 合成失败只报告失败，不替换已提交图片、不退出标注（B03）。
/// </summary>
public sealed unsafe partial class NativePinWindow
{
    private const uint WM_APP_EXPORT_PNG = 0x8000 + 33;
    private const uint WM_APP_COPY_OK = 0x8000 + 34;
    private const uint WM_APP_COPY_FAILED = 0x8000 + 35;

    /// <summary>外部线程（管理器）等贴图线程合成的最长时间；超时按不可用处理。</summary>
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMilliseconds(1000);

    private sealed class ExportRequest
    {
        public readonly TaskCompletionSource<byte[]?> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _exportLock = new();
    private ExportRequest? _exportRequest;

    /// <summary>
    /// 供管理器等外部线程调用：取这张贴图当前应被导出的 PNG。
    /// 标注态会切到贴图线程合成（最多等 <see cref="ExportTimeout"/>）；失败返回
    /// <see cref="PinExportStatus.Unavailable"/>，调用方必须报错而不是拿标注前的旧图。
    /// </summary>
    internal static PinExportResult TryGetCurrentPng(nint hwnd)
    {
        NativePinWindow? pin;
        lock (ClassLock)
            ByHwnd.TryGetValue(hwnd, out pin);

        if (pin is null)
            return new PinExportResult(PinExportStatus.Unavailable, null);

        // 非标注态：_pngBytes 就是最新已提交图片，无需跨线程。
        if (!pin._editMode)
            return new PinExportResult(PinExportStatus.NotEditing, pin._pngBytes);

        return pin.RequestCurrentPng();
    }

    private PinExportResult RequestCurrentPng()
    {
        var request = new ExportRequest();
        lock (_exportLock)
        {
            // 同一张贴图同时只接受一个取图请求，避免多个等待者互相覆盖。
            if (_exportRequest is not null)
                return new PinExportResult(PinExportStatus.Unavailable, null);
            _exportRequest = request;
        }

        if (!PInvoke.PostMessage(_hwnd, WM_APP_EXPORT_PNG, default, default))
        {
            ClearExportRequest(request);
            App.Log("Pin export: 投递取图请求失败");
            return new PinExportResult(PinExportStatus.Unavailable, null);
        }

        if (!request.Completion.Task.Wait(ExportTimeout))
        {
            ClearExportRequest(request);
            App.Log("Pin export: 贴图线程未在超时内响应");
            return new PinExportResult(PinExportStatus.Unavailable, null);
        }

        var bytes = request.Completion.Task.Result;
        return bytes is null
            ? new PinExportResult(PinExportStatus.Unavailable, null)
            : new PinExportResult(PinExportStatus.Composed, bytes);
    }

    private void ClearExportRequest(ExportRequest request)
    {
        lock (_exportLock)
        {
            if (ReferenceEquals(_exportRequest, request))
                _exportRequest = null;
        }
    }

    /// <summary>贴图线程收到 <see cref="WM_APP_EXPORT_PNG"/>：就地合成，不阻塞消息循环之外的东西。</summary>
    private void CompleteExportRequest()
    {
        ExportRequest? request;
        lock (_exportLock)
        {
            request = _exportRequest;
            _exportRequest = null;
        }

        if (request is null)
            return;

        request.Completion.TrySetResult(TryComposeCurrentPng());
    }

    /// <summary>
    /// 唯一的出口取图：非标注态返回已提交 PNG；标注态把当前标注（含正在画的一笔）烘焙进原图。
    /// 失败返回 null，调用方必须保留草稿与标注状态。
    /// </summary>
    private byte[]? TryComposeCurrentPng()
    {
        if (!_editMode)
            return _pngBytes;

        try
        {
            using var full = DecodeSource();
            if (full is null)
            {
                App.Log("Pin export: 源图解码失败");
                return null;
            }

            // 正在画的一笔也算「当前画面」，保证看到什么就复制什么。
            IReadOnlyList<Annotation> annotations = _draft is null
                ? _annotations
                : [.. _annotations, _draft];

            using var rendered = AnnotationRenderer.Render(full.Image, annotations);
            using var stream = new MemoryStream();
            rendered.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            App.Log($"Pin export: 合成当前标注失败 {ex}");
            return null;
        }
    }

    /// <summary>Ctrl+C / 菜单复制：复制当前画面（标注态含未提交标注），不退出标注。</summary>
    private void CopyCurrentPng()
    {
        var bytes = TryComposeCurrentPng();
        if (bytes is null)
        {
            ShowHint(_editMode ? "复制失败：标注合成失败，编辑内容已保留" : "复制失败：图片解码失败");
            return;
        }

        if (_editMode)
            App.Log("Pin: edit copy (composite)");

        PinClipboardHelper.Copy(bytes, _uiQueue, PostCopyResult);
    }

    /// <summary>剪贴板写入结果回到贴图线程，用底部提示条给出可见反馈。</summary>
    private void PostCopyResult(bool ok)
    {
        if (!_hwnd.IsNull)
            _ = PInvoke.PostMessage(_hwnd, ok ? WM_APP_COPY_OK : WM_APP_COPY_FAILED, default, default);
    }
}
