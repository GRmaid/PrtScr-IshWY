using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PrtScr.App.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Views;

/// <summary>
/// 贴图窗口内的“真·原位标注”：工具栏与绘制都发生在贴图窗口自身表面，不另外开窗。
/// 标注坐标以原图像素记录，绘制时按当前窗口缩放换算。
/// </summary>
public sealed unsafe partial class NativePinWindow
{
    private static readonly string[] ToolLabels = ["矩形", "箭头", "画笔", "文字", "马赛克", "序号"];
    private static readonly int[] ToolbarOrder = [0, 1, 2, 3, 4, 5, 20, 21, 22, 24, 23, 15];

    private volatile bool _editMode;
    private readonly List<Annotation> _annotations = new();
    private Annotation? _draft;
    private int _toolId;
    private uint _colorArgb = 0xFFFF3B30;
    private int _numberCounter;
    private Bitmap? _baseScaled;
    private readonly List<(int Id, Rectangle Rect)> _toolbarButtons = new();
    private float _toolbarHeight;
    private bool _editDragging;
    private bool _editPanning;
    private bool _panMoved;
    private NativePoint _panStartCursor;
    private int _panStartWinX;
    private int _panStartWinY;
    private nint _editControl;
    private PrtScr.App.Services.KeyInterceptor? _editKeys;
    private PointD _editTextPosition;
    private double _dpiScale = 1;
    private bool _toolbarAtTop;
    private int _editOffsetY;
    private int _imgX;
    private int _imgY;
    private int _imgW;
    private int _imgH;
    private Color _accentColor = Color.FromArgb(0xFF, 0x07, 0xC1, 0x60);
    private bool _paletteOpen;
    private int _paletteDrag;   // 0=none 1=SV 2=Hue
    private RectangleF _paletteRect;
    private RectangleF _svRect;
    private RectangleF _hueRect;
    private Bitmap? _svBitmap;
    private int _svHue = -1;
    private float _hue = 0;
    private float _sat = 1;
    private float _val = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, System.Text.StringBuilder text, int maxCount);

    private double EditScale => _winW / (double)_pixelWidth;

    private void EnterEditMode()
    {
        if (_pngBytes.Length == 0)
            return;

        _editMode = true;
        _annotations.Clear();
        _numberCounter = 0;
        _draft = null;
        _toolId = 0;

        // 图标固定大小：只跟随系统 DPI，不随贴图缩放
        var dpi = PInvoke.GetDpiForWindow(_hwnd);
        _dpiScale = dpi > 0 ? dpi / 96.0 : 1;

        // 高亮色 = Windows 主题强调色
        try
        {
            var settings = new Windows.UI.ViewManagement.UISettings();
            var accent = settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            _accentColor = Color.FromArgb(accent.A, accent.R, accent.G, accent.B);
        }
        catch
        {
            _accentColor = Color.FromArgb(0xFF, 0x07, 0xC1, 0x60);
        }

        // 工具栏放在鼠标“最后停留位置”的另一侧
        if (GetCursorPos(out var cursor))
        {
            var relative = cursor.y - _winY;
            _toolbarAtTop = relative > _winH / 2;
        }
        else
        {
            _toolbarAtTop = true;
        }

        // 记住图片窗口矩形，并把窗口向一侧扩出工具栏边栏（工具栏浮在图片之外，微信同款）
        _imgX = _winX;
        _imgY = _winY;
        _imgW = _winW;
        _imgH = _winH;

        _toolbarHeight = ComputeToolbarHeight();
        var gapPx = (int)Math.Round(8 * _dpiScale);   // 工具栏与图片之间的透明间隔
        var extra = (int)_toolbarHeight + gapPx;
        _editOffsetY = _toolbarAtTop ? extra : 0;
        _winY = _toolbarAtTop ? _imgY - extra : _imgY;
        _winH = _imgH + extra;
        // 不调用 MoveWindow：位置与尺寸由 UpdateLayeredWindow 一次性原子更新，避免退出/进入时画面跳动

        // Esc 不再单独注册全局热键：会和 OCR 结果窗的 Esc 热键冲突（1409），
        // 统一交给 EscRouter：它看到 _editMode 时只退出标注，不关窗口。
        RebuildBaseScaled();
        RenderEditMode();
        _ = PInvoke.SetFocus(_hwnd);
        App.Log("Pin: edit mode on");
    }

    private void ExitEditMode(bool apply)
    {
        if (!_editMode)
            return;

        DestroyTextEditor();
        _editMode = false;
        // 只有 Esc 仍被按住时才要求先抬起：避免同一次 Esc 退出标注后又关贴图；
        // 若用户已松手（或本次是鼠标点“完成”退出），不能把下一次 Esc 也吞掉。
        _escNeedsRelease = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;

        // 收回工具栏边栏，窗口恢复为纯图片尺寸与位置
        if (_imgW > 0 && _imgH > 0)
        {
            _winX = _imgX;
            _winY = _imgY;
            _winW = _imgW;
            _winH = _imgH;
            _paletteOpen = false;
            _paletteDrag = 0;
            _svBitmap?.Dispose();
            _svBitmap = null;
            _svHue = -1;
        }
        _draft = null;
        _annotations.Clear();
        _baseScaled?.Dispose();
        _baseScaled = null;
        _toolbarButtons.Clear();

        _needsRedrawAfterZoom = true;   // 强制重绘，否则工具栏会留在画面上（表现为 Esc 按一下没反应）
        // 标注时窗口为了工具栏变高，DIB 可能按更大尺寸分配；退出时收回到图片尺寸，
        // 避免这块常驻内存一直挂在进程里。
        MaybeShrinkDestDib();
        RenderAndPresent(highQuality: true);
        App.Log($"Pin: edit mode off (apply={apply})");
    }

    /// <summary>按与 DrawToolbar 相同的换行规则预算工具栏高度（用于扩窗）。</summary>
    private float ComputeToolbarHeight()
    {
        var scale = (float)_dpiScale;
        var button = 30 * scale;
        var gap = 4 * scale;
        var pad = 6 * scale;
        var itemHeight = 28 * scale;
        var rowHeight = itemHeight + gap;
        var swatch = 18 * scale;
        var maxX = _winW - pad;

        float x = pad, y = pad;
        void Place(float width)
        {
            if (x + width > maxX && x > pad)
            {
                x = pad;
                y += rowHeight;
            }
            x += width + gap;
        }

        for (var i = 0; i < ToolbarOrder.Length; i++)
            Place(button);
        for (var i = 0; i < 5; i++)
            Place(swatch);

        return y + itemHeight + pad;
    }

    private void RebuildBaseScaled()
    {
        var width = _imgW > 0 ? _imgW : _winW;
        var height = _imgH > 0 ? _imgH : _winH;
        _baseScaled?.Dispose();
        _baseScaled = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var source = DecodeSource();
        if (source is null)
            return;
        using var graphics = Graphics.FromImage(_baseScaled);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source.Image, new Rectangle(0, 0, width, height));
    }

    private void OnEditPointerDown(nint lParam)
    {
        var (x, y) = ClientPoint(lParam);
        DestroyTextEditor();   // 点击贴图其他位置时收起未确认的文字框

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
                HandleToolbarCommand(id);
                return;
            }
        }

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

    private void HandleToolbarCommand(int id)
    {
        switch (id)
        {
            case 0 or 1 or 2 or 3 or 4 or 5:
                _toolId = id;
                break;
            case >= 10 and <= 14:
                _colorArgb = id switch
                {
                    10 => 0xFFFF3B30,
                    11 => 0xFFFFCC00,
                    12 => 0xFF34C759,
                    13 => 0xFF007AFF,
                    _ => 0xFF000000,
                };
                break;
            case 15:
                _paletteOpen = !_paletteOpen;
                _paletteDrag = 0;
                _svHue = -1;
                break;
            case 20:
                UndoEdit();
                return;
            case 21:
                _annotations.Clear();
                _numberCounter = 0;
                break;
            case 22:
                ApplyEdit();
                return;
            case 24:
                ApplyEdit(saveAs: true);
                return;
            case 23:
                ExitEditMode(apply: false);
                return;
        }

        RenderEditMode();
    }

    /// <summary>把标注烘焙进原图、更新贴图本身、复制到剪贴板，然后退出编辑模式。</summary>
    private void ApplyEdit(bool saveAs = false)
    {
        byte[]? bytes = null;
        // 提交瞬间先把缩放基底放掉，避免“基底 + 全分辨率解码 + 渲染结果”三份大图同时存在
        _baseScaled?.Dispose();
        _baseScaled = null;
        try
        {
            using var full = DecodeSource();
            if (full is null)
            {
                ExitEditMode(apply: false);
                return;
            }

            using var rendered = AnnotationRenderer.Render(full.Image, _annotations);
            using var ms = new MemoryStream();
            rendered.Save(ms, ImageFormat.Png);
            bytes = ms.ToArray();
            _pngBytes = bytes;

            PinClipboardHelper.Copy(bytes, _uiQueue);
            App.Log("Pin: edit applied & copied");
        }
        catch (Exception ex)
        {
            App.Log($"Pin 应用标注失败: {ex}");
        }

        ExitEditMode(apply: true);

        if (saveAs && bytes is not null)
        {
            _ = PrtScr.App.Services.FileSaveService.SavePngAsync(
                (nint)_hwnd.Value, bytes, PrtScr.App.Services.FileSaveService.SuggestedName("pin"));
        }
    }

    // ---------- 文字输入（原生 EDIT，支持 IME） ----------

    private void BeginTextEditor(int x, int y)
    {
        DestroyTextEditor();

        var scale = EditScale;
        _editTextPosition = new PointD(x / scale, y / scale);

        // 分层窗口无法显示子控件，文字输入用临时顶层小窗，覆盖在贴图上
        var width = Math.Min(240, _winW - x - 8);
        _editControl = (nint)PInvoke.CreateWindowEx(
            Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_TOPMOST |
            Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
            "EDIT",
            "",
            Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_POPUP |
            Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_VISIBLE |
            Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_BORDER,
            _winX + x, _winY + y, Math.Max(80, width), 28,
            default, default, default, default).Value;

        if (_editControl == 0)
            return;

        _ = PInvoke.SetFocus(new HWND(_editControl));

        _editKeys = new PrtScr.App.Services.KeyInterceptor(_editControl);
        _editKeys.KeyDown += vk =>
        {
            if (vk == 0x0D)
                CommitTextEditor();
            else if (vk == 0x1B)
                DestroyTextEditor();
        };
    }

    private void CommitTextEditor()
    {
        if (_editControl == 0)
            return;

        var sb = new System.Text.StringBuilder(512);
        _ = GetWindowText(_editControl, sb, sb.Capacity);
        var text = sb.ToString();

        if (!string.IsNullOrWhiteSpace(text))
        {
            var scale = EditScale;
            var annotation = new Annotation
            {
                Tool = AnnotationTool.Text,
                Text = text,
                Color = _colorArgb,
                Size = (float)(18 / scale),
            };
            annotation.Points.Add(_editTextPosition);
            _annotations.Add(annotation);
        }

        DestroyTextEditor();
        RenderEditMode();
    }

    private void DestroyTextEditor()
    {
        _editKeys?.Dispose();
        _editKeys = null;
        if (_editControl != 0)
        {
            _ = PInvoke.DestroyWindow(new HWND(_editControl));
            _editControl = 0;
        }
        _ = PInvoke.SetFocus(_hwnd);
    }

    // ---------- 绘制 ----------

    private void RenderEditMode()
    {
        if (!_editMode || _baseScaled is null || _hwnd.IsNull)
            return;

        using var composed = CreateDestBitmap();
        if (composed is null)
            return;

        using (var graphics = Graphics.FromImage(composed))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImageUnscaled(_baseScaled, 0, _editOffsetY);
        }

        var scale = (float)EditScale;

        // 马赛克先应用（底图处理）
        foreach (var annotation in _annotations)
        {
            if (annotation.Tool == AnnotationTool.Mosaic)
                ApplyMosaicToBitmap(composed, annotation, scale, _editOffsetY);
        }
        if (_draft is { Tool: AnnotationTool.Mosaic })
            ApplyMosaicToBitmap(composed, _draft, scale, _editOffsetY);

        using (var graphics = Graphics.FromImage(composed))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.MultiplyTransform(new Matrix(scale, 0, 0, scale, 0, _editOffsetY));

            foreach (var annotation in _annotations)
            {
                if (annotation.Tool != AnnotationTool.Mosaic)
                    DrawAnnotationShape(graphics, annotation);
            }
            if (_draft is { } draft && draft.Tool != AnnotationTool.Mosaic)
                DrawAnnotationShape(graphics, draft);
        }

        using (var graphics = Graphics.FromImage(composed))
        {
            // 微信式：图片四周绿色边框 + 尺寸标签
            var border = (float)Math.Max(2, 2 * _dpiScale);
            using (var pen = new Pen(_accentColor, border))
                graphics.DrawRectangle(pen, border / 2, _editOffsetY + border / 2,
                    _imgW - border, _imgH - border);

            using var font = new Font("Microsoft YaHei UI", (float)(11 * _dpiScale), FontStyle.Regular, GraphicsUnit.Pixel);
            var label = $"{_pixelWidth} × {_pixelHeight}";
            var size = graphics.MeasureString(label, font);
            using var pill = new SolidBrush(Color.FromArgb(0xC8, 0x18, 0x18, 0x18));
            using var textBrush = new SolidBrush(_accentColor);
            graphics.FillRectangle(pill, 6, _editOffsetY + 6, size.Width + 10, size.Height + 2);
            graphics.DrawString(label, font, textBrush, 11, _editOffsetY + 7);

            DrawToolbar(graphics);
            DrawPalette(graphics);
        }

        Present();
    }

    private void DrawAnnotationShape(Graphics graphics, Annotation a)
    {
        var color = Color.FromArgb((int)a.Color);
        using var pen = new Pen(color, a.Size)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var brush = new SolidBrush(color);

        switch (a.Tool)
        {
            case AnnotationTool.Rectangle:
            {
                var rect = RectFrom(a.Start, a.End);
                graphics.DrawRectangle(pen, rect);
                break;
            }

            case AnnotationTool.Arrow:
            {
                var s = new PointF((float)a.Start.X, (float)a.Start.Y);
                var e = new PointF((float)a.End.X, (float)a.End.Y);
                graphics.DrawLine(pen, s, e);
                var angle = Math.Atan2(e.Y - s.Y, e.X - s.X);
                const double spread = Math.PI / 7;
                var head = Math.Max(8, a.Size * 4);
                var p1 = new PointF(e.X - (float)(head * Math.Cos(angle - spread)), e.Y - (float)(head * Math.Sin(angle - spread)));
                var p2 = new PointF(e.X - (float)(head * Math.Cos(angle + spread)), e.Y - (float)(head * Math.Sin(angle + spread)));
                graphics.FillPolygon(brush, new[] { e, p1, p2 });
                break;
            }

            case AnnotationTool.Pen:
            {
                if (a.Points.Count >= 2)
                {
                    var points = a.Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();
                    graphics.DrawLines(pen, points);
                }
                break;
            }

            case AnnotationTool.Text:
            {
                using var font = new Font("Microsoft YaHei UI", a.Size, FontStyle.Regular, GraphicsUnit.Pixel);
                graphics.DrawString(a.Text, font, brush, (float)a.Start.X, (float)a.Start.Y);
                break;
            }

            case AnnotationTool.Number:
            {
                var cx = (float)a.Start.X;
                var cy = (float)a.Start.Y;
                var r = a.Size;
                graphics.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
                using var font = new Font("Segoe UI", r * 1.2f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var white = new SolidBrush(Color.White);
                var text = a.Number.ToString();
                var size = graphics.MeasureString(text, font);
                graphics.DrawString(text, font, white, cx - size.Width / 2, cy - size.Height / 2);
                break;
            }
        }
    }

    private void DrawToolbar(Graphics graphics)
    {
        var scale = (float)_dpiScale;   // 固定尺寸：只跟 DPI，不跟贴图缩放
        using var bgBrush = new SolidBrush(Color.FromArgb(0xD8, 0x2B, 0x2B, 0x2B));
        using var activeBrush = new SolidBrush(_accentColor);
        using var borderPen = new Pen(Color.FromArgb(0xFF, 0xC8, 0xC8, 0xC8), Math.Max(1, scale));

        _toolbarButtons.Clear();

        var button = 30 * scale;
        var gap = 4 * scale;
        var pad = 6 * scale;
        var itemHeight = 28 * scale;
        var rowHeight = itemHeight + gap;
        var maxX = _winW - pad;
        var x = pad;
        var y = pad;

        float Next(float width)
        {
            if (x + width > maxX && x > pad)
            {
                x = pad;
                y += rowHeight;
            }
            var sx = x;
            x += width + gap;
            return sx;
        }

        var rects = new List<(int Id, RectangleF Rect)>();
        foreach (var id in ToolbarOrder)
        {
            var sx = Next(button);
            rects.Add((id, new RectangleF(sx, y, button, itemHeight)));
        }

        var swatch = 18 * scale;
        var palette = new (int Id, Color Color)[]
        {
            (10, Color.FromArgb(0xFF, 0xFF, 0x3B, 0x30)),
            (11, Color.FromArgb(0xFF, 0xFF, 0xCC, 0x00)),
            (12, Color.FromArgb(0xFF, 0x34, 0xC7, 0x59)),
            (13, Color.FromArgb(0xFF, 0x00, 0x7A, 0xFF)),
            (14, Color.FromArgb(0xFF, 0x00, 0x00, 0x00)),
        };
        foreach (var (id, _) in palette)
        {
            var sx = Next(swatch);
            rects.Add((id, new RectangleF(sx, y + (itemHeight - swatch) / 2, swatch, swatch)));
        }

        _toolbarHeight = y + itemHeight + pad;

        // 浮在窗口上方或下方：不遮挡鼠标最后停留的那一侧
        var offsetY = _toolbarAtTop ? 0 : _winH - _toolbarHeight;
        var placed = rects.Select(r => (r.Id, new RectangleF(r.Rect.X, r.Rect.Y + offsetY, r.Rect.Width, r.Rect.Height))).ToList();

        graphics.FillRectangle(bgBrush, 0, offsetY, _winW, _toolbarHeight);

        foreach (var (id, rect) in placed)
        {
            if (id is >= 0 and <= 5 && id == _toolId)
                graphics.FillRectangle(activeBrush, rect);

            if (id is >= 10 and <= 14)
            {
                var color = palette.First(p => p.Id == id).Color;
                using var brush = new SolidBrush(color);
                graphics.FillRectangle(brush, rect);
                graphics.DrawRectangle(borderPen, rect.X, rect.Y, rect.Width, rect.Height);
                if (_colorArgbColorId() == id)
                    graphics.DrawRectangle(borderPen, rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4);
            }
            else
            {
                DrawIcon(graphics, id, rect, scale);
            }

            _toolbarButtons.Add((id, Rectangle.Round(rect)));
        }
    }

    /// <summary>微信风格的矢量图标（不依赖字体图标库）。</summary>
    private static void DrawIcon(Graphics graphics, int id, RectangleF rect, float scale)
    {
        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        var s = Math.Min(rect.Width, rect.Height) * 0.34f;

        using var pen = new Pen(Color.White, Math.Max(1.4f, 1.6f * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var brush = new SolidBrush(Color.White);

        switch (id)
        {
            case 0:   // 矩形
                graphics.DrawRectangle(pen, cx - s, cy - s * 0.75f, s * 2, s * 1.5f);
                break;

            case 1:   // 箭头
                graphics.DrawLine(pen, cx - s, cy + s * 0.8f, cx + s * 0.9f, cy - s * 0.8f);
                DrawIconArrowHead(graphics, brush, cx + s * 0.9f, cy - s * 0.8f);
                break;

            case 2:   // 画笔
                graphics.DrawCurve(pen, new[]
                {
                    new PointF(cx - s, cy + s * 0.9f),
                    new PointF(cx - s * 0.4f, cy - s * 0.9f),
                    new PointF(cx + s * 0.3f, cy + s * 0.9f),
                    new PointF(cx + s, cy - s * 0.9f),
                });
                break;

            case 3:   // 文字
                using (var font = new Font("Segoe UI", s * 2.0f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    var size = graphics.MeasureString("A", font);
                    graphics.DrawString("A", font, brush, cx - size.Width / 2, cy - size.Height / 2);
                }
                break;

            case 4:   // 马赛克
                var cell = s * 0.85f;
                for (var gy = 0; gy < 3; gy++)
                {
                    for (var gx = 0; gx < 3; gx++)
                    {
                        if ((gx + gy) % 2 == 0)
                            graphics.FillRectangle(brush, cx - cell * 1.5f + gx * cell, cy - cell * 1.5f + gy * cell, cell, cell);
                    }
                }
                break;

            case 5:   // 序号
                graphics.DrawEllipse(pen, cx - s * 0.9f, cy - s * 0.9f, s * 1.8f, s * 1.8f);
                using (var font = new Font("Segoe UI", s * 1.25f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    var size = graphics.MeasureString("1", font);
                    graphics.DrawString("1", font, brush, cx - size.Width / 2, cy - size.Height / 2);
                }
                break;

            case 20:  // 撤销
                graphics.DrawArc(pen, cx - s, cy - s * 0.9f, s * 2, s * 1.8f, 200, 250);
                graphics.FillPolygon(brush, new[]
                {
                    new PointF(cx - s, cy - s * 0.2f),
                    new PointF(cx - s * 1.35f, cy + s * 0.35f),
                    new PointF(cx - s * 0.6f, cy + s * 0.4f),
                });
                break;

            case 21:  // 清空（垃圾桶）
                graphics.DrawLine(pen, cx - s * 0.9f, cy - s * 0.7f, cx + s * 0.9f, cy - s * 0.7f);
                graphics.DrawLine(pen, cx - s * 0.5f, cy - s * 1.05f, cx + s * 0.5f, cy - s * 1.05f);
                graphics.DrawRectangle(pen, cx - s * 0.7f, cy - s * 0.55f, s * 1.4f, s * 1.5f);
                break;

            case 22:  // 完成（对勾）
                graphics.DrawLines(pen, new[]
                {
                    new PointF(cx - s, cy),
                    new PointF(cx - s * 0.2f, cy + s * 0.75f),
                    new PointF(cx + s, cy - s * 0.8f),
                });
                break;

            case 24:  // 保存（下载箭头 + 托盘）
                graphics.DrawLine(pen, cx, cy - s, cx, cy + s * 0.35f);
                graphics.DrawLine(pen, cx - s * 0.55f, cy - s * 0.1f, cx, cy + s * 0.45f);
                graphics.DrawLine(pen, cx + s * 0.55f, cy - s * 0.1f, cx, cy + s * 0.45f);
                graphics.DrawLine(pen, cx - s * 0.9f, cy + s * 0.9f, cx + s * 0.9f, cy + s * 0.9f);
                break;

            case 15:  // 调色盘
            {
                var colors = new[]
                {
                    Color.FromArgb(0xFF, 0xFF, 0x3B, 0x30),
                    Color.FromArgb(0xFF, 0xFF, 0x95, 0x00),
                    Color.FromArgb(0xFF, 0xFF, 0xCC, 0x00),
                    Color.FromArgb(0xFF, 0x34, 0xC7, 0x59),
                    Color.FromArgb(0xFF, 0x00, 0x7A, 0xFF),
                    Color.FromArgb(0xFF, 0xAF, 0x52, 0xDE),
                };
                var radius = s * 1.0f;
                for (var i = 0; i < colors.Length; i++)
                {
                    using var pie = new SolidBrush(colors[i]);
                    graphics.FillPie(pie, cx - radius, cy - radius, radius * 2, radius * 2, -90 + i * 60, 60);
                }
                using var outline = new Pen(Color.FromArgb(0xFF, 0x10, 0x10, 0x10), Math.Max(1, scale * 0.8f));
                graphics.DrawEllipse(outline, cx - radius, cy - radius, radius * 2, radius * 2);
                break;
            }

            case 23:  // 退出（叉）
                graphics.DrawLine(pen, cx - s * 0.75f, cy - s * 0.75f, cx + s * 0.75f, cy + s * 0.75f);
                graphics.DrawLine(pen, cx + s * 0.75f, cy - s * 0.75f, cx - s * 0.75f, cy + s * 0.75f);
                break;
        }
    }

    private static void DrawIconArrowHead(Graphics graphics, Brush brush, float tipX, float tipY)
    {
        var s = Math.Abs(tipX) * 0 + 6f;
        graphics.FillPolygon(brush, new[]
        {
            new PointF(tipX, tipY),
            new PointF(tipX - s, tipY - s * 0.15f),
            new PointF(tipX - s * 0.25f, tipY + s),
        });
    }

    private void DrawPalette(Graphics graphics)
    {
        if (!_paletteOpen)
            return;

        var scale = (float)_dpiScale;
        var svW = 170 * scale;
        var svH = 100 * scale;
        var hueH = 14 * scale;
        var pad = 6 * scale;
        var panelW = svW + pad * 2;
        var panelH = svH + hueH + pad * 3;

        var x = pad;
        var y = _toolbarAtTop ? _toolbarHeight + 8 * scale + pad : _winH - _toolbarHeight - 8 * scale - panelH - pad;
        x = Math.Clamp(x, pad, Math.Max(pad, _winW - panelW - pad));
        y = Math.Clamp(y, pad, Math.Max(pad, _winH - panelH - pad));

        _paletteRect = new RectangleF(x, y, panelW, panelH);
        _svRect = new RectangleF(x + pad, y + pad, svW, svH);
        _hueRect = new RectangleF(x + pad, y + pad + svH + pad, svW, hueH);

        using var bg = new SolidBrush(Color.FromArgb(0xE8, 0x1E, 0x1E, 0x1E));
        graphics.FillRectangle(bg, _paletteRect);

        // SV 方块
        var hueIndex = (int)Math.Round(_hue) % 360;
        if (_svBitmap is null || _svHue != hueIndex)
        {
            _svBitmap?.Dispose();
            _svBitmap = BuildSvBitmap((int)svW, (int)svH, hueIndex);
            _svHue = hueIndex;
        }
        if (_svBitmap is not null)
            graphics.DrawImage(_svBitmap, _svRect);

        // 色相条
        for (var i = 0; i < _hueRect.Width; i++)
        {
            using var pen = new Pen(HsvToColor(i / _hueRect.Width * 360f, 1, 1));
            graphics.DrawLine(pen, _hueRect.X + i, _hueRect.Y, _hueRect.X + i, _hueRect.Y + _hueRect.Height);
        }

        // 标记
        using var white = new Pen(Color.White, Math.Max(1, scale));
        var mx = _svRect.X + _sat * _svRect.Width;
        var my = _svRect.Y + (1 - _val) * _svRect.Height;
        graphics.DrawEllipse(white, mx - 4 * scale, my - 4 * scale, 8 * scale, 8 * scale);

        var hx = _hueRect.X + _hue / 360f * _hueRect.Width;
        graphics.DrawEllipse(white, hx - 4 * scale, _hueRect.Y - 2 * scale, 8 * scale, _hueRect.Height + 4 * scale);
    }

    private void UpdatePaletteFromPoint(int x, int y)
    {
        if (_paletteDrag == 0)
            return;

        if (_paletteDrag == 1)
        {
            _sat = Math.Clamp((x - _svRect.X) / _svRect.Width, 0, 1);
            _val = 1 - Math.Clamp((y - _svRect.Y) / _svRect.Height, 0, 1);
        }
        else if (_paletteDrag == 2)
        {
            _hue = Math.Clamp((x - _hueRect.X) / _hueRect.Width, 0, 1) * 360f;
        }

        var color = HsvToColor(_hue, _sat, _val);
        _colorArgb = (uint)(0xFF << 24 | color.R << 16 | color.G << 8 | color.B);
        RenderEditMode();
    }

    private static Bitmap? BuildSvBitmap(int width, int height, int hue)
    {
        if (width <= 0 || height <= 0)
            return null;

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var ptr = (byte*)data.Scan0;
            for (var y = 0; y < height; y++)
            {
                var v = 1 - y / (float)height;
                var row = ptr + y * data.Stride;
                for (var x = 0; x < width; x++)
                {
                    var color = HsvToColor(hue, x / (float)width, v);
                    row[x * 4] = color.B;
                    row[x * 4 + 1] = color.G;
                    row[x * 4 + 2] = color.R;
                    row[x * 4 + 3] = 255;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    private static Color HsvToColor(float h, float s, float v)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = v - c;
        (float r, float g, float b) = h switch
        {
            < 60 => (c, x, 0f),
            < 120 => (x, c, 0f),
            < 180 => (0f, c, x),
            < 240 => (0f, x, c),
            < 300 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return Color.FromArgb(255,
            Math.Clamp((int)((r + m) * 255), 0, 255),
            Math.Clamp((int)((g + m) * 255), 0, 255),
            Math.Clamp((int)((b + m) * 255), 0, 255));
    }

    private void UndoEdit()
    {
        if (_annotations.Count > 0)
            _annotations.RemoveAt(_annotations.Count - 1);
        RenderEditMode();
    }

    private int _colorArgbColorId() => _colorArgb switch
    {
        0xFFFF3B30 => 10,
        0xFFFFCC00 => 11,
        0xFF34C759 => 12,
        0xFF007AFF => 13,
        _ => 14,
    };

    private static RectangleF RectFrom(PointD a, PointD b) => new(
        (float)Math.Min(a.X, b.X), (float)Math.Min(a.Y, b.Y),
        (float)Math.Abs(a.X - b.X), (float)Math.Abs(a.Y - b.Y));

    private void ApplyMosaicToBitmap(Bitmap bitmap, Annotation a, float scale, int offsetY)
    {
        var rect = RectFrom(a.Start, a.End);
        var x0 = Math.Max(0, (int)(rect.X * scale));
        var y0 = Math.Max(0, (int)(rect.Y * scale) + offsetY);
        var x1 = Math.Min(bitmap.Width, (int)((rect.X + rect.Width) * scale));
        var y1 = Math.Min(bitmap.Height, (int)((rect.Y + rect.Height) * scale) + offsetY);
        if (x1 - x0 < 2 || y1 - y0 < 2)
            return;

        var block = Math.Max(4, (int)(a.Size * scale));
        var data = bitmap.LockBits(new Rectangle(x0, y0, x1 - x0, y1 - y0),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            var basePtr = (byte*)data.Scan0;
            var stride = data.Stride;
            var width = x1 - x0;
            var height = y1 - y0;
            for (var by = 0; by < height; by += block)
            {
                for (var bx = 0; bx < width; bx += block)
                {
                    var bw = Math.Min(block, width - bx);
                    var bh = Math.Min(block, height - by);
                    long sb = 0, sg = 0, sr = 0, sa = 0;
                    for (var yy = 0; yy < bh; yy++)
                    {
                        var row = basePtr + (by + yy) * stride + bx * 4;
                        for (var xx = 0; xx < bw; xx++)
                        {
                            sb += row[xx * 4]; sg += row[xx * 4 + 1]; sr += row[xx * 4 + 2]; sa += row[xx * 4 + 3];
                        }
                    }
                    var count = bw * bh;
                    var nb = (byte)(sb / count); var ng = (byte)(sg / count); var nr = (byte)(sr / count); var na = (byte)(sa / count);
                    for (var yy = 0; yy < bh; yy++)
                    {
                        var row = basePtr + (by + yy) * stride + bx * 4;
                        for (var xx = 0; xx < bw; xx++)
                        {
                            row[xx * 4] = nb; row[xx * 4 + 1] = ng; row[xx * 4 + 2] = nr; row[xx * 4 + 3] = na;
                        }
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static (int X, int Y) ClientPoint(nint lParam)
    {
        var value = (long)lParam;
        var x = (short)(value & 0xFFFF);
        var y = (short)((value >> 16) & 0xFFFF);
        return (x, y);
    }
}