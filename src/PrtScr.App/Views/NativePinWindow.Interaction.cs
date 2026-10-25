using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using PrtScr.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Views;

/// <summary>贴图窗口：滚轮缩放、吸附、菜单、裁剪与另存。</summary>
public sealed unsafe partial class NativePinWindow
{
    private void OnWheel(int delta, int screenX, int screenY)
    {
        if (delta == 0)
            return;
        if (_locked && !_editMode)
            return;
        if (_editMode)
        {
            // 标注模式下滚轮不缩放贴图；文字工具时用来调字号，和输入框里的滚轮一致。
            if (_toolId == 3)
                AdjustTextSize(delta > 0 ? TextSizeStep : -TextSizeStep);
            return;
        }

        var factor = delta > 0 ? 1.1 : 1 / 1.1;
        var maxZoom = Math.Min(8, Math.Min(
            _maxWinW / (double)Math.Max(1, _pixelWidth),
            _maxWinH / (double)Math.Max(1, _pixelHeight)));
        maxZoom = Math.Max(0.1, maxZoom);
        var newZoom = Math.Clamp(_zoom * factor, 0.1, maxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001)
            return;

        // 以鼠标指针为锚点缩放：记录指针在当前窗口里的归一化位置，
        // 尺寸变化后反推窗口左上角，让指针下的同一图像素留在指针下。
        // 之前只改 _winW/_winH 不动 _winX/_winY，所以永远从左上方固定缩放。
        var anchorX = Math.Clamp((screenX - _winX) / (double)Math.Max(1, _winW), 0, 1);
        var anchorY = Math.Clamp((screenY - _winY) / (double)Math.Max(1, _winH), 0, 1);

        _zoom = newZoom;
        _winW = Math.Max(16, (int)Math.Round(_pixelWidth * _zoom));
        _winH = Math.Max(16, (int)Math.Round(_pixelHeight * _zoom));
        _winX = (int)Math.Round(screenX - anchorX * _winW);
        _winY = (int)Math.Round(screenY - anchorY * _winH);

        // 不再先 MoveWindow 再 UpdateLayeredWindow：后者本身就会按 psize 调整窗口尺寸和位置。
        // 连续滚轮时每帧只提交一次分层表面，避免 DWM 为每个中间尺寸各留一张表面。
        // 交互期走占位边框；停手 350ms 后再高清精修（150ms 会在快速滚轮中途反复触发）。
        RenderAndPresent(highQuality: false);

        _settleTimer?.Dispose();
        _settleTimer = new System.Threading.Timer(_ =>
        {
            if (!_hwnd.IsNull)
                _ = PInvoke.PostMessage(_hwnd, WM_APP_ZOOM_SETTLED, default, default);
        }, null, 350, Timeout.Infinite);
    }

    private RECT CurrentWorkArea()
    {
        var monitor = new HMONITOR(MonitorFromWindow((nint)_hwnd.Value, 2 /* MONITOR_DEFAULTTONEAREST */));
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        unsafe
        {
            if (!PInvoke.GetMonitorInfo(monitor, &info))
                return _workArea;
        }
        return info.rcWork;
    }

    /// <summary>拖动时吸附到工作区边缘或其他贴图边缘，并在命中处显示 1px 参考线。</summary>
    private void ApplySnap()
    {
        var work = CurrentWorkArea();
        var threshold = Math.Max(3, (int)Math.Round(8 * Math.Max(1, _dpiScale)));
        var x = _winX;
        var y = _winY;
        int? guideX = null;
        int? guideY = null;
        var bestX = int.MaxValue;
        var bestY = int.MaxValue;

        void ConsiderX(int target, int guide, int distance)
        {
            if (distance <= threshold && distance < bestX)
            {
                bestX = distance;
                x = target;
                guideX = guide;
            }
        }

        void ConsiderY(int target, int guide, int distance)
        {
            if (distance <= threshold && distance < bestY)
            {
                bestY = distance;
                y = target;
                guideY = guide;
            }
        }

        ConsiderX(work.left, work.left, Math.Abs(_winX - work.left));
        ConsiderX(work.right - _winW, work.right, Math.Abs(_winX + _winW - work.right));
        ConsiderY(work.top, work.top, Math.Abs(_winY - work.top));
        ConsiderY(work.bottom - _winH, work.bottom, Math.Abs(_winY + _winH - work.bottom));

        foreach (var pin in PinService.SnapshotPins())
        {
            if (pin.Hwnd == (nint)_hwnd.Value || pin.Hidden)
                continue;
            ConsiderX(pin.X - _winW, pin.X, Math.Abs(_winX + _winW - pin.X));
            ConsiderX(pin.X + pin.Width, pin.X + pin.Width, Math.Abs(_winX - (pin.X + pin.Width)));
            ConsiderY(pin.Y - _winH, pin.Y, Math.Abs(_winY + _winH - pin.Y));
            ConsiderY(pin.Y + pin.Height, pin.Y + pin.Height, Math.Abs(_winY - (pin.Y + pin.Height)));
        }

        _winX = x;
        _winY = y;
        if (guideX.HasValue || guideY.HasValue)
            PinSnapGuide.Show(guideX, work.top, work.bottom, guideY, work.left, work.right);
        else
            PinSnapGuide.Hide();
    }

    private void ShowMenu()
    {
        App.Log("Pin: menu enter");
        GetCursorPos(out var pt);
        _ = PInvoke.SetForegroundWindow(_hwnd);

        // 菜单期间让 Esc 轮询让路：菜单线程自己处理 Esc 和点击外部。
        _menuOpen = true;
        int command;
        try
        {
            command = PinMenuWindow.Show((nint)_hwnd.Value, pt.x, pt.y, _opacity, _locked, _clickThrough);
        }
        finally
        {
            _menuOpen = false;
        }

        _ = PInvoke.PostMessage(_hwnd, WM_NULL, default, default);
        App.Log($"Pin: menu command={command}");

        switch (command)
        {
            case PinMenuWindow.CmdCopy:
                PinClipboardHelper.Copy(_pngBytes, _uiQueue);
                break;
            case PinMenuWindow.CmdEdit:
                EnterEditMode();
                break;
            case PinMenuWindow.CmdCrop:
                StartCrop();
                break;
            case PinMenuWindow.CmdOcr:
                App.Log("Pin: ocr menu clicked");
                PinClipboardHelper.OpenOcr(_pngBytes, _uiQueue, (nint)_hwnd.Value);
                break;
            case PinMenuWindow.CmdSaveAs:
                if (_editMode)
                    ApplyEdit(saveAs: true);
                else
                    SaveAs();
                break;
            case PinMenuWindow.CmdHistory:
                PinService.RequestHistory((nint)_hwnd.Value);
                break;
            case PinMenuWindow.CmdToggleLock:
                HandlePinCommand(PinCommand.ToggleLock);
                break;
            case PinMenuWindow.CmdToggleThrough:
                HandlePinCommand(PinCommand.ToggleThrough);
                break;
            case PinMenuWindow.CmdOpacity100:
                SetOpacity(1);
                break;
            case PinMenuWindow.CmdOpacity75:
                SetOpacity(0.75);
                break;
            case PinMenuWindow.CmdOpacity50:
                SetOpacity(0.5);
                break;
            case PinMenuWindow.CmdOpacity25:
                SetOpacity(0.25);
                break;
            case PinMenuWindow.CmdClose:
                _ = PInvoke.DestroyWindow(_hwnd);
                break;
        }
    }

    /// <summary>
    /// 裁剪：在贴图上方拖框，确认后按动作生成新贴图 / OCR / 保存，原贴图保留。
    /// 选区和原图立即返回，解码/裁剪/编码放到线程池，避免回车后贴图线程和 UI 一起卡住。
    /// </summary>
    private void StartCrop()
    {
        var result = PinCropWindow.Show((nint)_hwnd.Value, _winX, _winY, _winW, _winH,
            _pngBytes, _pixelWidth, _pixelHeight);
        if (result is null)
        {
            App.Log("Pin: crop cancelled");
            return;
        }

        App.Log($"Pin: crop {result.Action} {result.Width}x{result.Height} at {result.Left},{result.Top}");
        ThreadPool.QueueUserWorkItem(_ => ProcessCrop(result));
    }

    /// <summary>线程池里只做 GDI+ 解码/裁剪/PNG 编码，结果再回 UI 线程执行贴图/OCR/保存。</summary>
    private void ProcessCrop(PinCropResult result)
    {
        byte[]? bytes = null;
        try
        {
            var started = Environment.TickCount64;
            MemoryProbe.Log("crop-start");
            using var source = Image.FromStream(new MemoryStream(result.SourcePngBytes, writable: false));
            using var cropped = new Bitmap(result.Width, result.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cropped))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(source, new Rectangle(0, 0, cropped.Width, cropped.Height),
                    result.Left, result.Top, result.Width, result.Height, GraphicsUnit.Pixel);
            }
            using var stream = new MemoryStream();
            cropped.Save(stream, ImageFormat.Png);
            bytes = stream.ToArray();
            App.Log($"Pin crop: encoded {result.Width}x{result.Height} {bytes.Length / 1024}KB {Environment.TickCount64 - started}ms");
        }
        catch (Exception ex)
        {
            App.Log($"Pin crop: 生成失败 {ex}");
            return;
        }

        MemoryProbe.Log("crop-encoded");
        if (App.Config.Current.HistoryPinCrop)
            _ = CaptureHistoryService.Instance.AddAsync(bytes, HistoryCaptureMode.PinCrop);

        var hwnd = (nint)_hwnd.Value;
        _uiQueue.TryEnqueue(() =>
        {
            switch (result.Action)
            {
                case PinCropAction.Pin:
                    PinService.PastePngOffset(bytes, 24, 24);
                    break;
                case PinCropAction.Ocr:
                    PinClipboardHelper.OpenOcr(bytes, _uiQueue, hwnd);
                    break;
                case PinCropAction.Save:
                    _ = FileSaveService.SavePngAsync(hwnd, bytes, FileSaveService.SuggestedName("crop"));
                    break;
            }
            MemoryProbe.Log("crop-done");
        });
    }

    /// <summary>在当前贴图上打开系统另存为对话框。unsafe 类里不能写 async，因此只 fire-and-forget。</summary>
    private void SaveAs()
    {
        _ = PrtScr.App.Services.FileSaveService.SavePngAsync(
            (nint)_hwnd.Value, _pngBytes, PrtScr.App.Services.FileSaveService.SuggestedName("pin"));
    }

}
