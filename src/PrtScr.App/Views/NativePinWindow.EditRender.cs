using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PrtScr.App.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Views;

/// <summary>贴图标注：合成、形状与工具栏绘制。</summary>
public sealed unsafe partial class NativePinWindow
{
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

            // 出口失败的可见反馈也画在标注画面里（含正在画的一笔）。
            if (_navHint is not null && _navHintUntil > Environment.TickCount64)
                DrawNavHintChip(graphics, Math.Max(1.0, _dpiScale));
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

}
