using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PrtScr.App.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Views;

/// <summary>贴图标注：指针绘制、平移与工具栏命令入口。</summary>
public sealed unsafe partial class NativePinWindow
{
    private void OnEditPointerDown(nint lParam)
    {
        var (x, y) = ClientPoint(lParam);

        // 取色面板优先处理（面板浮在最上层）
        if (_paletteOpen)
        {
            var point = new PointF(x, y);
            if (_svRect.Contains(point) || _hueRect.Contains(point))
            {
                _paletteDrag = _svRect.Contains(point) ? 1 : 2;
                _ = PInvoke.SetCapture(_hwnd);
                UpdatePaletteFromPoint(x, y);
                return;
            }
            if (_paletteRect.Contains(point))
                return;   // 面板内的空白处

            _paletteOpen = false;   // 点面板外收起，继续处理本次点击
            RenderEditMode();
        }

        // 工具栏点击
        foreach (var (id, rect) in _toolbarButtons)
        {
            if (rect.Contains(x, y))
            {
                // 字号按钮要保留正在输入的文字；其他命令照旧先收起文字框。
                if (id is not 30 and not 31)
                    DestroyTextEditor();
                HandleToolbarCommand(id);
                return;
            }
        }

        DestroyTextEditor();   // 点击贴图其他位置时收起未确认的文字框

        // 点在图片区域之外（工具栏边栏）不画图
        if (y < _editOffsetY || y >= _editOffsetY + _imgH)
            return;

        var imgY = y - _editOffsetY;
        var scale = EditScale;
        var source = new PointD(x / scale, imgY / scale);

        // 文字/序号是“点击型”工具，不能抓鼠标捕获，否则会把光标锁在贴图上
        if (_toolId == 3)
        {
            BeginTextEditor(x, imgY);
            return;
        }

        if (_toolId == 5)
        {
            _annotations.Add(new Annotation
            {
                Tool = AnnotationTool.Number,
                Number = ++_numberCounter,
                Color = _colorArgb,
                Size = (float)(16 / scale),
            });
            _annotations[^1].Points.Add(source);
            RenderEditMode();
            return;
        }

        _editDragging = true;
        _ = PInvoke.SetCapture(_hwnd);

        switch (_toolId)
        {

            case 4:   // 马赛克
                _draft = new Annotation
                {
                    Tool = AnnotationTool.Mosaic,
                    Color = _colorArgb,
                    Size = (float)(10 / scale),
                };
                break;

            default:
                _draft = new Annotation
                {
                    Tool = _toolId switch
                    {
                        1 => AnnotationTool.Arrow,
                        2 => AnnotationTool.Pen,
                        _ => AnnotationTool.Rectangle,
                    },
                    Color = _colorArgb,
                    Size = (float)(3 / scale),
                };
                break;
        }

        _draft.Points.Add(source);
        _draft.Points.Add(source);
        RenderEditMode();
    }

    private void OnEditPointerMove(nint lParam)
    {
        if (_paletteDrag != 0)
        {
            var (px, py) = ClientPoint(lParam);
            UpdatePaletteFromPoint(px, py);
            return;
        }

        if (!_editDragging || _draft is null)
            return;

        var (x, y) = ClientPoint(lParam);
        var imgY = y - _editOffsetY;
        var scale = EditScale;
        var p = new PointD(x / scale, imgY / scale);

        if (_draft.Tool == AnnotationTool.Pen)
        {
            var last = _draft.Points[^1];
            if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) > 0.5)
                _draft.Points.Add(p);
        }
        else
        {
            _draft.Points[^1] = p;
        }

        RenderEditMode();
    }

    /// <summary>编辑模式下按住中键/右键拖动贴图。左键仍用于绘制。</summary>
    private void BeginEditPan(nint lParam)
    {
        var (x, y) = ClientPoint(lParam);
        var point = new PointF(x, y);

        if (_paletteOpen && _paletteRect.Contains(point))
            return;
        foreach (var (_, rect) in _toolbarButtons)
        {
            if (rect.Contains(x, y))
                return;
        }
        if (y < _editOffsetY || y >= _editOffsetY + _imgH)
            return;

        DestroyTextEditor();
        if (!GetCursorPos(out _panStartCursor))
            return;

        _panStartWinX = _winX;
        _panStartWinY = _winY;
        _panMoved = false;
        _editPanning = true;
        _ = PInvoke.SetCapture(_hwnd);
    }

    private void MoveEditPan()
    {
        if (!GetCursorPos(out var cursor))
            return;

        var dx = cursor.x - _panStartCursor.x;
        var dy = cursor.y - _panStartCursor.y;
        if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2)
            _panMoved = true;

        _winX = _panStartWinX + dx;
        _winY = _panStartWinY + dy;
        _imgX = _winX;
        _imgY = _winY + _editOffsetY;
        _ = PInvoke.SetWindowPos(_hwnd, default, _winX, _winY, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    private bool EndEditPan()
    {
        if (!_editPanning)
            return false;

        _editPanning = false;
        _ = PInvoke.ReleaseCapture();
        return _panMoved;
    }

    private void OnEditPointerUp(nint lParam)
    {
        if (_paletteDrag != 0)
        {
            _paletteDrag = 0;
            _ = PInvoke.ReleaseCapture();
            return;
        }

        if (!_editDragging)
            return;

        _editDragging = false;
        _ = PInvoke.ReleaseCapture();
        if (_draft is null)
            return;

        var dx = Math.Abs(_draft.End.X - _draft.Start.X);
        var dy = Math.Abs(_draft.End.Y - _draft.Start.Y);
        var keep = _draft.Tool == AnnotationTool.Pen
            ? _draft.Points.Count >= 2
            : dx > 1 || dy > 1;

        if (keep)
            _annotations.Add(_draft);
        _draft = null;
        RenderEditMode();
    }

}
