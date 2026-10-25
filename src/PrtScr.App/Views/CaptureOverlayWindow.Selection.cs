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

/// <summary>冻屏取景覆盖窗：指针输入、移动/缩放、悬停与滚轮切窗。</summary>
public sealed partial class CaptureOverlayWindow
{
    // ---------- 交互 ----------

    private void Layer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            HandlePressed(e);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay PointerPressed 异常: {ex.Message}");
        }
    }

    private void HandlePressed(PointerRoutedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var pointInfo = e.GetCurrentPoint(Root);
        var point = pointInfo.Position;

        // 右键等价 Esc：取景阶段随时取消，不进入选区/拖拽状态。
        if (pointInfo.Properties.IsRightButtonPressed)
        {
            App.Log("Overlay: right-click cancel");
            Cancel();
            return;
        }

        var left = pointInfo.Properties.IsLeftButtonPressed;
        var middle = pointInfo.Properties.IsMiddleButtonPressed && !left;
        // 左键负责框选；滚轮键只用来拖动已有选区，不在空白处重新起框。
        if (!left && !middle)
            return;

        Layer.CapturePointer(e.Pointer);

        // 双击已有选区 = 确认（滚轮键不参与双击）
        if (left && _hasSelection && (now - _lastPress).TotalMilliseconds < 500 &&
            _selection.Contains(point))
        {
            _lastPress = now;
            _ = ConfirmAsync();
            return;
        }
        _lastPress = now;

        // 按下：记住吸附窗口。拖动 = 自由框选；不动 = 单击选中窗口
        _pressPoint = point;
        _lastHoverPoint = point;

        // 左键落在选区四边/四角 8 DIP 内 = 缩放。必须先于“内部移动”判断，
        // 否则靠边起手会被当成移动，永远拉不动边框。
        if (left && TryHitResizeEdge(point, out var resizeEdge))
        {
            BeginResize(resizeEdge, point);
            return;
        }

        // 两种模式：按在已有选区内部拖动 = 移动整个选框（左键或按住滚轮均可）。
        // 窗口模式下整窗选区一动就会脱离原窗口；窗口局部选区则保留窗口归属，
        // 并限制在窗口矩形内移动，确认时仍从窗口本体裁剪。
        if (_hasSelection && _selection.Contains(point))
        {
            _movingSelection = true;
            _moveStartSelection = _selection;
            _moveWholeWindow = _captureMode == CaptureMode.Window &&
                               _clickedWindow is not null && _subregionWindow is null;
            _dragging = true;
            _dragMoved = false;
            HideColorLoupe();
            UpdateVisuals();
            return;
        }

        // 滚轮键只用于移动已有选区：点在空白处时不做任何事，更不会重新框选。
        if (!left)
        {
            Layer.ReleasePointerCapture(e.Pointer);
            return;
        }

        _dragging = true;
        _dragMoved = false;
        HideColorLoupe();
        // 两种模式都允许预选/单击选中窗口；区别只在取图来源：
        // 窗口模式重建窗口本体，常规模式仍从冻结画面裁切（菜单/提示所见即所得）。
        _clickedWindow = _hoverWindow;
        _subregionWindow = null;
        // 悬浮在普通窗口上开始拖拽 = 窗口局部（悬浮类窗口仍按屏幕所见处理）
        _subregionCandidate = _captureMode == CaptureMode.Window &&
                              _hoverWindow is { IsOverlayClass: false } &&
                              _hoverWindow.FrameBounds.Width > 0
            ? _hoverWindow
            : null;
        // 不在按下时直接提交整窗选区：否则边框/内部按下会先变成“整窗选中”，
        // 拖拽被误判为已有选区，边界起拖也会被吞。整窗选中等松手再确认。
        _selection = Rect.Empty;
        _hasSelection = false;
        UpdateVisuals();
    }

    private void Layer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            HandleMoved(e);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay PointerMoved 异常: {ex.Message}");
        }
    }

    private void HandleMoved(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root).Position;

        if (_dragging)
        {
            if (!_dragMoved)
            {
                var dx = point.X - _pressPoint.X;
                var dy = point.Y - _pressPoint.Y;
                if (dx * dx + dy * dy > DragThresholdSq)
                    _dragMoved = true;
            }

            if (_resizingSelection)
            {
                if (_dragMoved)
                    ResizeSelection(point);
                UpdateVisuals();
                return;
            }

            if (_movingSelection)
            {
                if (_dragMoved)
                {
                    HideColorLoupe();

                    // 整窗选区一旦被拖动，就不再代表原来那个窗口；改为常规截图，
                    // 从冻结画面裁切，避免把移动后的矩形错误地映射回窗口内容。
                    if (_moveWholeWindow)
                    {
                        _moveWholeWindow = false;
                        _clickedWindow = null;
                        _subregionWindow = null;
                        _subregionCandidate = null;
                        _captureMode = CaptureMode.Normal;
                        ShowCopyToast("已移动整窗选区，改为常规截图");
                        App.Log("Overlay: whole-window selection moved -> normal screen crop");
                    }

                    var dx = point.X - _pressPoint.X;
                    var dy = point.Y - _pressPoint.Y;

                    var minX = 0.0;
                    var minY = 0.0;
                    var maxX = Math.Max(0, Root.ActualWidth - _moveStartSelection.Width);
                    var maxY = Math.Max(0, Root.ActualHeight - _moveStartSelection.Height);

                    // 窗口局部选区：只能在原窗口矩形内移动，确认时仍走窗口本体裁剪。
                    if (_subregionWindow is { } sub)
                    {
                        var wr = DipRect(sub.FrameBounds);
                        var localMaxX = Math.Min(maxX, wr.X + wr.Width - _moveStartSelection.Width);
                        var localMaxY = Math.Min(maxY, wr.Y + wr.Height - _moveStartSelection.Height);
                        if (localMaxX >= wr.X && localMaxY >= wr.Y)
                        {
                            minX = Math.Max(0, wr.X);
                            minY = Math.Max(0, wr.Y);
                            maxX = localMaxX;
                            maxY = localMaxY;
                        }
                    }

                    var x = Math.Clamp(_moveStartSelection.X + dx, minX, Math.Max(minX, maxX));
                    var y = Math.Clamp(_moveStartSelection.Y + dy, minY, Math.Max(minY, maxY));
                    _selection = new Rect(x, y, _moveStartSelection.Width, _moveStartSelection.Height);
                }

                UpdateVisuals();
                return;
            }

            if (_dragMoved)
            {
                HideColorLoupe();
                _hoverWindow = null;
                _clickedWindow = null;

                var rect = Normalize(_pressPoint, point);
                if (_subregionCandidate is { } target)
                {
                    _subregionWindow = target;
                    var wr = DipRect(target.FrameBounds);
                    var left = Math.Max(rect.X, wr.X);
                    var top = Math.Max(rect.Y, wr.Y);
                    var right = Math.Min(rect.X + rect.Width, wr.X + wr.Width);
                    var bottom = Math.Min(rect.Y + rect.Height, wr.Y + wr.Height);
                    rect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
                }
                else
                {
                    _subregionWindow = null;
                }

                _selection = rect;
                _hasSelection = true;
            }

            UpdateVisuals();
            return;
        }

        UpdateHover(point);
    }

    private void Layer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            HandleReleased(e);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay PointerReleased 异常: {ex.Message}");
        }
    }

    private void HandleReleased(PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        Layer.ReleasePointerCapture(e.Pointer);
        _dragging = false;

        // 焦点拉回锚点，保证 Esc/回车/Tab 随时可用
        _ = FocusSink.Focus(FocusState.Programmatic);

        if (_resizingSelection)
        {
            NormalizeResizedSelection();
            _resizingSelection = false;
            _resizeEdge = ResizeEdge.None;
            UpdateVisuals();
            HideColorLoupe();
            return;
        }

        if (_movingSelection)
        {
            _movingSelection = false;
            _moveWholeWindow = false;
            // 只是点了一下选框内部、没有拖动：保留选区，不进入“新框选”。
            UpdateVisuals();
            HideColorLoupe();
            return;
        }

        if (!_dragMoved)
        {
            if (_clickedWindow is null)
            {
                // 空白处纯点击 = 清除选区
                _selection = Rect.Empty;
                _hasSelection = false;
            }
            else
            {
                // 单击窗口 = 选中整窗（两种模式都支持；常规模式仍从冻结画面裁切）
                _selection = DipRect(_clickedWindow.FrameBounds);
                _hasSelection = true;
                _subregionWindow = null;
            }
        }
        else if (_dragMoved && _selection.Width * _selection.Height < 16)
        {
            // 拖拽距离太短 = 无效选区
            _selection = Rect.Empty;
            _hasSelection = false;
            _subregionWindow = null;
        }

        UpdateVisuals();
        if (_hasSelection)
            HideColorLoupe();
        else
            UpdateColorLoupe(e.GetCurrentPoint(Root).Position);
    }

    private bool TryHitResizeEdge(Windows.Foundation.Point point, out ResizeEdge edge)
    {
        edge = ResizeEdge.None;
        if (!_hasSelection)
            return false;

        var s = _selection;
        var tol = ResizeHandleTolerance;
        if (point.X < s.X - tol || point.X > s.Right + tol ||
            point.Y < s.Y - tol || point.Y > s.Bottom + tol)
            return false;

        var nearLeft = Math.Abs(point.X - s.X) <= tol;
        var nearRight = Math.Abs(point.X - s.Right) <= tol;
        var nearTop = Math.Abs(point.Y - s.Y) <= tol;
        var nearBottom = Math.Abs(point.Y - s.Bottom) <= tol;
        var inY = point.Y >= s.Y - tol && point.Y <= s.Bottom + tol;
        var inX = point.X >= s.X - tol && point.X <= s.Right + tol;

        var left = nearLeft && inY;
        var right = nearRight && inY;
        var top = nearTop && inX;
        var bottom = nearBottom && inX;

        if (left && top) edge = ResizeEdge.TopLeft;
        else if (right && top) edge = ResizeEdge.TopRight;
        else if (left && bottom) edge = ResizeEdge.BottomLeft;
        else if (right && bottom) edge = ResizeEdge.BottomRight;
        else if (left) edge = ResizeEdge.Left;
        else if (right) edge = ResizeEdge.Right;
        else if (top) edge = ResizeEdge.Top;
        else if (bottom) edge = ResizeEdge.Bottom;

        return edge != ResizeEdge.None;
    }

    private void BeginResize(ResizeEdge edge, Windows.Foundation.Point point)
    {
        _resizingSelection = true;
        _resizeEdge = edge;
        _resizeStartSelection = _selection;
        _resizeStartPoint = point;

        // 锚住“对边/对角”：拖过对边时选框翻转继续缩放，而不是卡在最小尺寸。
        _resizeAnchorX = edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft
            ? _selection.Right
            : _selection.X;
        _resizeAnchorY = edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight
            ? _selection.Bottom
            : _selection.Y;

        // 窗口模式整窗选区缩放时，先把原窗口记为局部裁剪目标；缩出来的矩形
        // 仍然从窗口本体重建，不会因为缩小而丢掉抗遮挡能力。
        if (_captureMode == CaptureMode.Window)
        {
            if (_subregionWindow is null && _clickedWindow is not null)
                _subregionWindow = _clickedWindow;
            _subregionCandidate = null;
        }
        else
        {
            _clickedWindow = null;
            _subregionWindow = null;
            _subregionCandidate = null;
        }

        _movingSelection = false;
        _moveWholeWindow = false;
        _dragging = true;
        _dragMoved = false;
        HideColorLoupe();
        App.Log($"Overlay: resize start edge={edge} mode={_captureMode} rect={_selection.X:0},{_selection.Y:0} {_selection.Width:0}x{_selection.Height:0}");
        UpdateVisuals();
    }

    private void ResizeSelection(Windows.Foundation.Point point)
    {
        var start = _resizeStartSelection;
        var bounds = GetResizeBounds();
        var x = Math.Clamp(point.X, bounds.X, bounds.Right);
        var y = Math.Clamp(point.Y, bounds.Y, bounds.Bottom);

        var left = start.X;
        var top = start.Y;
        var right = start.Right;
        var bottom = start.Bottom;

        // 拖动的边跟随指针，对边固定为锚点；min/max 归一化后允许指针越过锚点，
        // 选框会自然翻转（如左上角拖到右下角），不会停在另一条边上。
        if (_resizeEdge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
        {
            left = Math.Min(_resizeAnchorX, x);
            right = Math.Max(_resizeAnchorX, x);
        }
        if (_resizeEdge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight)
        {
            left = Math.Min(_resizeAnchorX, x);
            right = Math.Max(_resizeAnchorX, x);
        }
        if (_resizeEdge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
        {
            top = Math.Min(_resizeAnchorY, y);
            bottom = Math.Max(_resizeAnchorY, y);
        }
        if (_resizeEdge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight)
        {
            top = Math.Min(_resizeAnchorY, y);
            bottom = Math.Max(_resizeAnchorY, y);
        }

        _selection = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>松手后把过薄的选区补到最小可操作尺寸（保持锚定边，不跳回旧框）。</summary>
    private void NormalizeResizedSelection()
    {
        var bounds = GetResizeBounds();
        var sel = _selection;
        var w = sel.Width;
        var h = sel.Height;
        if (w >= MinSelectionSize && h >= MinSelectionSize)
            return;

        var x = sel.X;
        var y = sel.Y;
        if (w < MinSelectionSize)
        {
            if (_resizeEdge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
                x = Math.Clamp(sel.Right - MinSelectionSize, bounds.X, Math.Max(bounds.X, bounds.Right - MinSelectionSize));
            else
                x = Math.Clamp(sel.X, bounds.X, Math.Max(bounds.X, bounds.Right - MinSelectionSize));
            w = MinSelectionSize;
        }
        if (h < MinSelectionSize)
        {
            if (_resizeEdge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
                y = Math.Clamp(sel.Bottom - MinSelectionSize, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - MinSelectionSize));
            else
                y = Math.Clamp(sel.Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - MinSelectionSize));
            h = MinSelectionSize;
        }
        _selection = new Rect(x, y, w, h);
    }

    private Rect GetResizeBounds()
    {
        if (_captureMode == CaptureMode.Window && _subregionWindow is { } sub)
        {
            var wr = DipRect(sub.FrameBounds);
            if (wr.Width >= MinSelectionSize && wr.Height >= MinSelectionSize)
                return wr;
        }

        return new Rect(0, 0,
            Math.Max(MinSelectionSize, Root.ActualWidth),
            Math.Max(MinSelectionSize, Root.ActualHeight));
    }

    private void UpdateHover(Windows.Foundation.Point dipPoint)
    {
        _lastHoverPoint = dipPoint;
        HitTestHover(dipPoint);
        LogHoverChange();
        UpdateVisuals();
        UpdateColorLoupe(dipPoint);
    }

    /// <summary>命中目标变化时记一行，方便主人复现“某个弹窗选不中”时直接看日志。</summary>
    private void LogHoverChange()
    {
        var handle = _hoverWindow?.Handle ?? 0;
        if (handle == _lastHoverHandle)
            return;

        _lastHoverHandle = handle;
        if (_hoverWindow is { } win)
        {
            var title = win.Title.Length > 48 ? win.Title[..48] + "…" : win.Title;
            App.Log($"Overlay: hover 0x{win.Handle:X} class=\"{win.ClassName}\" child={win.IsChild} process=\"{win.ProcessName}\" rect={win.FrameBounds.X},{win.FrameBounds.Y} {win.FrameBounds.Width}x{win.FrameBounds.Height} title=\"{title}\"");
        }
        else
        {
            App.Log("Overlay: hover none");
        }
    }

    /// <summary>
    /// 取景窗亮起时鼠标往往没有移动，PointerMoved 不会触发；
    /// 主动按当前光标做一次命中，避免“必须先动一下鼠标才认窗口”。
    /// </summary>
    private void RefreshHoverFromCursor()
    {
        try
        {
            if (_closed || _scale <= 0)
                return;
            if (!PInvoke.GetCursorPos(out var pt))
                return;
            var dip = new Windows.Foundation.Point(
                (pt.X - _monitor.X) / _scale,
                (pt.Y - _monitor.Y) / _scale);
            UpdateHover(dip);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay 初始命中异常: {ex.Message}");
        }
    }

    private void HitTestHover(Windows.Foundation.Point dipPoint)
    {
        // DIP → 虚拟屏物理坐标
        var vx = _monitor.X + (int)Math.Round(dipPoint.X * _scale);
        var vy = _monitor.Y + (int)Math.Round(dipPoint.Y * _scale);

        // 两种模式都做预选/单击选中：常规模式也先高亮窗口，但确认时仍从冻结画面裁切。
        // 枚举列表本身按 Z 序（顶层在前）；悬浮类窗口（歌词/桌宠/提示）也一并纳入，
        // 与窗口截图合并后不再需要单独的「识别悬浮窗口」开关。
        // 注意命中的是“矩形包含该点”的全部窗口——被遮挡的窗口也在候选里，滚轮可轮换。
        // 复用列表，鼠标高频移动下零分配（分配会引起 GC 停顿 → 闪动感）
        _hoverCandidates.Clear();
        foreach (var w in _windows)
        {
            if (ContainsNear(w.FrameBounds, vx, vy, HitTolerancePx) &&
                w.FrameBounds.Width < _monitor.Width * 2)
            {
                _hoverCandidates.Add(w);
            }
        }

        _hoverWindow = _hoverCandidates.Count > 0 ? _hoverCandidates[0] : null;
    }

    private void Layer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (_dragging)
                return;

            var delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
            var now = Environment.TickCount64;
            if (now - _lastWheelTick > WheelResetMs)
                _wheelAccum = 0;
            _lastWheelTick = now;
            _wheelAccum += delta;
            if (Math.Abs(_wheelAccum) < WheelUnitsPerWindow)
                return;

            // 一次输入最多切一个窗口；累计值清掉，避免高速滚轮一次跨过多个候选。
            var step = _wheelAccum > 0 ? 1 : -1;
            _wheelAccum = 0;
            CycleHover(step);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay 滚轮异常: {ex.Message}");
        }
    }

    /// <summary>滚轮循环切换光标下的候选窗口（顶层→底层），被遮挡的窗口由此可达（微信同款）。</summary>
    private void CycleHover(int delta)
    {
        if (_hoverCandidates.Count == 0)
        {
            HitTestHover(_lastHoverPoint);
            if (_hoverCandidates.Count == 0)
                return;
        }

        var currentIndex = _hoverWindow is null
            ? -1
            : _hoverCandidates.FindIndex(c => c.Handle == _hoverWindow.Handle);

        var nextIndex = delta > 0 ? currentIndex + 1 : currentIndex - 1;
        if (nextIndex < 0)
            nextIndex = _hoverCandidates.Count - 1;
        if (nextIndex >= _hoverCandidates.Count)
            nextIndex = 0;

        _hoverWindow = _hoverCandidates[nextIndex];

        // 已处于“窗口选中”状态时，滚轮同时切换选中的目标
        if (_hasSelection && _clickedWindow is not null)
        {
            _clickedWindow = _hoverWindow;
            _selection = DipRect(_clickedWindow.FrameBounds);
            // 切到新窗口后选区回到“整窗”，不能再带着上一个窗口的局部裁剪归属，
            // 否则确认时会把新窗口的矩形映射到旧窗口位图上裁错。
            _subregionWindow = null;
            _subregionCandidate = null;
        }

        UpdateVisuals();
    }

}
