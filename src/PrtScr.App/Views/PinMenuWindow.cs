using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using Microsoft.Win32;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using PrtScr.App.Services;

namespace PrtScr.App.Views;

/// <summary>
/// 贴图右键菜单：外观是 WinUI 3，但不再用 MenuFlyout 的锚点定位。
/// 菜单窗口本身就是菜单面板，按鼠标屏幕坐标定位，避免 flyout 在 2x2 锚点上算错位置。
/// 点击菜单外由低级鼠标钩子关闭；关闭菜单的那一下同时打上 PinClickGuard，避免切到贴图。
/// </summary>
public sealed partial class PinMenuWindow : Window
{
    public const int CmdCopy = 1;
    public const int CmdClose = 2;
    public const int CmdEdit = 3;
    public const int CmdOcr = 4;
    public const int CmdSaveAs = 5;
    public const int CmdCrop = 6;
    public const int CmdHistory = 7;
    public const int CmdToggleLock = 8;
    public const int CmdToggleThrough = 9;
    public const int CmdOpacity100 = 10;
    public const int CmdOpacity75 = 11;
    public const int CmdOpacity50 = 12;
    public const int CmdOpacity25 = 13;

    private readonly nint _owner;
    private readonly double _opacity;
    private readonly bool _locked;
    private readonly bool _clickThrough;
    private readonly Action<int> _done;
    private readonly int _screenX;
    private readonly int _screenY;
    private readonly nint _hwnd;
    private readonly List<Button> _rows = new();
    private bool _finished;
    private bool _opacityOpen;
    private nint _mouseHook;
    private LowLevelMouseProc? _mouseProc;
    private KeyInterceptor? _borderFix;
    private SolidColorBrush _textBrush = new(Colors.White);
    private SolidColorBrush _hoverBrush = new(Color.FromArgb(30, 255, 255, 255));
    private SolidColorBrush _pressedBrush = new(Color.FromArgb(46, 255, 255, 255));
    private SolidColorBrush _separatorBrush = new(Color.FromArgb(44, 255, 255, 255));
    private static PinMenuWindow? _current;

    /// <summary>贴图线程调用：回到主 UI 线程弹出菜单，并等待选择结果。</summary>
    public static int Show(nint owner, int screenX, int screenY, double opacity,
        bool locked = false, bool clickThrough = false)
    {
        var queue = App.UiQueue;
        if (queue is null)
        {
            App.Log("Pin menu: 主 UI 队列不可用");
            return 0;
        }

        var result = 0;
        var done = new ManualResetEventSlim(false);
        var enqueued = queue.TryEnqueue(() =>
        {
            try
            {
                var window = new PinMenuWindow(owner, screenX, screenY, opacity, locked, clickThrough, command =>
                {
                    result = command;
                    done.Set();
                });
                window.ShowMenu();
            }
            catch (Exception ex)
            {
                App.Log($"Pin menu: WinUI 菜单创建失败 {ex}");
                done.Set();
            }
        });

        if (!enqueued)
            return 0;

        if (!done.Wait(TimeSpan.FromMinutes(5)))
        {
            App.Log("Pin menu: 等待菜单结果超时");
            return 0;
        }

        return result;
    }

    private PinMenuWindow(nint owner, int screenX, int screenY, double opacity,
        bool locked, bool clickThrough, Action<int> done)
    {
        _owner = owner;
        _screenX = screenX;
        _screenY = screenY;
        _opacity = opacity;
        _locked = locked;
        _clickThrough = clickThrough;
        _done = done;
        _current = this;

        InitializeComponent();
        Title = "IshWY 菜单";
        AppWindow.IsShownInSwitchers = false;
        _hwnd = WindowNative.GetWindowHandle(this);

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
        // 去掉 WinUI 无边框窗口仍保留的 1px 非客户区，否则菜单会有一圈白边。
        _borderFix = new KeyInterceptor(_hwnd);
        _borderFix.MakeClientAreaFullWindow();
        ApplyDwmChrome();

        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch (Exception backdropEx)
        {
            App.Log($"Pin menu: 亚克力背景不可用 {backdropEx.Message}");
        }

        ApplyTheme();
        BuildRows();

        // 先估一个尺寸并把窗口放到鼠标处，Loaded 后再按实际内容精确收缩。
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        AppWindow.MoveAndResize(new RectInt32(
            Math.Max(0, _screenX),
            Math.Max(0, _screenY),
            Math.Max(220, (int)(216 * scale)),
            Math.Max(320, (int)(360 * scale))));

        Panel.Opacity = 0;
        Panel.Loaded += (_, _) =>
        {
            SizeAndPlace();
            FadeIn();
        };
        Panel.KeyDown += Panel_KeyDown;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
                DispatcherQueue.TryEnqueue(() => Complete(0));
        };
        Closed += (_, _) =>
        {
            _borderFix?.Dispose();
            _borderFix = null;
            UninstallClickHook();
            if (ReferenceEquals(_current, this))
                _current = null;
        };
    }

    private void ShowMenu()
    {
        App.Log($"Pin menu: WinUI open at cursor {_screenX},{_screenY}");
        InstallClickHook();
        Activate();
    }

    private void ApplyDwmChrome()
    {
        try
        {
            // 让 DWM 按圆角窗口处理，并去掉系统给无边框窗口补的方形 1px 边。
            var preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
            var none = unchecked((int)DwmwaColorNone);
            _ = DwmSetWindowAttribute(_hwnd, DwmwaBorderColor, ref none, sizeof(int));
        }
        catch
        {
        }
    }

    private void ApplyTheme()
    {
        var light = IsLightTheme();
        // 亚克力由 SystemBackdrop 提供，这里只叠一层很薄的色调保证文字可读。
        Panel.Background = new SolidColorBrush(light
            ? Color.FromArgb(226, 250, 250, 252)
            : Color.FromArgb(232, 30, 30, 34));
        Panel.BorderBrush = new SolidColorBrush(light
            ? Color.FromArgb(32, 0, 0, 0)
            : Color.FromArgb(44, 255, 255, 255));
        _textBrush = new SolidColorBrush(light
            ? Color.FromArgb(255, 32, 32, 36)
            : Color.FromArgb(255, 244, 246, 250));
        _separatorBrush = new SolidColorBrush(light
            ? Color.FromArgb(26, 0, 0, 0)
            : Color.FromArgb(44, 255, 255, 255));
        _hoverBrush = new SolidColorBrush(light
            ? Color.FromArgb(20, 0, 0, 0)
            : Color.FromArgb(30, 255, 255, 255));
        _pressedBrush = new SolidColorBrush(light
            ? Color.FromArgb(32, 0, 0, 0)
            : Color.FromArgb(46, 255, 255, 255));
    }

    private void BuildRows(bool opacityOpen = false)
    {
        _opacityOpen = opacityOpen;
        ItemsPanel.Children.Clear();
        _rows.Clear();

        if (opacityOpen)
        {
            AddRow("返回", "", -2);
            AddSeparator();
            AddRow("100%", "", CmdOpacity100, check: Math.Abs(_opacity * 100 - 100) < 1);
            AddRow("75%", "", CmdOpacity75, check: Math.Abs(_opacity * 100 - 75) < 1);
            AddRow("50%", "", CmdOpacity50, check: Math.Abs(_opacity * 100 - 50) < 1);
            AddRow("25%", "", CmdOpacity25, check: Math.Abs(_opacity * 100 - 25) < 1);
            return;
        }

        AddRow("复制图片", "", CmdCopy);
        AddRow("标注…", "", CmdEdit);
        AddRow("裁剪…", "", CmdCrop);
        AddRow("识别文字", "", CmdOcr);
        AddRow("另存为…", "", CmdSaveAs);
        AddRow("打开截图历史", "", CmdHistory);
        AddRow("锁定位置", "", CmdToggleLock, check: _locked);
        AddRow("点击穿透", "", CmdToggleThrough, check: _clickThrough);
        AddSeparator();
        AddRow("不透明度", "", -1, chevron: true);
        AddSeparator();
        AddRow("关闭贴图", "", CmdClose);
    }

    private void AddRow(string text, string glyph, int command, bool chevron = false, bool check = false)
    {
        var button = new Button
        {
            Tag = command,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(9, 7, 10, 7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(6),
            MinHeight = 0,
            Foreground = _textBrush,
            FontSize = 13,
        };
        button.Resources["ButtonBackgroundPointerOver"] = _hoverBrush;
        button.Resources["ButtonBackgroundPressed"] = _pressedBrush;
        button.Resources["ButtonBackground"] = new SolidColorBrush(Colors.Transparent);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = _textBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (chevron)
        {
            var arrow = new FontIcon
            {
                Glyph = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = _textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.65,
            };
            Grid.SetColumn(arrow, 2);
            grid.Children.Add(arrow);
        }
        else if (check)
        {
            var mark = new FontIcon
            {
                Glyph = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = _textBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(mark, 2);
            grid.Children.Add(mark);
        }

        button.Content = grid;
        button.Click += (_, _) =>
        {
            if (command == -1)
            {
                BuildRows(opacityOpen: true);
                SizeAndPlace();
                FocusFirst();
            }
            else if (command == -2)
            {
                BuildRows(opacityOpen: false);
                SizeAndPlace();
                FocusFirst();
            }
            else
            {
                Complete(command);
            }
        };

        _rows.Add(button);
        ItemsPanel.Children.Add(button);
    }

    private void AddSeparator()
    {
        ItemsPanel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(6, 3, 6, 3),
            Background = _separatorBrush,
        });
    }

    private void SizeAndPlace()
    {
        if (_finished)
            return;

        Panel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var scale = Panel.XamlRoot?.RasterizationScale ?? (GetDpiForWindow(_hwnd) / 96.0);
        var w = Math.Max(190, (int)Math.Ceiling(Panel.DesiredSize.Width * scale));
        var h = Math.Max(40, (int)Math.Ceiling(Panel.DesiredSize.Height * scale));
        var screenW = GetSystemMetrics(0);
        var screenH = GetSystemMetrics(1);
        var x = Math.Clamp(_screenX, 0, Math.Max(0, screenW - w));
        var y = Math.Clamp(_screenY, 0, Math.Max(0, screenH - h));

        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        ApplyRoundedRegion(w, h);
        // SystemBackdrop/MoveAndResize 可能重置 DWM 边框属性，尺寸落定后再设一次。
        ApplyDwmChrome();
        _borderFix?.MakeClientAreaFullWindow();
        App.Log($"Pin menu: placed {x},{y} {w}x{h} scale={scale:0.00}");
        FocusFirst();
    }

    private void FadeIn()
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(90)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, Panel);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void FocusFirst()
    {
        if (_rows.Count == 0)
            return;
        _rows[0].Focus(FocusState.Programmatic);
    }

    private void Panel_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Complete(0);
            return;
        }

        if (e.Key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down))
            return;

        e.Handled = true;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Panel.XamlRoot) as Button;
        var index = focused is null ? -1 : _rows.IndexOf(focused);
        if (index < 0)
            index = e.Key == Windows.System.VirtualKey.Down ? 0 : _rows.Count - 1;
        else
            index += e.Key == Windows.System.VirtualKey.Down ? 1 : -1;
        index = Math.Clamp(index, 0, _rows.Count - 1);
        _rows[index].Focus(FocusState.Programmatic);
    }

    private void Complete(int command)
    {
        if (_finished)
            return;
        _finished = true;
        UninstallClickHook();
        _done(command);
        try
        {
            Close();
        }
        catch
        {
        }

        if (_owner != 0)
            _ = SetForegroundWindow(_owner);
        App.Log($"Pin menu: command={command}");
    }

    private void InstallClickHook()
    {
        if (_mouseHook != 0)
            return;
        _mouseProc = ClickProc;
        _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, GetModuleHandleW(null), 0);
        App.Log($"Pin menu: click hook={_mouseHook != 0}");
    }

    private void UninstallClickHook()
    {
        if (_mouseHook == 0)
            return;
        _ = UnhookWindowsHookEx(_mouseHook);
        _mouseHook = 0;
        _mouseProc = null;
    }

    private static nint ClickProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var message = (uint)wParam;
            if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                PinClickGuard.Suppress();
                _current?.CheckOutside(lParam);
            }
        }
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    private void CheckOutside(nint lParam)
    {
        if (_finished)
            return;
        var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
        if (!GetWindowRect(_hwnd, out var rect))
            return;
        if (data.pt.X < rect.left || data.pt.X >= rect.right ||
            data.pt.Y < rect.top || data.pt.Y >= rect.bottom)
        {
            DispatcherQueue.TryEnqueue(() => Complete(0));
        }
    }

    private static bool IsLightTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int i && i != 0;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyRoundedRegion(int width, int height)
    {
        var rgn = CreateRoundRectRgn(0, 0, width + 1, height + 1, 18, 18);
        if (rgn == 0)
            return;
        if (SetWindowRgn(_hwnd, rgn, true) == 0)
            _ = DeleteObject(rgn);
    }

    private const int GwlExStyle = -20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;
    private const uint DwmwaColorNone = 0xFFFFFFFE;
    private const int WsExToolWindow = 0x00000080;
    private const int WH_MOUSE_LL = 14;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MBUTTONDOWN = 0x0207;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public NativePoint pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, nint hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint hRgn, bool redraw);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}