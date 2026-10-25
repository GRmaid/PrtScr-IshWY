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
    private static readonly uint[] QuickColors =
    [
        0xFFFF3B30, 0xFFFFCC00, 0xFF34C759, 0xFF007AFF, 0xFF000000,
    ];

    private const int TextSizeMin = 10;
    private const int TextSizeMax = 96;
    private const int TextSizeStep = 2;
    private const uint WM_SETFONT = 0x0030;

    private enum ToolbarItemKind
    {
        Tool,
        Swatch,
        Palette,
        Command,
        Separator,
    }

    private readonly record struct ToolbarItem(int Id, RectangleF Rect, ToolbarItemKind Kind);

    private readonly record struct ToolbarMetrics(
        float Button,
        float Gap,
        float Pad,
        float ItemHeight,
        float Swatch,
        float RowGap,
        float TextButtonWidth,
        float SeparatorWidth);

    private sealed class ToolbarLayout
    {
        public ToolbarMetrics Metrics;
        public float Height;
        public float ActionY;
        public float ActionRight;
        public bool TextActions;
        public List<ToolbarItem> Items = new();
    }

    private volatile bool _editMode;
    private readonly List<Annotation> _annotations = new();
    private Annotation? _draft;
    private int _toolId;
    private uint _colorArgb = 0xFFFF3B30;
    private int _numberCounter;
    private int _textSize = 18;
    private Bitmap? _baseScaled;
    private readonly List<(int Id, Rectangle Rect)> _toolbarButtons = new();
    private float _toolbarHeight;
    private bool _applyingEdit;
    private bool _editDragging;
    private bool _editPanning;
    private bool _panMoved;
    private NativePoint _panStartCursor;
    private int _panStartWinX;
    private int _panStartWinY;
    private nint _editControl;
    private nint _editFont;
    private int _editLocalX;
    private int _editLocalY;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

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

    private float ComputeToolbarHeight() => MathF.Ceiling(BuildToolbarLayout(_winW).Height);

    /// <summary>
    /// 工具栏布局只有一套来源：扩窗预算和实际绘制都调用这里，避免两套换行规则错位。
    /// 正常宽度是「工具 + 颜色」一排、「撤销/清空 | 取消/完成/另存」一排；
    /// 窄图依次降到紧凑/极紧凑字号，工具与颜色最多占三排，动作始终单独一排。
    /// </summary>
    private ToolbarLayout BuildToolbarLayout(float width)
    {
        var s = (float)_dpiScale;
        var candidates = new[]
        {
            new ToolbarMetrics(30 * s, 4 * s, 8 * s, 28 * s, 18 * s, 8 * s, 42 * s, MathF.Max(1, s)),
            new ToolbarMetrics(26 * s, 3 * s, 6 * s, 25 * s, 14 * s, 6 * s, 38 * s, MathF.Max(1, s)),
            new ToolbarMetrics(22 * s, 2 * s, 5 * s, 22 * s, 12 * s, 4 * s, 34 * s, MathF.Max(1, s)),
        };

        foreach (var metrics in candidates)
        {
            var text = TryBuildToolbar(width, metrics, textActions: true, maxToolRows: 3, allowOverflow: false);
            if (text is not null)
                return text;

            var icons = TryBuildToolbar(width, metrics, textActions: false, maxToolRows: 4, allowOverflow: false);
            if (icons is not null)
                return icons;
        }

        return TryBuildToolbar(width, candidates[^1], textActions: false, maxToolRows: 8, allowOverflow: true)
            ?? new ToolbarLayout { Metrics = candidates[^1], Height = 60 * s, Items = new List<ToolbarItem>() };
    }

    private ToolbarLayout? TryBuildToolbar(float width, ToolbarMetrics m, bool textActions, int maxToolRows, bool allowOverflow)
    {
        var maxX = width - m.Pad;
        if (maxX <= m.Pad)
        {
            if (!allowOverflow)
                return null;
            maxX = m.Pad + MathF.Max(1, m.Button);
        }

        var items = new List<ToolbarItem>();
        var toolRows = 1;
        var rowHeight = m.ItemHeight + m.RowGap;
        var x = m.Pad;
        var y = m.Pad;

        bool NewToolRow()
        {
            toolRows++;
            if (toolRows > maxToolRows)
                return false;
            x = m.Pad;
            y += rowHeight;
            return true;
        }

        // 工具组：矩形 / 箭头 / 画笔 / 文字 / 马赛克 / 序号
        for (var id = 0; id < 6; id++)
        {
            if (x + m.Button > maxX && x > m.Pad && !NewToolRow())
                return null;
            if (!allowOverflow && m.Button > maxX - m.Pad)
                return null;
            items.Add(new ToolbarItem(id, new RectangleF(x, y, m.Button, m.ItemHeight), ToolbarItemKind.Tool));
            x += m.Button + m.Gap;
        }

        // 颜色组：快速色 + 调色盘。能与工具同排就同排，否则整组换到下一排。
        var colorGap = m.Gap * 0.5f;
        var colorGroup = m.SeparatorWidth + m.Gap + 5 * m.Swatch + 5 * colorGap + m.Button;
        if (x > m.Pad && x + colorGroup <= maxX)
        {
            items.Add(new ToolbarItem(-1, new RectangleF(x, y, m.SeparatorWidth, m.ItemHeight), ToolbarItemKind.Separator));
            x += m.SeparatorWidth + m.Gap;
        }
        else if (!NewToolRow())
        {
            return null;
        }

        for (var i = 0; i < 5; i++)
        {
            if (x + m.Swatch > maxX && x > m.Pad && !NewToolRow())
                return null;
            var sy = y + (m.ItemHeight - m.Swatch) / 2;
            items.Add(new ToolbarItem(10 + i, new RectangleF(x, sy, m.Swatch, m.Swatch), ToolbarItemKind.Swatch));
            x += m.Swatch + colorGap;
        }

        if (x + m.Button > maxX && x > m.Pad && !NewToolRow())
            return null;
        items.Add(new ToolbarItem(15, new RectangleF(x, y, m.Button, m.ItemHeight), ToolbarItemKind.Palette));

        // 动作组：固定单独一排，工具和破坏性/确认动作不再混排
        y += rowHeight;
        var actionW = textActions ? m.TextButtonWidth : m.Button;
        var actionWidth = 5 * actionW + 5 * m.Gap + m.SeparatorWidth;
        if (actionWidth > maxX - m.Pad)
        {
            if (textActions)
                return null;

            var available = maxX - m.Pad - 5 * m.Gap - m.SeparatorWidth;
            if (available <= 0)
            {
                if (!allowOverflow)
                    return null;
                available = 5 * m.Button;
            }
            actionW = available / 5;
            if (!allowOverflow && actionW < 14 * (float)_dpiScale)
                return null;
            actionW = MathF.Max(8 * (float)_dpiScale, actionW);
        }

        var actionY = y;
        x = m.Pad;

        void AddCommand(int id)
        {
            items.Add(new ToolbarItem(id, new RectangleF(x, actionY, actionW, m.ItemHeight), ToolbarItemKind.Command));
            x += actionW;
        }

        AddCommand(20);
        x += m.Gap;
        AddCommand(21);
        x += m.Gap;
        items.Add(new ToolbarItem(-1, new RectangleF(x, actionY, m.SeparatorWidth, m.ItemHeight), ToolbarItemKind.Separator));
        x += m.SeparatorWidth + m.Gap;
        AddCommand(23);
        x += m.Gap;
        AddCommand(22);
        x += m.Gap;
        AddCommand(24);

        var actionRight = items
            .Where(i => i.Kind == ToolbarItemKind.Command)
            .Select(i => i.Rect.Right)
            .DefaultIfEmpty(x)
            .Max();

        return new ToolbarLayout
        {
            Metrics = m,
            Height = actionY + m.ItemHeight + m.Pad,
            ActionY = actionY,
            ActionRight = actionRight,
            TextActions = textActions,
            Items = items,
        };
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

    private void HandleToolbarCommand(int id)
    {
        switch (id)
        {
            case 0 or 1 or 2 or 3 or 4 or 5:
                _toolId = id;
                break;
            case >= 10 and <= 14:
                _colorArgb = QuickColors[id - 10];
                break;
            case 15:
                _paletteOpen = !_paletteOpen;
                _paletteDrag = 0;
                _svHue = -1;
                break;
            case 30:
                AdjustTextSize(-TextSizeStep);
                return;
            case 31:
                AdjustTextSize(TextSizeStep);
                return;
            case 20:
                if (_annotations.Count > 0)
                    UndoEdit();
                return;
            case 21:
                if (_annotations.Count == 0)
                    return;
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
        if (!_editMode || _applyingEdit)
            return;

        _applyingEdit = true;
        try
        {
            ApplyEditCore(saveAs);
        }
        finally
        {
            _applyingEdit = false;
        }
    }

    private void ApplyEditCore(bool saveAs)
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
        // 标注会改写 _pngBytes，管理器缩略图与复制/另存都要拿到新图。
        PublishPinState();

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
        _editLocalX = x;
        _editLocalY = y;

        // 分层窗口无法显示子控件，文字输入用临时顶层小窗，覆盖在贴图上。
        // 位置必须用图片原点 _imgX/_imgY：工具栏在顶部时 _winY 比图片原点高一截，
        // 用 _winY + y 会让输入框整体上移一个工具栏高度。
        _editControl = (nint)PInvoke.CreateWindowEx(
            Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_TOPMOST |
            Windows.Win32.UI.WindowsAndMessaging.WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
            "EDIT",
            "",
            Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_POPUP |
            Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_VISIBLE |
            Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_BORDER,
            _imgX + x, _imgY + y, ComputeTextEditorWidth(x), ComputeTextEditorHeight(),
            default, default, default, default).Value;

        if (_editControl == 0)
            return;

        ApplyEditorFontAndBounds();
        _ = PInvoke.SetFocus(new HWND(_editControl));

        _editKeys = new PrtScr.App.Services.KeyInterceptor(_editControl);
        _editKeys.KeyDown += vk =>
        {
            if (vk == 0x0D)
                CommitTextEditor();
            else if (vk == 0x1B)
                DestroyTextEditor();
        };
        _editKeys.MouseWheel += delta => AdjustTextSize(delta > 0 ? TextSizeStep : -TextSizeStep);
        App.Log($"Pin: text editor at {_imgX + x},{_imgY + y} size={_textSize}");
    }

    private int ComputeTextEditorWidth(int x)
    {
        var preferred = Math.Max(240, _textSize * 8);
        var available = Math.Max(80, _imgW - x - 8);
        return Math.Min(preferred, available);
    }

    private int ComputeTextEditorHeight()
    {
        using var font = new Font("Microsoft YaHei UI", Math.Max(6, _textSize), FontStyle.Regular, GraphicsUnit.Pixel);
        return Math.Max((int)Math.Round(28 * _dpiScale), font.Height + (int)Math.Round(8 * _dpiScale));
    }

    private void ApplyEditorFontAndBounds()
    {
        if (_editControl == 0)
            return;

        var oldFont = _editFont;
        using (var font = new Font("Microsoft YaHei UI", Math.Max(6, _textSize), FontStyle.Regular, GraphicsUnit.Pixel))
        {
            _editFont = font.ToHfont();
            _ = SendMessage(_editControl, WM_SETFONT, _editFont, 1);
        }
        if (oldFont != 0)
            _ = DeleteObject(oldFont);

        _ = PInvoke.SetWindowPos(
            new HWND(_editControl), default,
            _imgX + _editLocalX, _imgY + _editLocalY,
            ComputeTextEditorWidth(_editLocalX), ComputeTextEditorHeight(),
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        _ = PInvoke.SetFocus(new HWND(_editControl));
    }

    private void AdjustTextSize(int delta)
    {
        var next = Math.Clamp(_textSize + delta, TextSizeMin, TextSizeMax);
        if (next == _textSize)
        {
            if (_editControl != 0)
                _ = PInvoke.SetFocus(new HWND(_editControl));
            return;
        }

        _textSize = next;
        ApplyEditorFontAndBounds();
        RenderEditMode();
        App.Log($"Pin: text size={_textSize}");
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
                Size = (float)(_textSize / scale),
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
        if (_editFont != 0)
        {
            _ = DeleteObject(_editFont);
            _editFont = 0;
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
        var layout = BuildToolbarLayout(_winW);
        _toolbarHeight = MathF.Ceiling(layout.Height);
        var scale = (float)_dpiScale;   // 固定尺寸：只跟 DPI，不跟贴图缩放
        var offsetY = _toolbarAtTop ? 0 : _winH - _toolbarHeight;
        _toolbarButtons.Clear();

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using var bgBrush = new SolidBrush(Color.FromArgb(0xEE, 0x24, 0x24, 0x24));
        graphics.FillRectangle(bgBrush, 0, offsetY, _winW, _toolbarHeight);

        using var edgePen = new Pen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), 1);
        graphics.DrawLine(edgePen, 0, offsetY + 0.5f, _winW, offsetY + 0.5f);
        graphics.DrawLine(edgePen, 0, offsetY + _toolbarHeight - 0.5f, _winW, offsetY + _toolbarHeight - 0.5f);

        using var activeBrush = new SolidBrush(_accentColor);
        using var swatchBorder = new Pen(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), MathF.Max(1, scale));
        using var selectedPen = new Pen(Color.White, MathF.Max(1.5f, scale * 1.5f));
        using var separatorPen = new Pen(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF), MathF.Max(1, scale));
        using var commandFont = new Font("Microsoft YaHei UI", 11 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var commandBrush = new SolidBrush(Color.FromArgb(0xFF, 0xE8, 0xE8, 0xE8));
        using var disabledBrush = new SolidBrush(Color.FromArgb(0xFF, 0x78, 0x78, 0x78));
        using var cancelBrush = new SolidBrush(Color.FromArgb(0xFF, 0xFF, 0xA8, 0xA0));
        using var whiteBrush = new SolidBrush(Color.White);
        var colorId = _colorArgbColorId();

        foreach (var item in layout.Items)
        {
            var rect = item.Rect;
            rect.Y += offsetY;

            if (item.Kind == ToolbarItemKind.Separator)
            {
                var sx = rect.X + rect.Width / 2;
                graphics.DrawLine(separatorPen, sx, rect.Y + rect.Height * 0.2f, sx, rect.Y + rect.Height * 0.8f);
                continue;
            }

            _toolbarButtons.Add((item.Id, Rectangle.Round(rect)));

            switch (item.Kind)
            {
                case ToolbarItemKind.Swatch:
                    {
                        var color = Color.FromArgb((int)QuickColors[item.Id - 10]);
                        using var brush = new SolidBrush(color);
                        graphics.FillRectangle(brush, rect);
                        graphics.DrawRectangle(swatchBorder, rect.X, rect.Y, rect.Width, rect.Height);
                        if (colorId == item.Id)
                        {
                            graphics.DrawRectangle(selectedPen, rect.X - 3 * scale, rect.Y - 3 * scale,
                                rect.Width + 6 * scale, rect.Height + 6 * scale);
                        }
                        break;
                    }

                case ToolbarItemKind.Tool:
                    if (item.Id == _toolId)
                        FillRoundedRectangle(graphics, activeBrush, rect, 5 * scale);
                    DrawIcon(graphics, item.Id, rect, scale);
                    break;

                case ToolbarItemKind.Palette:
                    if (_paletteOpen)
                        FillRoundedRectangle(graphics, activeBrush, rect, 5 * scale);
                    else if (colorId < 0)
                        DrawRoundedBorder(graphics, selectedPen, rect, 5 * scale);
                    DrawIcon(graphics, item.Id, rect, scale);
                    break;

                case ToolbarItemKind.Command:
                    {
                        var disabled = item.Id is 20 or 21 && _annotations.Count == 0;
                        var complete = item.Id == 22;
                        if (complete)
                            FillRoundedRectangle(graphics, activeBrush, rect, 5 * scale);

                        if (layout.TextActions)
                        {
                            var label = item.Id switch
                            {
                                20 => "撤销",
                                21 => "清空",
                                22 => "完成",
                                23 => "取消",
                                _ => "另存",
                            };
                            var brush = disabled ? disabledBrush
                                : complete ? whiteBrush
                                : item.Id == 23 ? cancelBrush
                                : commandBrush;
                            DrawCenteredText(graphics, label, commandFont, brush, rect);
                        }
                        else
                        {
                            DrawIcon(graphics, item.Id, rect, scale);
                        }
                        break;
                    }
            }
        }

        var textControlLeft = DrawTextSizeControl(graphics, layout, offsetY, scale);
        DrawToolbarMeta(graphics, layout, offsetY, scale, textControlLeft);
    }

    /// <summary>文字工具时在动作条右侧显示字号步进器；宽度不够就隐藏，滚轮仍可调。</summary>
    private float DrawTextSizeControl(Graphics graphics, ToolbarLayout layout, float offsetY, float scale)
    {
        if (_toolId != 3)
            return float.NaN;

        var m = layout.Metrics;
        var button = 22 * scale;
        var gap = 4 * scale;
        var labelWidth = 52 * scale;
        var groupWidth = button * 2 + labelWidth + gap * 2;
        var right = _winW - m.Pad;
        var x = right - groupWidth;
        if (x < layout.ActionRight + m.Gap * 3)
            return float.NaN;

        var y = offsetY + layout.ActionY + (m.ItemHeight - button) / 2;
        using var borderPen = new Pen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), MathF.Max(1, scale));
        using var font = new Font("Microsoft YaHei UI", 11 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.FromArgb(0xFF, 0xE8, 0xE8, 0xE8));
        using var dimBrush = new SolidBrush(Color.FromArgb(0xFF, 0x78, 0x78, 0x78));

        var minus = new RectangleF(x, y, button, button);
        DrawRoundedBorder(graphics, borderPen, minus, 4 * scale);
        DrawCenteredText(graphics, "−", font, _textSize > TextSizeMin ? textBrush : dimBrush, minus);
        _toolbarButtons.Add((30, Rectangle.Round(minus)));

        var label = new RectangleF(minus.Right + gap, y, labelWidth, button);
        DrawCenteredText(graphics, $"字号 {_textSize}", font, textBrush, label);

        var plus = new RectangleF(label.Right + gap, y, button, button);
        DrawRoundedBorder(graphics, borderPen, plus, 4 * scale);
        DrawCenteredText(graphics, "+", font, _textSize < TextSizeMax ? textBrush : dimBrush, plus);
        _toolbarButtons.Add((31, Rectangle.Round(plus)));

        return x;
    }

    /// <summary>动作条右侧的状态：当前工具、当前颜色、可用的快捷键；空间不足时逐级缩短。</summary>
    private void DrawToolbarMeta(Graphics graphics, ToolbarLayout layout, float offsetY, float scale, float maxRight)
    {
        var x = layout.ActionRight + layout.Metrics.Gap * 2;
        if (float.IsNaN(maxRight))
            maxRight = _winW - layout.Metrics.Pad;
        if (maxRight - x < 48 * scale)
            return;

        using var font = new Font("Microsoft YaHei UI", 11 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        var tool = ToolLabels[Math.Clamp(_toolId, 0, ToolLabels.Length - 1)];
        var hex = "#" + (_colorArgb & 0xFFFFFF).ToString("X6");
        var sizeHint = _toolId == 3 ? $" · 字号 {_textSize} · 滚轮 调字号" : "";
        var variants = new (string Text, bool Accent)[][]
        {
            new (string Text, bool Accent)[] { (tool, true), ($" · {hex}{sizeHint} · Ctrl+Z 撤销 · Enter 完成 · Ctrl+S 另存 · Esc 取消", false) },
            new (string Text, bool Accent)[] { (tool, true), ($" · {hex}{sizeHint} · Enter 完成 · Esc 取消", false) },
            new (string Text, bool Accent)[] { (tool, true), ($" · {hex}{sizeHint}", false) },
        };

        (string Text, bool Accent)[]? chosen = null;
        foreach (var variant in variants)
        {
            var width = variant.Sum(part => graphics.MeasureString(part.Text, font).Width);
            if (x + width <= maxRight)
            {
                chosen = variant;
                break;
            }
        }

        if (chosen is null)
            return;

        var lineHeight = font.GetHeight(graphics);
        var y = offsetY + layout.ActionY + (layout.Metrics.ItemHeight - lineHeight) / 2;
        var cursor = x;
        foreach (var (text, accent) in chosen)
        {
            using var brush = new SolidBrush(accent ? _accentColor : Color.FromArgb(0xFF, 0xA8, 0xA8, 0xA8));
            graphics.DrawString(text, font, brush, cursor, y);
            cursor += graphics.MeasureString(text, font).Width;
        }
    }

    private static GraphicsPath RoundedRectanglePath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var r = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) / 2);
        if (r <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        var d = r * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void FillRoundedRectangle(Graphics graphics, Brush brush, RectangleF rect, float radius)
    {
        using var path = RoundedRectanglePath(rect, radius);
        graphics.FillPath(brush, path);
    }

    private static void DrawRoundedBorder(Graphics graphics, Pen pen, RectangleF rect, float radius)
    {
        using var path = RoundedRectanglePath(rect, radius);
        graphics.DrawPath(pen, path);
    }

    private static void DrawCenteredText(Graphics graphics, string text, Font font, Brush brush, RectangleF rect)
    {
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        graphics.DrawString(text, font, brush, rect, format);
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

    private int _colorArgbColorId()
    {
        for (var i = 0; i < QuickColors.Length; i++)
        {
            if (QuickColors[i] == _colorArgb)
                return 10 + i;
        }

        // 调色盘选出的自定义颜色不落在任何快速色上，避免误高亮黑色。
        return -1;
    }

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
