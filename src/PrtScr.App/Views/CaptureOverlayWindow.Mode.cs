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

/// <summary>冻屏取景覆盖窗：模式切换与窗口包含判断。</summary>
public sealed partial class CaptureOverlayWindow
{
    /// <summary>Tab：窗口截图 ↔ 常规截图。两种模式共用同一套框选与确认流程，只换取图来源。</summary>
    private void ToggleCaptureMode()
    {
        try
        {
            var hadSelection = _hasSelection;
            var previousSelection = _selection;
            var previousMode = _captureMode;
            _captureMode = _captureMode == CaptureMode.Window ? CaptureMode.Normal : CaptureMode.Window;

            _dragging = false;
            _dragMoved = false;
            _movingSelection = false;
            _moveWholeWindow = false;
            _resizingSelection = false;
            _resizeEdge = ResizeEdge.None;
            _wheelAccum = 0;
            _lastTipWindow = null;
            HideColorLoupe();

            if (!hadSelection)
            {
                // 没有选区时沿用旧行为：清空窗口引用，光标下重新吸附。
                _hasSelection = false;
                _selection = Rect.Empty;
                _hoverWindow = null;
                _clickedWindow = null;
                _subregionCandidate = null;
                _subregionWindow = null;
                _hoverCandidates.Clear();
                RefreshHoverFromCursor();
                UpdateVisuals();
                ShowModeToast($"已切换到{ModeName()}");
                App.Log($"Overlay: mode={(ModeName())}");
                return;
            }

            _hasSelection = true;
            _selection = previousSelection;

            if (_captureMode == CaptureMode.Normal)
            {
                // 窗口 -> 常规：保留原选区，直接按冻结画面裁切。
                _hoverWindow = null;
                _clickedWindow = null;
                _subregionCandidate = null;
                _subregionWindow = null;
                _hoverCandidates.Clear();
                UpdateVisuals();
                ShowModeToast("已切换到常规截图，选区保留");
                App.Log("Overlay: mode=normal (selection preserved)");
                return;
            }

            // 常规 -> 窗口：找能完整包住当前选区的最小窗口，用窗口本体重建后再按同一选区裁切。
            // 跨窗口或没有窗口能包容时保持常规模式，宁可少切也不裁错。
            var target = FindContainingWindow(_selection);
            if (target is null)
            {
                _captureMode = CaptureMode.Normal;
                _hoverWindow = null;
                _clickedWindow = null;
                _subregionCandidate = null;
                _subregionWindow = null;
                _hoverCandidates.Clear();
                UpdateVisuals();
                ShowModeToast("没有窗口能完整包住选区，保持常规截图");
                App.Log("Overlay: mode=normal (no containing window, selection preserved)");
                return;
            }

            var frame = DipRect(target.FrameBounds);
            _clickedWindow = target;
            _hoverWindow = target;
            _subregionCandidate = null;
            _subregionWindow = NearlyEquals(frame, _selection, 2) ? null : target;
            _hoverCandidates.Clear();
            UpdateVisuals();
            ShowModeToast("已切换到窗口截图，选区保留");
            App.Log($"Overlay: mode=window (selection preserved, title={target.Title}, local={(_subregionWindow is not null)})");
        }
        catch (Exception ex)
        {
            App.Log($"Overlay Tab 切换异常: {ex.Message}");
        }
    }

    private string ModeName() => _captureMode == CaptureMode.Window ? "窗口截图" : "常规截图";

    /// <summary>在候选窗口里找完整包住选区的最小窗口；排除自身、全屏点击穿透层与隐藏窗口。</summary>
    private WindowInfo? FindContainingWindow(Rect selection)
    {
        WindowInfo? best = null;
        var bestArea = double.MaxValue;
        foreach (var w in _windows)
        {
            if (w.Handle == _selfHwnd || w.IsCloaked || w.FrameBounds.Width <= 0 || w.FrameBounds.Height <= 0)
                continue;
            if (IsFullScreenClickThrough(w))
                continue;

            var frame = DipRect(w.FrameBounds);
            const double tolerance = 0.5;
            if (frame.X > selection.X + tolerance ||
                frame.Y > selection.Y + tolerance ||
                frame.X + frame.Width < selection.X + selection.Width - tolerance ||
                frame.Y + frame.Height < selection.Y + selection.Height - tolerance)
                continue;

            var area = frame.Width * frame.Height;
            if (area < bestArea)
            {
                bestArea = area;
                best = w;
            }
        }
        return best;
    }

    private static bool NearlyEquals(Rect a, Rect b, double tolerance) =>
        Math.Abs(a.X - b.X) <= tolerance &&
        Math.Abs(a.Y - b.Y) <= tolerance &&
        Math.Abs(a.Width - b.Width) <= tolerance &&
        Math.Abs(a.Height - b.Height) <= tolerance;

}
