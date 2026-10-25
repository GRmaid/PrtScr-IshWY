using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PrtScr.App.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Views;

/// <summary>贴图标注：工具栏命令、应用标注与文字编辑器。</summary>
public sealed unsafe partial class NativePinWindow
{
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
}
