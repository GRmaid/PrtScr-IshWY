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
    private int _toolId;
    private uint _colorArgb = 0xFFFF3B30;
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
        // B02：幂等——菜单「标注…」/重复请求不得清空画布与撤销栈。
        if (_editMode)
        {
            App.Log("Pin: already in edit mode, ignored");
            _ = PInvoke.SetFocus(_hwnd);
            return;
        }

        if (_pngBytes.Length == 0)
            return;

        BeginEditDocument();
        _editMode = true;
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
        EndEditDocument();
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

}
