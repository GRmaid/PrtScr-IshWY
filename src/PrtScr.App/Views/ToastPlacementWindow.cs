using Bitmap = System.Drawing.Bitmap;
using DrawingRectangle = System.Drawing.Rectangle;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PrtScr.App.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using Point = Windows.Foundation.Point;

namespace PrtScr.App.Views;

/// <summary>
/// 提示位置编辑视图：铺满主显示器工作区，拖动提示框移动，拖右下角缩放，保存后写入 custom 矩形。
/// </summary>
public sealed class ToastPlacementWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;
    private const uint DwmwaColorNone = 0xFFFFFFFE;

    private static ToastPlacementWindow? _current;

    private readonly Grid _root;
    private readonly Canvas _canvas;
    private readonly Border _toast;
    private readonly Border _grip;
    private readonly TextBlock _toastText;
    private NumberBox _fontSizeBox = null!;
    private NumberBox _opacityBox = null!;
    private ComboBox _materialCombo = null!;
    private readonly nint _hwnd;
    private readonly Action? _onSaved;
    private readonly Action? _onCanceled;
    private readonly Bitmap? _workAreaCapture;
    private double _scale = 1;
    private bool _saved;
    private KeyInterceptor? _borderFix;
    private Rect _rect;
    private Point _startPoint;
    private Rect _startRect;
    private bool _moving;
    private bool _resizing;
    private bool _closed;

    private ToastPlacementWindow(Bitmap? workAreaCapture, Action? onSaved, Action? onCanceled)
    {
        _workAreaCapture = workAreaCapture;
        _onSaved = onSaved;
        _onCanceled = onCanceled;
        Title = "IshWY 提示位置";
        _hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        var ex = GetWindowLong(_hwnd, GwlExStyle);
        _ = SetWindowLong(_hwnd, GwlExStyle, ex | WsExToolWindow);
        _borderFix = new KeyInterceptor(_hwnd);
        _borderFix.MakeClientAreaFullWindow();
        ApplyDwmChrome();

        _root = new Grid();
        _root.IsTabStop = true;
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var config = App.Config.Current;
        var top = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(24, 18, 24, 18),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)),
        };
        top.Children.Add(new TextBlock
        {
            Text = "拖动提示框调整位置，拖右下角小方块调整大小。",
            FontSize = 15,
            Foreground = new SolidColorBrush(Colors.White),
        });
        top.Children.Add(new TextBlock
        {
            Text = "位置按主显示器工作区记录；材质、不透明度与本页字号会随「保存位置」一起保存，Esc 或「取消」不保存。",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Colors.White),
        });

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        controls.Children.Add(CreateNumberGroup("字号", 11, 32, config.ToastFontSize, 1, out _fontSizeBox));

        _materialCombo = new ComboBox { Width = 150 };
        _materialCombo.Items.Add(new ComboBoxItem { Content = "纯色", Tag = "solid" });
        _materialCombo.Items.Add(new ComboBoxItem { Content = "亚克力", Tag = "acrylic" });
        _materialCombo.Items.Add(new ComboBoxItem { Content = "云母", Tag = "mica" });
        _materialCombo.Items.Add(new ComboBoxItem { Content = "液态玻璃", Tag = "liquid" });
        _materialCombo.SelectedIndex = MaterialIndex(config.ToastMaterial);
        var materialGroup = new StackPanel { Spacing = 4 };
        materialGroup.Children.Add(new TextBlock
        {
            Text = "背景材质",
            FontSize = 12,
            Opacity = 0.72,
            Foreground = new SolidColorBrush(Colors.White),
        });
        materialGroup.Children.Add(_materialCombo);
        controls.Children.Add(materialGroup);

        controls.Children.Add(CreateNumberGroup("不透明度 %", 35, 100,
            Math.Round(Services.ToastAppearance.ClampOpacity(config.ToastOpacity) * 100), 5, out _opacityBox));
        top.Children.Add(controls);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var save = new Button { Content = "保存位置", Padding = new Thickness(16, 6, 16, 6) };
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 6, 16, 6) };
        cancel.Click += (_, _) => Close();
        actions.Children.Add(save);
        actions.Children.Add(cancel);
        top.Children.Add(actions);
        Grid.SetRow(top, 0);
        _root.Children.Add(top);

        _canvas = new Canvas
        {
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
        };
        Grid.SetRow(_canvas, 1);
        _root.Children.Add(_canvas);

        var toastGrid = new Grid();
        _toastText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(config.ToastSuccessText)
                ? "提示预览"
                : config.ToastSuccessText,
            FontSize = Math.Clamp(config.ToastFontSize, 11, 32),
            Foreground = new SolidColorBrush(Colors.White),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toastGrid.Children.Add(_toastText);

        _grip = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0x20, 0x20, 0x20)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 5, 5),
        };
        toastGrid.Children.Add(_grip);

        _toast = new Border
        {
            Background = Services.ToastAppearance.CreatePanelBrush(config.ToastMaterial, config.ToastOpacity),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 9, 16, 9),
            Child = toastGrid,
        };
        _canvas.Children.Add(_toast);

        _fontSizeBox.ValueChanged += (_, args) =>
        {
            if (double.IsNaN(args.NewValue))
                return;
            _toastText.FontSize = Math.Clamp((int)Math.Round(args.NewValue), 11, 32);
        };
        _materialCombo.SelectionChanged += (_, _) => ApplyPreviewAppearance();
        _opacityBox.ValueChanged += (_, _) => ApplyPreviewAppearance();

        _canvas.PointerPressed += Canvas_PointerPressed;
        _canvas.PointerMoved += Canvas_PointerMoved;
        _canvas.PointerReleased += Canvas_PointerReleased;
        _canvas.PointerCanceled += Canvas_PointerReleased;
        _root.KeyDown += Root_KeyDown;
        _root.Loaded += (_, _) =>
        {
            InitializeRect();
            ApplyPreviewAppearance();
            _root.Focus(FocusState.Programmatic);
        };
        Content = _root;

        var work = DisplayArea.Primary.WorkArea;
        AppWindow.MoveAndResize(work);
        Closed += (_, _) =>
        {
            _closed = true;
            _borderFix?.Dispose();
            _borderFix = null;
            _workAreaCapture?.Dispose();
            if (ReferenceEquals(_current, this))
                _current = null;
            if (!_saved && _onCanceled is not null)
                App.UiQueue?.TryEnqueue(() => _onCanceled());
        };
    }

    /// <summary>主 UI 线程打开位置编辑视图；保存成功后回调，用于刷新设置页并预览。</summary>
    public static void Open(Action? onSaved = null, Action? onCanceled = null)
    {
        var queue = App.UiQueue;
        if (queue is null)
            return;
        queue.TryEnqueue(() =>
        {
            try
            {
                _current?.Close();
                // 先截主显示器工作区，编辑视图本身不会出现在预览背景里。
                var work = DisplayArea.Primary.WorkArea;
                var capture = ToastBackdrop.Capture(work);
                App.Log($"Toast placement: capture={(capture is null ? "null" : $"{capture.Width}x{capture.Height}")}");
                _current = new ToastPlacementWindow(capture, onSaved, onCanceled);
                _current.Activate();
            }
            catch (Exception ex)
            {
                App.Log($"Toast placement: 打开失败 {ex}");
            }
        });
    }

    private static StackPanel CreateNumberGroup(string label, double min, double max, double value, double step, out NumberBox box)
    {
        box = new NumberBox
        {
            Width = 110,
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = value,
        };
        var group = new StackPanel { Spacing = 4 };
        group.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.72,
            Foreground = new SolidColorBrush(Colors.White),
        });
        group.Children.Add(box);
        return group;
    }

    private static int MaterialIndex(string? material) => Services.ToastAppearance.NormalizeMaterial(material) switch
    {
        "solid" => 0,
        "mica" => 2,
        "liquid" => 3,
        _ => 1,
    };

    private string SelectedMaterial() =>
        (_materialCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "acrylic";

    private double SelectedOpacity()
    {
        if (double.IsNaN(_opacityBox.Value))
            return 0.9;
        return Math.Clamp(_opacityBox.Value / 100.0, Services.ToastAppearance.MinOpacity, Services.ToastAppearance.MaxOpacity);
    }

    private void ApplyPreviewAppearance()
    {
        if (_closed)
            return;

        var material = SelectedMaterial();
        var opacity = SelectedOpacity();
        if (material == "solid" || _workAreaCapture is null ||
            _canvas.ActualWidth <= 0)
        {
            _toast.Background = Services.ToastAppearance.CreatePanelBrush(material, opacity);
            return;
        }

        try
        {
            // 画布左上角在整窗内偏移（顶部说明条），整窗又从主显示器工作区左上角开始；
            // 因此预览裁剪 = 画布偏移 + 提示框在画布内的位置。
            var origin = CanvasOrigin();
            var crop = new DrawingRectangle(
                (int)Math.Round((origin.X + _rect.X) * _scale),
                (int)Math.Round((origin.Y + _rect.Y) * _scale),
                Math.Max(2, (int)Math.Round(_rect.Width * _scale)),
                Math.Max(2, (int)Math.Round(_rect.Height * _scale)));
            _toast.Background = Services.ToastBackdrop.CreateBrushFrom(
                _workAreaCapture, crop, material, opacity)
                ?? Services.ToastAppearance.CreatePanelBrush(material, opacity);
        }
        catch (Exception ex)
        {
            App.Log($"Toast placement: 预览背景失败 {ex.Message}");
            _toast.Background = Services.ToastAppearance.CreatePanelBrush(material, opacity);
        }
    }

    private void InitializeRect()
    {
        _scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
        var config = App.Config.Current;
        var origin = CanvasOrigin();
        var width = Math.Clamp(config.ToastCustomWidth, 160, Math.Max(160, _canvas.ActualWidth));
        var height = Math.Clamp(config.ToastCustomHeight, 48, Math.Max(48, _canvas.ActualHeight));
        if (string.Equals(config.ToastPosition, "custom", StringComparison.OrdinalIgnoreCase) &&
            config.ToastCustomWidth >= 160 && config.ToastCustomHeight >= 48)
        {
            width = Math.Clamp(config.ToastCustomWidth, 160, Math.Max(160, _canvas.ActualWidth));
            height = Math.Clamp(config.ToastCustomHeight, 48, Math.Max(48, _canvas.ActualHeight));
            _rect = new Rect(
                Math.Clamp(config.ToastCustomX - origin.X, 0, Math.Max(0, _canvas.ActualWidth - width)),
                Math.Clamp(config.ToastCustomY - origin.Y, 0, Math.Max(0, _canvas.ActualHeight - height)),
                width, height);
        }
        else
        {
            _rect = new Rect(
                Math.Max(0, (_canvas.ActualWidth - width) / 2),
                Math.Max(0, _canvas.ActualHeight - height - 24),
                width, height);
        }
        ApplyRect();
    }

    /// <summary>Canvas 左上角在整窗内的 DIP 偏移（顶部说明条占位），保存时换算成工作区绝对坐标。</summary>
    private Point CanvasOrigin()
    {
        try
        {
            return _canvas.TransformToVisual(_root).TransformPoint(new Point(0, 0));
        }
        catch
        {
            return new Point(0, 0);
        }
    }

    private void ApplyRect()
    {
        if (_canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0)
            return;
        _rect = new Rect(
            Math.Clamp(_rect.X, 0, Math.Max(0, _canvas.ActualWidth - _rect.Width)),
            Math.Clamp(_rect.Y, 0, Math.Max(0, _canvas.ActualHeight - _rect.Height)),
            Math.Clamp(_rect.Width, 160, Math.Max(160, _canvas.ActualWidth)),
            Math.Clamp(_rect.Height, 48, Math.Max(48, _canvas.ActualHeight)));
        _toast.Width = _rect.Width;
        _toast.Height = _rect.Height;
        Canvas.SetLeft(_toast, _rect.X);
        Canvas.SetTop(_toast, _rect.Y);
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_closed)
            return;
        var point = e.GetCurrentPoint(_canvas);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        var position = point.Position;
        var gripRect = new Rect(_rect.Right - 24, _rect.Bottom - 24, 24, 24);
        if (gripRect.Contains(position))
            _resizing = true;
        else if (_rect.Contains(position))
            _moving = true;
        else
            return;

        _startPoint = position;
        _startRect = _rect;
        _canvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_closed || (!_moving && !_resizing))
            return;
        var position = e.GetCurrentPoint(_canvas).Position;
        var dx = position.X - _startPoint.X;
        var dy = position.Y - _startPoint.Y;
        if (_moving)
        {
            _rect = new Rect(
                Math.Clamp(_startRect.X + dx, 0, Math.Max(0, _canvas.ActualWidth - _startRect.Width)),
                Math.Clamp(_startRect.Y + dy, 0, Math.Max(0, _canvas.ActualHeight - _startRect.Height)),
                _startRect.Width, _startRect.Height);
        }
        else
        {
            _rect = new Rect(
                _startRect.X, _startRect.Y,
                Math.Clamp(_startRect.Width + dx, 160, Math.Max(160, _canvas.ActualWidth - _startRect.X)),
                Math.Clamp(_startRect.Height + dy, 48, Math.Max(48, _canvas.ActualHeight - _startRect.Y)));
        }
        ApplyRect();
        e.Handled = true;
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_moving && !_resizing)
            return;
        _moving = false;
        _resizing = false;
        _canvas.ReleasePointerCapture(e.Pointer);
        ApplyPreviewAppearance();
        e.Handled = true;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void SaveAndClose()
    {
        var config = App.Config.Current;
        var origin = CanvasOrigin();
        config.ToastPosition = "custom";
        config.ToastCustomX = Math.Round(_rect.X + origin.X);
        config.ToastCustomY = Math.Round(_rect.Y + origin.Y);
        config.ToastCustomWidth = Math.Round(_rect.Width);
        config.ToastCustomHeight = Math.Round(_rect.Height);
        if (!double.IsNaN(_fontSizeBox.Value))
            config.ToastFontSize = Math.Clamp((int)Math.Round(_fontSizeBox.Value), 11, 32);
        config.ToastMaterial = SelectedMaterial();
        config.ToastOpacity = SelectedOpacity();
        App.Config.Save();
        App.Log($"Toast: customPlacement={config.ToastCustomX},{config.ToastCustomY} {config.ToastCustomWidth}x{config.ToastCustomHeight}");
        _saved = true;
        var callback = _onSaved;
        Close();
        if (callback is not null)
            App.UiQueue?.TryEnqueue(() => callback());
    }

    private void ApplyDwmChrome()
    {
        try
        {
            var preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            var none = unchecked((int)DwmwaColorNone);
            _ = DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref none, sizeof(int));
        }
        catch
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
