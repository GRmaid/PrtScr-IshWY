using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using PrtScr.App.Services;

namespace PrtScr.App.Views;

/// <summary>
/// 纯文字提示窗：无边框、置顶、不抢焦点、鼠标穿透。
/// 背景材质先用屏幕快照做模糊+染色（ToastBackdrop），不再依赖 WinUI SystemBackdrop；
/// 材质/不透明度/字号/位置/显示时长全部来自设置。
/// </summary>
public sealed class ToastWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;
    private const uint DwmwaColorNone = 0xFFFFFFFE;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int HwndTopmost = -1;

    private static ToastWindow? _current;
    private readonly TextBlock _text;
    private readonly Border _panel;
    private readonly Grid _root;
    private readonly DispatcherQueueTimer _timer;
    private readonly KeyInterceptor _borderFix;
    private readonly nint _hwnd;
    private readonly AppConfig _config;
    private bool _closed;
    private bool _fadingOut;
    private bool _hasDesktopBackdrop;

    private ToastWindow(string text, AppConfig config)
    {
        _config = config;
        Title = "IshWY 提示";
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
        // 无激活显示由 ShowWindow(SW_SHOWNOACTIVATE) 与 SetWindowPos(SWP_NOACTIVATE) 保证。
        _ = SetWindowLong(_hwnd, GwlExStyle, ex | WsExToolWindow | WsExTransparent);
        // 无边框 WinUI 窗口仍会留 1px 非客户区白边，先扩成整窗客户区再画圆角。
        _borderFix = new KeyInterceptor(_hwnd);
        _borderFix.MakeClientAreaFullWindow();
        ApplyDwmChrome();

        var fontSize = Math.Clamp(config.ToastFontSize, 11, 32);
        _text = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = new SolidColorBrush(Colors.White),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 720,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LineHeight = fontSize * 1.35,
        };

        _panel = new Border
        {
            Background = ToastAppearance.CreatePanelBrush(config.ToastMaterial, config.ToastOpacity),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 9, 16, 9),
            Child = _text,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        // 窗口底层铺一张「提示框位置下的真实桌面截图」：
        // 提示层淡入/淡出时露出的是真实画面而不是窗口的黑底，所以淡出不会变黑。
        _root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _root.Children.Add(_panel);
        _panel.Loaded += (_, _) => OnLoaded();
        Content = _root;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(config.ToastVisibleMs, 500, 10000));
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => BeginFadeOut();
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _borderFix.Dispose();
            if (ReferenceEquals(_current, this))
                _current = null;
        };
    }

    /// <summary>主 UI 线程弹出纯文字提示；同一时刻只保留一个。</summary>
    public static void Show(string text, AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var queue = App.UiQueue;
        if (queue is null)
            return;

        if (!queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => Show(text, config));
            return;
        }

        try
        {
            _current?.CloseToast();
            var window = new ToastWindow(text, config);
            _current = window;
            window.PrepareOffscreen();
            window.ShowWindowNoActivate();
        }
        catch (Exception ex)
        {
            App.Log($"Toast: 创建提示窗失败 {ex.Message}");
        }
    }

    /// <summary>先缩到 1×1 并移出屏幕，避免 WinUI 首帧在屏幕上闪一下黑底。</summary>
    private void PrepareOffscreen()
    {
        try
        {
            AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));
        }
        catch (Exception ex)
        {
            App.Log($"Toast: 预置离屏失败 {ex.Message}");
        }
    }

    private void ShowWindowNoActivate()
    {
        _ = ShowWindow(_hwnd, SwShowNoActivate);
        _ = SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0,
            SwpNoActivate | SwpShowWindow | 0x0001 | 0x0002); // NOSIZE | NOMOVE
    }

    private async void OnLoaded()
    {
        if (_closed)
            return;

        var position = _config.ToastPosition ?? "bottom-center";
        var rect = ComputePlacementRect(position);
        var material = ToastAppearance.NormalizeMaterial(_config.ToastMaterial);

        // 关键：窗口此时还在屏幕外的 1×1 位置，先截目标区域。
        // Raw 像素铺到窗口底层，Blurred 像素贴到提示面板；截屏与模糊放后台，UI 线程只建 Brush。
        try
        {
            var capture = await Task.Run(() =>
                ToastBackdrop.CreatePixelsWithRaw(rect, material, _config.ToastOpacity));
            if (_closed)
                return;
            if (capture is { } images)
            {
                var rawBrush = ToastBackdrop.CreateBrushFromPixels(images.Raw);
                if (rawBrush is not null)
                {
                    _root.Background = rawBrush;
                    _hasDesktopBackdrop = true;
                }

                if (images.Blurred is { } blurred)
                {
                    var brush = ToastBackdrop.CreateBrushFromPixels(blurred);
                    if (brush is not null)
                        _panel.Background = brush;
                }

                App.Log($"Toast: 背景 {material} rect={rect.X},{rect.Y} {rect.Width}x{rect.Height}");
            }
            else
            {
                App.Log("Toast: 截取为空，使用纯色兜底");
            }
        }
        catch (Exception ex)
        {
            App.Log($"Toast: 背景合成异常 {ex.Message}");
        }

        if (_closed)
            return;

        try
        {
            AppWindow.MoveAndResize(rect);
        }
        catch (Exception ex)
        {
            App.Log($"Toast: 定位失败 {ex.Message}");
            CloseToast();
            return;
        }

        ApplyDwmChrome();

        if (_hasDesktopBackdrop)
        {
            _panel.Opacity = 0;
            // 等布局/移动完成后再淡入；底层是真实桌面截图，淡入不会露出黑底。
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_closed)
                    return;
                BeginFadeIn();
                _timer.Start();
            });
        }
        else
        {
            // 没有桌面截图时直接显示、到点直接关闭，避免窗口黑底参与淡入淡出。
            _panel.Opacity = 1;
            _timer.Start();
        }
    }

    private RectInt32 ComputePlacementRect(string position)
    {
        _panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var scale = _panel.XamlRoot?.RasterizationScale ?? (GetDpiForWindow(_hwnd) / 96.0);
        var desiredWidth = _panel.DesiredSize.Width;
        var desiredHeight = _panel.DesiredSize.Height;
        if (desiredWidth < 2 || desiredHeight < 2)
        {
            _text.Measure(new Size(720, double.PositiveInfinity));
            desiredWidth = _text.DesiredSize.Width + 34;
            desiredHeight = _text.DesiredSize.Height + 20;
        }

        // 自定义位置：设置页「编辑位置」保存的是主显示器工作区内的 DIP 矩形。
        if (string.Equals(position, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var customArea = DisplayArea.Primary;
            var customWork = customArea.WorkArea;
            var workWidthDip = customWork.Width / scale;
            var workHeightDip = customWork.Height / scale;
            var widthDip = Math.Clamp(_config.ToastCustomWidth, Math.Min(160, workWidthDip), workWidthDip);
            var heightDip = Math.Clamp(_config.ToastCustomHeight, Math.Min(48, workHeightDip), workHeightDip);
            var xDip = Math.Clamp(_config.ToastCustomX, 0, Math.Max(0, workWidthDip - widthDip));
            var yDip = Math.Clamp(_config.ToastCustomY, 0, Math.Max(0, workHeightDip - heightDip));
            var customWidth = Math.Max(40, (int)Math.Round(widthDip * scale));
            var customHeight = Math.Max(24, (int)Math.Round(heightDip * scale));
            var customX = customWork.X + (int)Math.Round(xDip * scale);
            var customY = customWork.Y + (int)Math.Round(yDip * scale);
            return new RectInt32(customX, customY, customWidth, customHeight);
        }

        var width = Math.Max(40, (int)Math.Ceiling(desiredWidth * scale));
        var height = Math.Max(24, (int)Math.Ceiling(desiredHeight * scale));

        if (!GetCursorPos(out var cursor))
            cursor = new NativePoint { X = 0, Y = 0 };
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        const int marginDip = 24;
        var margin = (int)Math.Round(marginDip * scale);

        var horizontal = position.Contains("left", StringComparison.OrdinalIgnoreCase)
            ? 0
            : position.Contains("right", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var vertical = position.StartsWith("top", StringComparison.OrdinalIgnoreCase)
            ? 0
            : position.StartsWith("middle", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

        var x = horizontal switch
        {
            0 => work.X + margin,
            2 => work.X + work.Width - width - margin,
            _ => work.X + (work.Width - width) / 2,
        };
        var y = vertical switch
        {
            0 => work.Y + margin,
            1 => work.Y + (work.Height - height) / 2,
            _ => work.Y + work.Height - height - margin,
        };

        return new RectInt32(x, y, width, height);
    }

    private void BeginFadeIn()
    {
        if (!_hasDesktopBackdrop)
        {
            _panel.Opacity = 1;
            return;
        }

        var ms = Math.Clamp(_config.ToastFadeInMs, 0, 1000);
        AnimateOpacity(0, 1, ms);
    }

    private void BeginFadeOut()
    {
        if (_closed || _fadingOut)
            return;
        _fadingOut = true;
        _timer.Stop();

        // 没有桌面截图时淡出会露出窗口黑底，直接关闭。
        if (!_hasDesktopBackdrop)
        {
            CloseToast();
            return;
        }

        var ms = Math.Clamp(_config.ToastFadeOutMs, 0, 2000);
        AnimateOpacity(_panel.Opacity, 0, ms, CloseToast);
    }

    private void AnimateOpacity(double from, double to, int milliseconds, Action? completed = null)
    {
        if (milliseconds <= 0)
        {
            _panel.Opacity = to;
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EnableDependentAnimation = true,
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, _panel);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        if (completed is not null)
            storyboard.Completed += (_, _) => completed();
        storyboard.Begin();
    }

    private void CloseToast()
    {
        if (_closed)
            return;
        _closed = true;
        _timer.Stop();
        try
        {
            Close();
        }
        catch
        {
        }
        if (ReferenceEquals(_current, this))
            _current = null;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, int insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
