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

/// <summary>冻屏取景覆盖窗：提示、可视状态、HUD 与几何辅助。</summary>
public sealed partial class CaptureOverlayWindow
{
    /// <summary>模式切换的可见提示：贴在顶部模式徽标下方，避免“Tab 按了但看不出变化”。</summary>
    private void ShowModeToast(string message)
    {
        ShowCopyToast(message);
    }

    private void ShowCopyToast(string text)
    {
        try
        {
            CopyToastText.Text = text;
            CopyToast.Visibility = Visibility.Visible;

            var rootW = Root.ActualWidth > 0 ? Root.ActualWidth : _monitor.Width / _scale;
            var rootH = Root.ActualHeight > 0 ? Root.ActualHeight : _monitor.Height / _scale;
            var toastW = CopyToast.ActualWidth > 0 ? CopyToast.ActualWidth : 160;
            var toastH = CopyToast.ActualHeight > 0 ? CopyToast.ActualHeight : 30;
            var hudH = HudBar.Visibility == Visibility.Visible
                ? Math.Max(32, HudBar.ActualHeight)
                : 0;
            Canvas.SetLeft(CopyToast, Math.Max(4, (rootW - toastW) / 2));
            Canvas.SetTop(CopyToast, Math.Max(4, rootH - toastH - hudH - 36));

            _toastTimer?.Stop();
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1400);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                CopyToast.Visibility = Visibility.Collapsed;
            };
            timer.Start();
            _toastTimer = timer;
        }
        catch (Exception ex)
        {
            App.Log($"Overlay 提示异常: {ex.Message}");
        }
    }

    // ---------- 绘制 ----------

    /// <summary>把设置里的选区颜色应用到边框与 8 个缩放手柄；手柄描边按底色自动取反差色。</summary>
    private void ApplySelectionColor()
    {
        var config = App.Config.Current;
        var color = config.SelectionBorderFollowTheme
            ? Services.ThemeColorService.Accent
            : ParseHexColor(config.SelectionBorderColor)
              ?? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        SelectionBorder.Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        var outline = luminance < 0.55
            ? Windows.UI.Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xE6, 0, 0, 0);
        var fillBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        var outlineBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(outline);
        foreach (var handle in new[] { HandleNW, HandleN, HandleNE, HandleW, HandleE, HandleSW, HandleS, HandleSE })
        {
            handle.Fill = fillBrush;
            handle.Stroke = outlineBrush;
        }
    }

    private static Windows.UI.Color? ParseHexColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var value = text.Trim();
        if (value.StartsWith('#'))
            value = value[1..];
        if (value.Length != 6 || !uint.TryParse(value,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            return null;
        return Windows.UI.Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private void UpdateVisuals()
    {
        // 布局前 ActualWidth 为 0，用物理尺寸换算兜底（保证亮窗第一帧遮罩就位）
        var width = Root.ActualWidth;
        if (width <= 0)
            width = _monitor.Width / _scale;
        var height = Root.ActualHeight;
        if (height <= 0)
            height = _monitor.Height / _scale;
        if (width <= 0 || height <= 0)
            return;

        // 底部常驻 HUD：模式 + 当前状态下最该知道的按键，替代原先散落的徽标/提示文案
        var hudH = UpdateHud(width, height);

        var sel = _hasSelection ? _selection : Rect.Empty;
        var tipShown = false;

        // 遮罩四块（选区挖洞；无选区时全遮）
        SetRect(MaskTop, 0, 0, width, sel.IsEmpty ? height : sel.Y);
        SetRect(MaskLeft, 0, sel.Y, sel.IsEmpty ? 0 : sel.X, sel.IsEmpty ? 0 : sel.Height);
        SetRect(MaskRight, sel.IsEmpty ? 0 : sel.Right, sel.Y,
            sel.IsEmpty ? 0 : Math.Max(0, width - sel.Right), sel.IsEmpty ? 0 : sel.Height);
        SetRect(MaskBottom, 0, sel.IsEmpty ? height : sel.Bottom, width,
            sel.IsEmpty ? 0 : Math.Max(0, height - sel.Bottom));

        // 窗口局部模式：被取景的窗口始终描边（青色），提示选区来自窗口本体
        if (_subregionWindow is { } subregion)
        {
            var sr = DipRect(subregion.FrameBounds);
            SetRect(HoverBorder, sr.X, sr.Y, sr.Width, sr.Height);
            HoverBorder.Stroke = _normalHoverBrush;
            HoverBorder.Visibility = Visibility.Visible;
        }
        // 窗口吸附高亮：普通窗口青色、悬浮类橙色（有选区/拖拽中不画）
        else if (_hoverWindow is { } hover && !_hasSelection && !_dragging)
        {
            var r = DipRect(hover.FrameBounds);
            SetRect(HoverBorder, r.X, r.Y, r.Width, r.Height);
            HoverBorder.Stroke = hover.IsOverlayClass ? _overlayHoverBrush : _normalHoverBrush;
            HoverBorder.Visibility = Visibility.Visible;

            // 尺寸提示只留数据；操作提示统一交给底部 HUD，避免提示框越贴边越挤
            SizeText.Text = $"{hover.FrameBounds.Width} × {hover.FrameBounds.Height}";
            SetProcessLabel(_captureMode == CaptureMode.Window ? hover : null);
            _lastTipWindow = hover;
            SizeTip.Visibility = Visibility.Visible;
            var tipW = MeasureSizeTipWidth(200);
            var tipX = Math.Clamp(r.X + r.Width - tipW, 4, Math.Max(4, width - tipW - 4));
            var tipY = r.Y + r.Height + 6 < height - hudH - 30 ? r.Y + r.Height + 6 : r.Y - 30;
            Canvas.SetLeft(SizeTip, tipX);
            Canvas.SetTop(SizeTip, Math.Max(4, tipY));
            SizeTip.Visibility = Visibility.Visible;
            tipShown = true;
        }
        else
        {
            HoverBorder.Visibility = Visibility.Collapsed;
        }

        // 选区边框 + 操作条
        if (_hasSelection)
        {
            HideColorLoupe();
            SetRect(SelectionBorder, sel.X, sel.Y, sel.Width, sel.Height);
            SelectionBorder.Visibility = Visibility.Visible;
            UpdateSelectionHandles(sel);

            // 尺寸提示只显示数据，操作方式由底部 HUD 说明
            var prefix = _subregionWindow is not null ? "窗口内容 " : "";
            SizeText.Text = prefix + $"{(int)Math.Round(sel.Width * _scale)} × {(int)Math.Round(sel.Height * _scale)}";
            SetProcessLabel(_subregionWindow ?? (_captureMode == CaptureMode.Window ? _clickedWindow : null));

            SelectionActions.Visibility = Visibility.Visible;
            SelectionActions.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var actionsW = SelectionActions.DesiredSize.Width > 0 ? SelectionActions.DesiredSize.Width : 260;
            var actionsH = SelectionActions.DesiredSize.Height > 0 ? SelectionActions.DesiredSize.Height : 32;

            SizeTip.Visibility = Visibility.Visible;
            var tipSize = MeasureSizeTip(200, 26);
            var tipW = tipSize.Width;
            var tipH = tipSize.Height;

            var hudTop = Math.Max(0, height - hudH - 16);
            var floatBottom = Math.Max(8, hudTop - 6);
            var barBelow = sel.Bottom + 6 + actionsH <= floatBottom;
            var barAbove = !barBelow && sel.Y - 6 - actionsH >= 6;
            var actionsInside = !barBelow && !barAbove;

            var actionsX = Math.Clamp(sel.Right - actionsW, 4, Math.Max(4, width - actionsW - 4));
            var actionsY = barBelow
                ? sel.Bottom + 6
                : barAbove
                    ? sel.Y - 6 - actionsH
                    : Math.Clamp(sel.Y + 6, 4, Math.Max(4, floatBottom - actionsH));

            // 尺寸提示优先贴在选区下方；操作条也优先在下方外侧，两者并排。
            // 只有横向放不下（进程名 + 操作条超过选区宽）或纵向放不下时才翻到选区上方。
            var tipX = Math.Clamp(sel.X, 4, Math.Max(4, width - tipW - 4));
            var tipY = sel.Bottom + 6;
            if (barBelow)
            {
                var sideBySide = sel.Width >= actionsW + tipW + 18 && tipY + tipH <= floatBottom;
                if (sideBySide)
                {
                    // 左：尺寸/进程，右：截图/识别/保存，共用选区下方一条带
                    actionsX = Math.Clamp(sel.Right - actionsW, 4, Math.Max(4, width - actionsW - 4));
                }
                else if (sel.Y - 6 - tipH >= 4)
                {
                    tipX = Math.Clamp(sel.Right - tipW, 4, Math.Max(4, width - tipW - 4));
                    tipY = Math.Max(4, sel.Y - tipH - 6);
                }
                else
                {
                    // 横向过窄且上方也没有空间：提示留在下方，操作条再下移一层；
                    // 仍然放不下时只能把操作条压回选区内右上角，至少保证可点。
                    var stackedY = tipY + tipH + 4;
                    if (stackedY + actionsH <= floatBottom)
                    {
                        actionsY = stackedY;
                    }
                    else
                    {
                        actionsInside = true;
                        actionsX = Math.Clamp(sel.Right - actionsW - 6, 4, Math.Max(4, width - actionsW - 4));
                        actionsY = Math.Clamp(sel.Y + 6, 4, Math.Max(4, floatBottom - actionsH));
                    }
                }
            }
            else if (barAbove)
            {
                if (sel.Bottom + 6 + tipH <= floatBottom)
                {
                    tipX = Math.Clamp(sel.X, 4, Math.Max(4, width - tipW - 4));
                    tipY = sel.Bottom + 6;
                }
                else
                {
                    // 下方被 HUD 顶住：提示翻到操作条上方，仍保持窗口外
                    tipX = Math.Clamp(sel.Right - tipW, 4, Math.Max(4, width - tipW - 4));
                    tipY = Math.Max(4, actionsY - tipH - 4);
                }
            }

            Canvas.SetLeft(SelectionActions, actionsX);
            Canvas.SetTop(SelectionActions, Math.Clamp(actionsY, 4, Math.Max(4, height - actionsH - 4)));
            Canvas.SetLeft(SizeTip, Math.Clamp(tipX, 4, Math.Max(4, width - tipW - 4)));
            Canvas.SetTop(SizeTip, Math.Clamp(tipY, 4, Math.Max(4, height - tipH - 4)));
            tipShown = true;
        }
        else
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            ResizeHandles.Visibility = Visibility.Collapsed;
            SelectionActions.Visibility = Visibility.Collapsed;
            // 悬停提示是独立的：没有选区但有悬浮窗口时必须留着，否则进程名永远看不见
            if (!tipShown)
                SizeTip.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSelectionHandles(Rect sel)
    {
        ResizeHandles.Visibility = Visibility.Visible;
        const double half = 6;
        var cx = sel.X + sel.Width / 2;
        var cy = sel.Y + sel.Height / 2;
        PlaceHandle(HandleNW, sel.X - half, sel.Y - half);
        PlaceHandle(HandleN, cx - half, sel.Y - half);
        PlaceHandle(HandleNE, sel.Right - half, sel.Y - half);
        PlaceHandle(HandleW, sel.X - half, cy - half);
        PlaceHandle(HandleE, sel.Right - half, cy - half);
        PlaceHandle(HandleSW, sel.X - half, sel.Bottom - half);
        PlaceHandle(HandleS, cx - half, sel.Bottom - half);
        PlaceHandle(HandleSE, sel.Right - half, sel.Bottom - half);
    }

    private static void PlaceHandle(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x);
        Canvas.SetTop(handle, y);
    }

    /// <summary>滚轮 + 主人自选的上一张/下一张按键，压成 HUD 里的一小段。</summary>
    private static string WindowSwitchHint()
    {
        var cfg = App.Config.Current;
        var prev = cfg.WindowPrevKeyVk != 0
            ? HotkeyService.Describe(cfg.WindowPrevKeyVk, cfg.WindowPrevKeyModifiers)
            : "";
        var next = cfg.WindowNextKeyVk != 0
            ? HotkeyService.Describe(cfg.WindowNextKeyVk, cfg.WindowNextKeyModifiers)
            : "";
        if (prev.Length == 0 && next.Length == 0)
            return "滚轮";
        return $"滚轮/{prev}{next}";
    }

    /// <summary>
    /// 底部常驻 HUD：模式芯片 + 当前状态下的快捷键提示。
    /// 提示文案只在这里维护，尺寸提示框保持纯数据，避免贴边时被长文案挤出屏幕。
    /// </summary>
    private double UpdateHud(double width, double height)
    {
        HudModeText.Text = _captureMode == CaptureMode.Window ? "窗口截图" : "常规截图";

        if (_dragging)
        {
            if (_resizingSelection)
            {
                HudHintText.Text = _dragMoved ? "松开 完成缩放" : "拖动边缘 缩放选区";
            }
            else if (_movingSelection)
            {
                HudHintText.Text = _dragMoved ? "松开 完成移动" : "拖动 移动选区";
            }
            else
            {
                HudHintText.Text = _dragMoved
                    ? "松开 完成选区"
                    : "拖拽 框选 · 单击 选中窗口";
            }
        }
        else if (_hasSelection)
        {
            // 已有选区：拖边缘缩放、拖内部移动；整窗选区还能继续切窗口。
            var wheel = _clickedWindow is not null ? $" · {WindowSwitchHint()} 切窗" : "";
            HudHintText.Text =
                $"拖边缘 缩放 · 拖内部 移动{wheel} · 回车 截图 · O 识别 · Ctrl+S 保存";
        }
        else if (_hoverWindow is not null)
        {
            HudHintText.Text = _captureMode == CaptureMode.Window
                ? $"{WindowSwitchHint()} 切换窗口 · 回车 截取 · 拖拽 截取局部"
                : $"{WindowSwitchHint()} 切换窗口 · 单击 选中 · 拖拽 自由框选";
        }
        else
        {
            HudHintText.Text = $"拖拽 框选区域 · {WindowSwitchHint()} 切换窗口 · Tab 切换模式";
        }

        HudBar.Visibility = Visibility.Visible;
        HudBar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var hudW = HudBar.DesiredSize.Width > 0 ? HudBar.DesiredSize.Width : 420;
        var hudH = HudBar.DesiredSize.Height > 0 ? HudBar.DesiredSize.Height : 32;
        var hudX = Math.Clamp((width - hudW) / 2, 8, Math.Max(8, width - hudW - 8));
        var hudY = Math.Max(8, height - hudH - 16);
        Canvas.SetLeft(HudBar, hudX);
        Canvas.SetTop(HudBar, hudY);
        return hudH;
    }

    /// <summary>窗口模式在尺寸提示里附带进程名；常规模式或未知进程时收起，避免多占横向空间。</summary>
    private void SetProcessLabel(WindowInfo? window)
    {
        var process = window?.ProcessName;
        if (_captureMode == CaptureMode.Window && !string.IsNullOrWhiteSpace(process))
        {
            ProcessText.Text = $"进程 {process.Trim()}";
            ProcessText.Visibility = Visibility.Visible;
        }
        else
        {
            ProcessText.Text = string.Empty;
            ProcessText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>进程标签宽度不定，先测量 SizeTip 再决定贴边位置，避免提示框被挤出屏幕。</summary>
    private double MeasureSizeTipWidth(double fallback)
    {
        return MeasureSizeTip(fallback, 26).Width;
    }

    /// <summary>同时测量提示框宽高：选区下方要判断能否与操作条并排，高度也参与翻转判断。</summary>
    private Windows.Foundation.Size MeasureSizeTip(double fallbackWidth, double fallbackHeight)
    {
        SizeTip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = SizeTip.DesiredSize.Width;
        var height = SizeTip.DesiredSize.Height;
        return new Windows.Foundation.Size(
            width > 0 ? width : fallbackWidth,
            height > 0 ? height : fallbackHeight);
    }

    private static void SetRect(FrameworkElement element, double x, double y, double w, double h)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, w);
        element.Height = Math.Max(0, h);
    }

    private Rect DipRect(WindowRect physical)
    {
        return new Rect(
            (physical.X - _monitor.X) / _scale,
            (physical.Y - _monitor.Y) / _scale,
            physical.Width / _scale,
            physical.Height / _scale);
    }

    /// <summary>
    /// 几乎铺满整屏的点击穿透层（例如 DSH 歌词 toast 的透明 Chrome 窗口）。
    /// 它永远在 Z 序最前且鼠标会穿过去，不能作为窗口吸附的首选，否则其他窗口都“悬停不亮”。
    /// </summary>
    private bool IsFullScreenClickThrough(WindowInfo w)
    {
        if (!w.IsClickThrough)
            return false;

        var area = (long)w.FrameBounds.Width * w.FrameBounds.Height;
        var monitorArea = (long)_monitor.Width * _monitor.Height;
        return monitorArea > 0 && area >= monitorArea * 85 / 100;
    }

    /// <summary>带容差的矩形命中：窗口右/下边框本身不在 Contains 内，贴边起手会漏选。</summary>
    private static bool ContainsNear(WindowRect rect, int x, int y, int tolerance)
    {
        return x >= rect.X - tolerance && x <= rect.Right + tolerance &&
               y >= rect.Y - tolerance && y <= rect.Bottom + tolerance;
    }

    private static Rect Normalize(Windows.Foundation.Point a, Windows.Foundation.Point b)
    {
        return new Rect(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));
    }
}
