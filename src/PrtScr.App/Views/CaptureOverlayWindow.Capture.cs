using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using PrtScr.App.Services;
using PrtScr.Capture;
using PrtScr.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;

namespace PrtScr.App.Views;

/// <summary>冻屏取景覆盖窗：键盘/确认/取消、裁剪与保存流程。</summary>
public sealed partial class CaptureOverlayWindow
{
    /// <summary>低级键盘钩子回调（钩子线程）：只切回 UI 线程，绝不做其他事。</summary>
    private void OnOverlayHotkey(int vk, bool ctrl)
    {
        DispatcherQueue.TryEnqueue(() => HandleOverlayHotkey(vk, ctrl));
    }

    /// <summary>取景窗可见时的键盘直达，不依赖 XAML 焦点。</summary>
    private void HandleOverlayHotkey(int vk, bool ctrl)
    {
        if (_closed)
            return;

        switch (vk)
        {
            case 0x1B: // Esc
                Cancel();
                break;
            case 0x0D: // Enter
                _ = ConfirmCurrentTargetAsync();
                break;
            case 0x09: // Tab
                ToggleCaptureMode();
                break;
            case OverlayPrevWindowKey: // 取景器内：上一个窗口
                CycleHover(-1);
                break;
            case OverlayNextWindowKey: // 取景器内：下一个窗口
                CycleHover(1);
                break;
            case 0x4F: // O：OCR 选区
                if (_hasSelection)
                    _ = OcrSelectionAsync();
                break;
            case 0x43 when ctrl: // Ctrl+C
                if (_hasSelection)
                    _ = ConfirmAsync();
                else
                    _ = CopyLoupeColorAsync();
                break;
            case 0x53 when ctrl: // Ctrl+S
                if (_hasSelection)
                    _ = SaveSelectionAsync();
                break;
        }
    }

    /// <summary>焦点锚的键盘直达：Esc/回车/Tab 全部不依赖窗口焦点漂移。</summary>
    private void FocusSink_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                Cancel();
                break;

            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                _ = ConfirmCurrentTargetAsync();
                break;

            case Windows.System.VirtualKey.Tab:
                e.Handled = true;
                ToggleCaptureMode();
                break;
        }
    }

    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Cancel();
    }

    private void OnEnter(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = ConfirmCurrentTargetAsync();
    }

    // ---------- 收尾 ----------

    public void Cancel() => Dismiss();

    /// <summary>
    /// 确认截图时先把取景窗收掉：冻结图已经够用，没必要让遮罩/选区一直停在屏幕上
    /// 等裁剪、PNG 编码、写剪贴板和落历史——那会看起来像“回车卡住”。
    /// _closed 置位后重复按键不会再次进入。
    /// </summary>
    private void HideForCapture()
    {
        if (_closed)
            return;
        _closed = true;
        StopEscPolling();
        EscOverPinHook.SetOverlayKeysActive(false);
        AppWindow.Hide();
        try
        {
            HideColorLoupe();
            _toastTimer?.Stop();
            CopyToast.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.Log($"Overlay hide 异常: {ex.Message}");
        }
        App.Log("Overlay: hidden for capture");
    }

    /// <summary>捕获已经取完，释放冻结图并通知服务本次取景结束。</summary>
    private void FinishCaptureSession()
    {
        if (_sessionFinished)
            return;
        _sessionFinished = true;

        var before = GC.GetTotalMemory(false);
        try
        {
            Frozen.Source = null;
            _full.Dispose();
            _full = new Bitmap(1, 1);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay cleanup 异常: {ex.Message}");
        }

        // 2560×1600 的 WriteableBitmap + GDI Bitmap 各约 16 MB，PixelBuffer 会进大对象堆。
        // 但不能在 UI 线程上阻塞式 GC——那正是下一次截图“卡一下”的来源；放到后台延迟压缩。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180);
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                App.Log($"Overlay: frozen buffers released, managed={before / 1048576}->{GC.GetTotalMemory(false) / 1048576}MB");
            }
            catch (Exception ex)
            {
                App.Log($"Overlay GC 异常: {ex.Message}");
            }
        });

        Dismissed?.Invoke();
    }

    /// <summary>隐藏窗口（复用），并通知服务本次取景结束。</summary>
    private void Dismiss()
    {
        if (_closed)
            return;
        HideForCapture();
        FinishCaptureSession();
    }

    /// <summary>把选区按“窗口矩形 → 捕获位图”的比率映射到窗口图上并裁剪。</summary>
    private Bitmap? CropFromWindow(Bitmap windowBitmap, WindowInfo window)
    {
        var sel = _selection;
        if (sel.IsEmpty || windowBitmap.Width <= 0 || windowBitmap.Height <= 0)
            return null;

        var frame = window.FrameBounds;
        if (frame.Width <= 0 || frame.Height <= 0)
            return null;

        // 选区 DIP → 显示器本地物理 → 虚拟屏物理 → 窗口内相对坐标
        var vx0 = _monitor.X + sel.X * _scale;
        var vy0 = _monitor.Y + sel.Y * _scale;
        var vx1 = _monitor.X + (sel.X + sel.Width) * _scale;
        var vy1 = _monitor.Y + (sel.Y + sel.Height) * _scale;

        // 捕获位图与窗口矩形可能存在几像素边框差，按比率归一化
        var sx = windowBitmap.Width / (double)frame.Width;
        var sy = windowBitmap.Height / (double)frame.Height;

        var x0 = (int)Math.Round((vx0 - frame.X) * sx);
        var y0 = (int)Math.Round((vy0 - frame.Y) * sy);
        var x1 = (int)Math.Round((vx1 - frame.X) * sx);
        var y1 = (int)Math.Round((vy1 - frame.Y) * sy);

        x0 = Math.Clamp(x0, 0, windowBitmap.Width - 1);
        y0 = Math.Clamp(y0, 0, windowBitmap.Height - 1);
        x1 = Math.Clamp(x1, x0 + 1, windowBitmap.Width);
        y1 = Math.Clamp(y1, y0 + 1, windowBitmap.Height);

        var cropped = windowBitmap.Clone(
            new System.Drawing.Rectangle(x0, y0, x1 - x0, y1 - y0),
            PixelFormat.Format32bppArgb);
        App.Log($"Capture: 窗口局部 {x1 - x0}×{y1 - y0}（窗口 {frame.Width}×{frame.Height}，捕获图 {windowBitmap.Width}×{windowBitmap.Height}）");
        return cropped;
    }

    /// <summary>按当前选区/窗口取回一张位图（窗口模式抗遮挡；否则裁冻结画面）。调用方负责 Dispose。</summary>
    private async Task<Bitmap?> CaptureSelectionAsync()
    {
        // 常规截图：不重建任何窗口，直接裁冻结画面——菜单、悬停提示、下拉框、指针所见即所得
        if (_captureMode == CaptureMode.Normal)
            return CropFullScreenSelection();

        // 窗口局部：先抓完整窗口（抗遮挡），再按选区裁剪
        if (_subregionWindow is { } sub)
        {
            var fullWindow = await WindowCapture.CaptureAsync(sub);
            if (fullWindow is not null)
            {
                var cropped = CropFromWindow(fullWindow, sub);
                fullWindow.Dispose();
                if (cropped is not null)
                    return cropped;
            }
            App.Log("Capture: 窗口局部捕获失败，回退屏幕裁剪");
        }

        // 窗口模式：PrintWindow 直接取窗口自身内容（抗遮挡，被挡住的部分也完整），
        // 失败（不支持/全黑）则回退 WGC
        if (_clickedWindow is { } win)
        {
            var windowBitmap = await WindowCapture.CaptureAsync(win);
            if (windowBitmap is not null)
                return windowBitmap;
        }

        return CropFullScreenSelection();
    }

    /// <summary>按选区从冻结画面裁切（DIP → 显示器本地物理坐标，夹在显示器内）。</summary>
    private Bitmap? CropFullScreenSelection()
    {
        var dip = _selection;
        if (dip.IsEmpty)
            return null;

        var x = Math.Clamp((int)Math.Round(dip.X * _scale), 0, _monitor.Width - 1);
        var y = Math.Clamp((int)Math.Round(dip.Y * _scale), 0, _monitor.Height - 1);
        var right = Math.Clamp((int)Math.Round((dip.X + dip.Width) * _scale), x + 1, _monitor.Width);
        var bottom = Math.Clamp((int)Math.Round((dip.Y + dip.Height) * _scale), y + 1, _monitor.Height);

        return _full.Clone(
            new System.Drawing.Rectangle(x, y, right - x, bottom - y),
            PixelFormat.Format32bppArgb);
    }

    /// <summary>对当前选区做 OCR（O 键）。</summary>
    private async Task OcrSelectionAsync()
    {
        Bitmap? captured = null;
        try
        {
            captured = await CaptureSelectionAsync();
            if (captured is null)
                return;

            using var ms = new MemoryStream();
            captured.Save(ms, ImageFormat.Png);
            var bytes = ms.ToArray();

            Dismiss();
            await PrtScr.App.Services.OcrUiService.RecognizeAndShowAsync(bytes);
        }
        catch (Exception ex)
        {
            App.Log($"OCR 选区失败: {ex}");
        }
        finally
        {
            captured?.Dispose();
        }
    }

    /// <summary>
    /// 回车确认当前目标：已有选区走原确认流程；只有滚轮/悬停高亮时，
    /// 把高亮窗口当作整窗选区再确认，这样滚轮选完不必再点一下。
    /// </summary>
    private async Task ConfirmCurrentTargetAsync()
    {
        if (_closed || _confirming)
            return;

        _confirming = true;
        try
        {
            if (!_hasSelection)
            {
                if (_hoverWindow is null)
                    return;

                _clickedWindow = _hoverWindow;
                _selection = DipRect(_clickedWindow.FrameBounds);
                _hasSelection = true;
                _subregionWindow = null;
                // 不在这里 UpdateVisuals：回车是“直接截当前高亮窗口”，
                // 若先画选区边框/遮罩，窗口捕获的几十毫秒里主人会看到亮框先缩到窗口再截图。
                // 保持原有悬停高亮，等 ConfirmAsync 捕获完直接 Dismiss。
                App.Log($"Overlay: enter captures hovered window 0x{_clickedWindow.Handle:X} process=\"{_clickedWindow.ProcessName}\"");
            }

            await ConfirmAsync();
        }
        finally
        {
            _confirming = false;
        }
    }

    private async Task ConfirmAsync()
    {
        Bitmap? captured = null;
        var ended = false;
        try
        {
            // 先藏窗再取图：窗口捕获/PNG 编码期间主人不会再看到遮罩和选区。
            HideForCapture();
            captured = await CaptureSelectionAsync();

            // 图已经拿到，冻结缓冲可以释放，取景服务也立即进入空闲状态；
            // 后面的编码/剪贴板/历史都在后台，不挡下一次热键。
            FinishCaptureSession();
            ended = true;

            if (captured is null)
            {
                PrtScr.App.Services.ToastService.ShowCaptureFailure();
                return;
            }

            var bytes = await Task.Run(() => EncodePng(captured));

            var target = ResolveHistoryTarget();
            var copyTask = PrtScr.App.Services.ClipboardService.SetImageAsync(bytes);
            var historyTask = PrtScr.App.Services.CaptureHistoryService.Instance.AddAsync(
                bytes, target.Mode, target.ProcessName, target.WindowTitle);
            await Task.WhenAll(copyTask, historyTask);

            var ok = copyTask.Result;
            App.Log(ok ? "Capture: copied" : "Capture: clipboard final failure");
            if (historyTask.Result is { } savedPath)
                App.Log($"Capture: history saved {savedPath}");
            if (ok)
                PrtScr.App.Services.ToastService.ShowCaptureSuccess();
            else
                PrtScr.App.Services.ToastService.ShowCaptureFailure();
        }
        catch (Exception ex)
        {
            App.Log($"截图确认失败: {ex}");
            PrtScr.App.Services.ToastService.ShowCaptureFailure();
        }
        finally
        {
            captured?.Dispose();
            if (!ended)
                FinishCaptureSession();
        }
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private void Capture_Click(object sender, RoutedEventArgs e) => _ = ConfirmAsync();

    private void CancelCapture_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (_hasSelection)
            _ = OcrSelectionAsync();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveSelectionAsync();

    private void OnSaveShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_hasSelection)
            _ = SaveSelectionAsync();
    }

    /// <summary>选区另存为 PNG/JPEG；保存成功后同样复制到剪贴板并写入历史，再关闭取景。</summary>
    private async Task SaveSelectionAsync()
    {
        if (!_hasSelection || _closed || _saving)
            return;

        _saving = true;
        // 文件选择器会抢前台：期间暂停取景器按键，避免 Esc/Tab/回车被钩子当成取景操作。
        _keysSuspended = true;
        EscOverPinHook.SetOverlayKeysActive(false);
        Bitmap? captured = null;
        try
        {
            captured = await CaptureSelectionAsync();
            if (captured is null)
            {
                PrtScr.App.Services.ToastService.ShowCaptureFailure();
                return;
            }

            using var ms = new MemoryStream();
            captured.Save(ms, ImageFormat.Png);
            var bytes = ms.ToArray();

            var path = await PrtScr.App.Services.FileSaveService.SavePngAsync(
                WindowNative.GetWindowHandle(this), bytes, PrtScr.App.Services.FileSaveService.SuggestedName("shot"));
            if (path is null)
            {
                FocusSink.Focus(FocusState.Programmatic);
                return;
            }

            // 另存为不是“只写文件”：保存成功后仍要进剪贴板和截图历史，保持与回车确认一致
            var target = ResolveHistoryTarget();
            var copyTask = PrtScr.App.Services.ClipboardService.SetImageAsync(bytes);
            var historyTask = PrtScr.App.Services.CaptureHistoryService.Instance.AddAsync(
                bytes, target.Mode, target.ProcessName, target.WindowTitle);
            await Task.WhenAll(copyTask, historyTask);

            var copied = copyTask.Result;
            App.Log(copied ? "Capture: copied" : "Capture: clipboard final failure");
            App.Log($"Capture: saved as {path}");
            if (historyTask.Result is { } historyPath)
                App.Log($"Capture: history saved {historyPath}");
            if (copied)
                PrtScr.App.Services.ToastService.ShowCaptureSuccess();
            else
                PrtScr.App.Services.ToastService.ShowCaptureFailure();
            Dismiss();
        }
        catch (Exception ex)
        {
            App.Log($"另存为失败: {ex}");
            PrtScr.App.Services.ToastService.ShowCaptureFailure();
        }
        finally
        {
            captured?.Dispose();
            _saving = false;
            _keysSuspended = false;
            if (!_closed)
            {
                EscOverPinHook.SetOverlayKeysActive(true);
                FocusSink.Focus(FocusState.Programmatic);
            }
        }
    }

    /// <summary>
    /// 解析本次截图应归入的历史分类：窗口模式且确实落在某个窗口上时按进程归类；
    /// 常规模式、或窗口模式下自由拖拽没命中窗口（实际裁的是冻屏），都算常规截图。
    /// </summary>
    private (HistoryCaptureMode Mode, string ProcessName, string WindowTitle) ResolveHistoryTarget()
    {
        if (_captureMode == CaptureMode.Normal)
            return (HistoryCaptureMode.Normal, string.Empty, string.Empty);

        var target = _subregionWindow ?? _clickedWindow;
        if (target is null)
            return (HistoryCaptureMode.Normal, string.Empty, string.Empty);

        return (HistoryCaptureMode.Window, target.ProcessName, target.Title);
    }

}
