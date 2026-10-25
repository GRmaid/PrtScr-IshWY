using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using PrtScr.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;

namespace PrtScr.App.Views;

/// <summary>
/// 截图标注编辑器：矩形/箭头/画笔/文字/马赛克/序号，撤销、清空，复制到剪贴板后关闭。
/// 坐标体系：标注坐标一律存“原始图片像素”，预览按 DIP 换算显示。
/// </summary>
public sealed partial class AnnotationWindow : Window
{
    private const double ToolbarHeight = 52;

    private static readonly List<AnnotationWindow> OpenWindows = new();

    private readonly byte[] _pngBytes;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private readonly Bitmap _source;

    private readonly List<Annotation> _annotations = new();
    private Annotation? _draft;
    private AnnotationTool _tool = AnnotationTool.Rectangle;
    private uint _color = 0xFFFF3B30;
    private double _pixelPerDip = 1;
    private double _displayWidthDip = 1;
    private double _displayHeightDip = 1;
    private PrtScr.App.Services.KeyInterceptor? _keys;
    private Windows.Graphics.RectInt32? _anchor;
    private int _numberCounter;
    private TextBox? _textBox;
    private bool _closed;
    private bool _toolbarDragging;
    private NativePoint _toolbarDragCursor;
    private Windows.Graphics.PointInt32 _toolbarDragWindow;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int x;
        public int y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern BOOL GetCursorPos(out NativePoint point);

    private AnnotationWindow(byte[] pngBytes, int pixelWidth, int pixelHeight)
    {
        _pngBytes = pngBytes;
        _pixelWidth = pixelWidth;
        _pixelHeight = pixelHeight;
        _dispatcherQueue = DispatcherQueue;

        InitializeComponent();
        Title = $"{PrtScr.App.AppInfo.Name} 标注";

        using (var ms = new MemoryStream(pngBytes))
            _source = (Bitmap)System.Drawing.Image.FromStream(ms);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;   // 从贴图右键进入时，确保编辑器不被贴图挡住
        }

        // 消息层拦截 Esc/Enter：不依赖 XAML 焦点，窗口一定可以关
        _keys = new PrtScr.App.Services.KeyInterceptor(WindowNative.GetWindowHandle(this));
        _keys.KeyDown += vk => DispatcherQueue.TryEnqueue(() =>
        {
            if (vk == 0x1B)
            {
                if (_textBox is not null)
                    CancelTextInput();
                else
                    Close();
            }
            else if (vk == 0x0D)
            {
                if (_textBox is not null)
                    CommitTextInput();
                else
                    _ = CopyAndCloseAsync();
            }
        });

        SetupSize();
        LoadImage();

        var undoAccelerator = new KeyboardAccelerator
        {
            Key = VirtualKey.Z,
            Modifiers = VirtualKeyModifiers.Control,
        };
        undoAccelerator.Invoked += (_, e) =>
        {
            e.Handled = true;
            Undo();
        };
        Root.KeyboardAccelerators.Add(undoAccelerator);

        ((FrameworkElement)Content).Loaded += (_, _) =>
        {
            ImageContainer.Width = _displayWidthDip;
            ImageContainer.Height = _displayHeightDip;
            _pixelPerDip = _pixelWidth / Math.Max(1, _displayWidthDip);
            _ = FocusSink.Focus(FocusState.Programmatic);
            Redraw();
        };

        Closed += (_, _) =>
        {
            _closed = true;
            _source.Dispose();
            OpenWindows.Remove(this);
        };
    }

    public static void Open(byte[] pngBytes, int pixelWidth, int pixelHeight,
        Windows.Graphics.RectInt32? anchor = null)
    {
        try
        {
            var window = new AnnotationWindow(pngBytes, pixelWidth, pixelHeight) { _anchor = anchor };
            OpenWindows.Add(window);
            window.Activate();
            window.DispatcherQueue.TryEnqueue(() => window.Activate());
            App.Log($"Annotation: opened {pixelWidth}x{pixelHeight}");
        }
        catch (Exception ex)
        {
            App.Log($"Annotation: 打开失败 {ex}");
        }
    }

    private void SetupSize()
    {
        var hwnd = new HWND(WindowNative.GetWindowHandle(this));
        var dpi = PInvoke.GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var areaWidthDip = area.Width / scale;
        var areaHeightDip = area.Height / scale;
        var maxWidthDip = areaWidthDip * 0.92;
        var maxHeightDip = (areaHeightDip - ToolbarHeight) * 0.9;

        var naturalWidth = _pixelWidth / scale;
        var naturalHeight = _pixelHeight / scale;
        var fit = Math.Min(1, Math.Min(maxWidthDip / naturalWidth, maxHeightDip / naturalHeight));

        _displayWidthDip = Math.Max(1, naturalWidth * fit);
        _displayHeightDip = Math.Max(1, naturalHeight * fit);

        // 窗口至少容纳工具栏，图片居中显示（避免小截图时工具栏按钮点不到）
        var minToolbarWidth = Math.Min(830, areaWidthDip * 0.95);
        var widthDip = Math.Max(_displayWidthDip, minToolbarWidth);

        var windowWidth = (int)Math.Round(widthDip * scale);
        var windowHeight = (int)Math.Round((_displayHeightDip + ToolbarHeight) * scale);

        int x, y;
        if (_anchor is { } anchor)
        {
            // 从贴图进入：贴着原贴图位置打开，视觉上等同“在原图上编辑”
            x = anchor.X - (windowWidth - anchor.Width) / 2;
            y = anchor.Y - (int)Math.Round(ToolbarHeight * scale);
        }
        else
        {
            x = area.X + (area.Width - windowWidth) / 2;
            y = area.Y + (area.Height - windowHeight) / 2;
        }

        x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - windowWidth));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - windowHeight));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, windowWidth, windowHeight));
    }

    private void LoadImage()
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(_pngBytes);
        _ = writer.StoreAsync().AsTask().ContinueWith(_ =>
        {
            writer.DetachStream();
            stream.Seek(0);
            DispatcherQueue.TryEnqueue(async () =>
            {
                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                BaseImage.Source = image;
            });
        });
    }

    // ---------- 工具栏拖动 ----------

    private void Toolbar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 只有点在工具栏空白处才拖窗口，点按钮不拖
        if (!ReferenceEquals(e.OriginalSource, Toolbar))
            return;

        GetCursorPos(out _toolbarDragCursor);
        _toolbarDragWindow = AppWindow.Position;
        _toolbarDragging = true;
        Toolbar.CapturePointer(e.Pointer);
    }

    private void Toolbar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_toolbarDragging)
            return;

        GetCursorPos(out var pt);
        AppWindow.Move(new Windows.Graphics.PointInt32(
            _toolbarDragWindow.X + (pt.x - _toolbarDragCursor.x),
            _toolbarDragWindow.Y + (pt.y - _toolbarDragCursor.y)));
    }

    private void Toolbar_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _toolbarDragging = false;
        Toolbar.ReleasePointerCapture(e.Pointer);
    }

    // ---------- 工具栏 ----------

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } || !Enum.TryParse<AnnotationTool>(tag, out var tool))
            return;

        _tool = tool;
        foreach (var button in new[] { ToolRectangle, ToolArrow, ToolPen, ToolText, ToolMosaic, ToolNumber })
            button.IsChecked = ReferenceEquals(button, sender);

        _ = FocusSink.Focus(FocusState.Programmatic);
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
            _color = ParseColor(tag);
        _ = FocusSink.Focus(FocusState.Programmatic);
    }

    private static uint ParseColor(string hex) => uint.Parse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber);

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _annotations.Clear();
        _numberCounter = 0;
        Redraw();
    }

    private void Undo()
    {
        if (_annotations.Count == 0)
            return;
        _annotations.RemoveAt(_annotations.Count - 1);
        Redraw();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 画布交互 ----------

    private void DrawLayer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            var dip = e.GetCurrentPoint(DrawLayer).Position;
            var p = ToImage(dip);

            if (_tool == AnnotationTool.Text)
            {
                BeginTextInput(dip);
                return;
            }

            DrawLayer.CapturePointer(e.Pointer);

            if (_tool == AnnotationTool.Number)
            {
                _annotations.Add(new Annotation
                {
                    Tool = AnnotationTool.Number,
                    Number = ++_numberCounter,
                    Color = _color,
                    Size = (float)(18 * _pixelPerDip),
                });
                _annotations[^1].Points.Add(p);
                Redraw();
                _ = FocusSink.Focus(FocusState.Programmatic);
                return;
            }

            if (_tool == AnnotationTool.Mosaic)
            {
                _draft = new Annotation
                {
                    Tool = AnnotationTool.Mosaic,
                    Color = _color,
                    Size = (float)Math.Max(8, 10 * _pixelPerDip),
                };
            }
            else
            {
                _draft = new Annotation
                {
                    Tool = _tool,
                    Color = _color,
                    Size = (float)(3 * _pixelPerDip),
                };
            }

            _draft.Points.Add(p);
            _draft.Points.Add(p);
            Redraw();
        }
        catch (Exception ex)
        {
            App.Log($"Annotation 按下异常: {ex.Message}");
        }
    }

    private void DrawLayer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draft is null)
            return;

        try
        {
            var p = ToImage(e.GetCurrentPoint(DrawLayer).Position);
            if (_draft.Tool == AnnotationTool.Pen)
            {
                var last = _draft.Points[^1];
                if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) > 1)
                    _draft.Points.Add(p);
            }
            else
            {
                _draft.Points[^1] = p;
            }
            Redraw();
        }
        catch (Exception ex)
        {
            App.Log($"Annotation 移动异常: {ex.Message}");
        }
    }

    private void DrawLayer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        DrawLayer.ReleasePointerCapture(e.Pointer);
        if (_draft is null)
            return;

        try
        {
            var dx = Math.Abs(_draft.End.X - _draft.Start.X);
            var dy = Math.Abs(_draft.End.Y - _draft.Start.Y);
            var keep = _draft.Tool == AnnotationTool.Pen
                ? _draft.Points.Count >= 2
                : dx > 2 || dy > 2;

            if (keep)
            {
                if (_draft.Tool == AnnotationTool.Mosaic)
                    _draft.PreviewImage = BuildMosaicPreview(_draft);
                _annotations.Add(_draft);
            }
        }
        finally
        {
            _draft = null;
            Redraw();
            _ = FocusSink.Focus(FocusState.Programmatic);
        }
    }

    private void FocusSink_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (_textBox is not null)
                CancelTextInput();
            else
                Close();
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = CopyAndCloseAsync();
        }
    }

    // ---------- 文字 ----------

    private void BeginTextInput(Windows.Foundation.Point dip)
    {
        CancelTextInput();

        var box = new TextBox
        {
            Width = 260,
            FontSize = 16,
            AcceptsReturn = false,
            PlaceholderText = "输入文字，回车确认",
            TextWrapping = TextWrapping.NoWrap,
        };
        Canvas.SetLeft(box, dip.X);
        Canvas.SetTop(box, dip.Y);
        DrawLayer.Children.Add(box);
        _textBox = box;

        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                CommitTextInput();
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                CancelTextInput();
            }
        };
        box.LostFocus += (_, _) => CommitTextInput();
        _ = box.Focus(FocusState.Programmatic);
    }

    private void CommitTextInput()
    {
        var box = _textBox;
        if (box is null)
            return;
        _textBox = null;

        var text = box.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            var p = new PointD(
                Math.Max(0, Canvas.GetLeft(box)) * _pixelPerDip,
                Math.Max(0, Canvas.GetTop(box)) * _pixelPerDip);
            var annotation = new Annotation
            {
                Tool = AnnotationTool.Text,
                Text = text,
                Color = _color,
                Size = (float)(18 * _pixelPerDip),
            };
            annotation.Points.Add(p);
            _annotations.Add(annotation);
        }

        Redraw();
        _ = FocusSink.Focus(FocusState.Programmatic);
    }

    private void CancelTextInput()
    {
        if (_textBox is not null)
        {
            DrawLayer.Children.Remove(_textBox);
            _textBox = null;
        }
        Redraw();
    }

    // ---------- 预览绘制 ----------

    private void Redraw()
    {
        if (_closed)
            return;

        DrawLayer.Children.Clear();
        foreach (var annotation in _annotations)
            AddPreviewShape(annotation);
        if (_draft is not null)
            AddPreviewShape(_draft);

        // 文字输入框要保持在最上层
        if (_textBox is not null && !DrawLayer.Children.Contains(_textBox))
            DrawLayer.Children.Add(_textBox);
    }

    private void AddPreviewShape(Annotation a)
    {
        var brush = new SolidColorBrush(ToColor(a.Color));
        var thickness = Math.Max(1, a.Size / _pixelPerDip);
        var start = ToDip(a.Start);
        var end = ToDip(a.End);

        switch (a.Tool)
        {
            case AnnotationTool.Rectangle:
            case AnnotationTool.Mosaic:
                {
                    var left = Math.Min(start.X, end.X);
                    var top = Math.Min(start.Y, end.Y);
                    var width = Math.Abs(end.X - start.X);
                    var height = Math.Abs(end.Y - start.Y);

                    if (a.Tool == AnnotationTool.Mosaic && a.PreviewImage is not null)
                    {
                        var patch = new Microsoft.UI.Xaml.Controls.Image
                        {
                            Source = a.PreviewImage,
                            Width = width,
                            Height = height,
                            Stretch = Stretch.Fill,
                        };
                        Canvas.SetLeft(patch, left);
                        Canvas.SetTop(patch, top);
                        DrawLayer.Children.Add(patch);
                    }

                    var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
                    {
                        Stroke = brush,
                        StrokeThickness = thickness,
                        Fill = a.Tool == AnnotationTool.Mosaic && a.PreviewImage is null
                            ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x30, 0x80, 0x80, 0x80))
                            : null,
                    };
                    Canvas.SetLeft(rect, left);
                    Canvas.SetTop(rect, top);
                    rect.Width = width;
                    rect.Height = height;
                    DrawLayer.Children.Add(rect);
                    break;
                }

            case AnnotationTool.Arrow:
                {
                    var line = new Line
                    {
                        X1 = start.X,
                        Y1 = start.Y,
                        X2 = end.X,
                        Y2 = end.Y,
                        Stroke = brush,
                        StrokeThickness = thickness,
                    };
                    DrawLayer.Children.Add(line);

                    var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
                    const double spread = Math.PI / 7;
                    var head = Math.Max(8, thickness * 4);
                    var p1 = new Windows.Foundation.Point(
                        end.X - head * Math.Cos(angle - spread), end.Y - head * Math.Sin(angle - spread));
                    var p2 = new Windows.Foundation.Point(
                        end.X - head * Math.Cos(angle + spread), end.Y - head * Math.Sin(angle + spread));
                    var polygon = new Polygon { Fill = brush };
                    polygon.Points.Add(end);
                    polygon.Points.Add(p1);
                    polygon.Points.Add(p2);
                    DrawLayer.Children.Add(polygon);
                    break;
                }

            case AnnotationTool.Pen:
                {
                    var polyline = new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = thickness,
                        StrokeLineJoin = PenLineJoin.Round,
                    };
                    foreach (var p in a.Points)
                        polyline.Points.Add(ToDip(p));
                    DrawLayer.Children.Add(polyline);
                    break;
                }

            case AnnotationTool.Text:
                {
                    var text = new TextBlock
                    {
                        Text = a.Text,
                        Foreground = brush,
                        FontSize = Math.Max(10, a.Size / _pixelPerDip),
                    };
                    Canvas.SetLeft(text, start.X);
                    Canvas.SetTop(text, start.Y);
                    DrawLayer.Children.Add(text);
                    break;
                }

            case AnnotationTool.Number:
                {
                    var radius = Math.Max(8, a.Size / _pixelPerDip);
                    var ellipse = new Ellipse
                    {
                        Width = radius * 2,
                        Height = radius * 2,
                        Fill = brush,
                    };
                    Canvas.SetLeft(ellipse, start.X - radius);
                    Canvas.SetTop(ellipse, start.Y - radius);
                    DrawLayer.Children.Add(ellipse);

                    var label = new TextBlock
                    {
                        Text = a.Number.ToString(),
                        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                        FontSize = Math.Max(10, radius * 1.2),
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    };
                    Canvas.SetLeft(label, start.X - radius * 0.4);
                    Canvas.SetTop(label, start.Y - radius * 0.75);
                    DrawLayer.Children.Add(label);
                    break;
                }
        }
    }


    /// <summary>把马赛克区域真打码成一张小图，用于编辑器实时预览。</summary>
    private WriteableBitmap? BuildMosaicPreview(Annotation a)
    {
        try
        {
            var l = Math.Max(0, (int)Math.Min(a.Start.X, a.End.X));
            var t = Math.Max(0, (int)Math.Min(a.Start.Y, a.End.Y));
            var r = Math.Min(_source.Width, (int)Math.Max(a.Start.X, a.End.X));
            var b = Math.Min(_source.Height, (int)Math.Max(a.Start.Y, a.End.Y));
            var w = r - l;
            var h = b - t;
            if (w < 2 || h < 2)
                return null;

            var block = Math.Max(4, (int)a.Size);
            var patch = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(patch))
                g.DrawImage(_source, new System.Drawing.Rectangle(0, 0, w, h), new System.Drawing.Rectangle(l, t, w, h), GraphicsUnit.Pixel);

            var data = patch.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    var ptr = (byte*)data.Scan0;
                    var stride = data.Stride;
                    for (var by = 0; by < h; by += block)
                    {
                        for (var bx = 0; bx < w; bx += block)
                        {
                            var bw = Math.Min(block, w - bx);
                            var bh = Math.Min(block, h - by);
                            long sb = 0, sg = 0, sr = 0, sa = 0;
                            for (var y = 0; y < bh; y++)
                            {
                                var row = ptr + (by + y) * stride + bx * 4;
                                for (var x = 0; x < bw; x++)
                                {
                                    sb += row[x * 4]; sg += row[x * 4 + 1]; sr += row[x * 4 + 2]; sa += row[x * 4 + 3];
                                }
                            }
                            var count = bw * bh;
                            var nb = (byte)(sb / count); var ng = (byte)(sg / count); var nr = (byte)(sr / count); var na = (byte)(sa / count);
                            for (var y = 0; y < bh; y++)
                            {
                                var row = ptr + (by + y) * stride + bx * 4;
                                for (var x = 0; x < bw; x++)
                                {
                                    row[x * 4] = nb; row[x * 4 + 1] = ng; row[x * 4 + 2] = nr; row[x * 4 + 3] = na;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                patch.UnlockBits(data);
            }

            var writeable = new WriteableBitmap(w, h);
            var pixels = patch.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                using var target = writeable.PixelBuffer.AsStream();
                for (var y = 0; y < h; y++)
                {
                    var row = new byte[w * 4];
                    Marshal.Copy(pixels.Scan0 + y * pixels.Stride, row, 0, row.Length);
                    target.Write(row, 0, row.Length);
                }
            }
            finally
            {
                patch.UnlockBits(pixels);
                patch.Dispose();
            }

            writeable.Invalidate();
            return writeable;
        }
        catch (Exception ex)
        {
            App.Log($"马赛克预览失败: {ex.Message}");
            return null;
        }
    }

    // ---------- 输出 ----------

    private async Task CopyAndCloseAsync()
    {
        try
        {
            using var rendered = AnnotationRenderer.Render(_source, _annotations);
            using var ms = new MemoryStream();
            rendered.Save(ms, ImageFormat.Png);

            var ok = await Services.ClipboardService.SetImageAsync(ms.ToArray());
            App.Log(ok ? "Annotation: copied" : "Annotation: clipboard failure");
        }
        catch (Exception ex)
        {
            App.Log($"Annotation 复制失败: {ex}");
        }
        finally
        {
            Close();
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e) => await CopyAndCloseAsync();

    // ---------- 坐标换算 ----------

    private PointD ToImage(Windows.Foundation.Point dip) => new(dip.X * _pixelPerDip, dip.Y * _pixelPerDip);

    private Windows.Foundation.Point ToDip(PointD point) => new(point.X / _pixelPerDip, point.Y / _pixelPerDip);

    private static Windows.UI.Color ToColor(uint argb) => Windows.UI.Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
