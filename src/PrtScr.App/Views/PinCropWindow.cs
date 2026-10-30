using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace PrtScr.App.Views;

public enum PinCropAction
{
    Pin,
    Ocr,
    Save,
}

public sealed record PinCropResult(PinCropAction Action, byte[] SourcePngBytes, int Left, int Top, int Width, int Height);

/// <summary>
/// 贴图裁剪取景层：WinUI 3 窗口，视觉与截图取景器统一（白色选区框、深色尺寸提示、
/// 底部 HUD 与选区操作条）。图层由 XAML 合成，拖拽时只更新遮罩和选区框，不整帧上传 GDI。
/// 键盘由 WH_KEYBOARD_LL 钩子直达，不依赖窗口是否拿到前台焦点。
/// </summary>
public sealed partial class PinCropWindow : Window
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkEscape = 0x1B;
    private const int VkReturn = 0x0D;
    private const int VkO = 0x4F;
    private const int VkS = 0x53;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private static int _active;
    public static bool IsActive => Volatile.Read(ref _active) != 0;
    public static long ClosedAtTick;

    private readonly byte[] _pngBytes;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private readonly Action<PinCropResult?> _done;

    private bool _dragging;
    private bool _hasSelection;
    private bool _finished;
    private bool _doneCalled;
    private Point _start;
    private Rect _selection;
    private nint _hook;
    private LowLevelKeyboardProc? _hookProc;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    /// <summary>贴图线程同步等待；窗口在 App 主 UI 线程创建。</summary>
    public static PinCropResult? Show(nint owner, int x, int y, int width, int height, byte[] pngBytes, int pixelWidth, int pixelHeight)
    {
        var queue = App.UiQueue;
        if (queue is null)
        {
            App.Log("Pin crop: 主 UI 队列不可用");
            return null;
        }

        PinCropResult? result = null;
        PinCropWindow? window = null;
        using var done = new ManualResetEventSlim(false);

        var enqueued = queue.TryEnqueue(async () =>
        {
            try
            {
                var image = await DecodeAsync(pngBytes, width);
                window = new PinCropWindow(owner, x, y, width, height, pngBytes, pixelWidth, pixelHeight, image, r =>
                {
                    result = r;
                    done.Set();
                });
                window.Activate();
            }
            catch (Exception ex)
            {
                App.Log($"Pin crop: 窗口创建失败 {ex}");
                done.Set();
            }
        });

        if (!enqueued)
            return null;

        if (!done.Wait(TimeSpan.FromMinutes(5)))
        {
            App.Log("Pin crop: 等待结果超时");
            queue.TryEnqueue(() => window?.Close());
            return null;
        }

        return result;
    }

    private PinCropWindow(
        nint owner,
        int x,
        int y,
        int width,
        int height,
        byte[] pngBytes,
        int pixelWidth,
        int pixelHeight,
        BitmapImage image,
        Action<PinCropResult?> done)
    {
        _pngBytes = pngBytes;
        _pixelWidth = Math.Max(1, pixelWidth);
        _pixelHeight = Math.Max(1, pixelHeight);
        _done = done;

        InitializeComponent();
        Title = "IshWY 裁剪";
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, Math.Max(1, width), Math.Max(1, height)));
        SourceImage.Source = image;

        Root.Loaded += Root_Loaded;
        Closed += Root_Closed;
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Interlocked.Exchange(ref _active, 1);
        InstallHook();
        UpdateVisuals();
        Activate();
    }

    private void Root_Closed(object sender, WindowEventArgs args)
    {
        UninstallHook();
        Interlocked.Exchange(ref _active, 0);
        ClosedAtTick = Environment.TickCount64;
        if (!_doneCalled)
        {
            _doneCalled = true;
            _done(null);
        }
    }

    // ---------- 键盘：低级钩子直达，避免裁剪层拿不到焦点时 Enter/Esc 丢失 ----------

    private void InstallHook()
    {
        _hookProc = HookProc;
        _hook = SetWindowsHookExW(WhKeyboardLl, _hookProc, GetModuleHandleW(null), 0);
        App.Log($"Pin crop: keyboard hook={_hook != 0}");
    }

    private void UninstallHook()
    {
        if (_hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
        _hookProc = null;
    }

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(0, nCode, wParam, lParam);

        if ((long)wParam != WmKeyDown && (long)wParam != WmSysKeyDown)
            return CallNextHookEx(0, nCode, wParam, lParam);

        var vk = Marshal.ReadInt32(lParam);
        var ctrl = (GetKeyState(VkControl) & 0x8000) != 0;
        var shift = (GetKeyState(VkShift) & 0x8000) != 0;
        var alt = (GetKeyState(VkMenu) & 0x8000) != 0;
        var win = (GetKeyState(VkLWin) & 0x8000) != 0 || (GetKeyState(VkRWin) & 0x8000) != 0;

        // Alt / Win 组合留给系统（Alt+Tab、Win+方向键等），裁剪层不吞。
        if (alt || win)
            return CallNextHookEx(0, nCode, wParam, lParam);

        var capture = vk == VkEscape
            || (vk == VkReturn && !ctrl && !shift)
            || (vk == VkO && !ctrl && !shift)
            || (vk == VkS && ctrl && !shift);

        if (!capture)
            return CallNextHookEx(0, nCode, wParam, lParam);

        DispatcherQueue.TryEnqueue(() => HandleKey(vk, ctrl));
        return 1;
    }

    private void HandleKey(int vk, bool ctrl)
    {
        if (_finished)
            return;

        switch (vk)
        {
            case VkEscape:
                Finish(null);
                break;
            case VkReturn:
                if (_hasSelection)
                    Complete(PinCropAction.Pin);
                break;
            case VkO:
                if (_hasSelection)
                    Complete(PinCropAction.Ocr);
                break;
            case VkS when ctrl:
                if (_hasSelection)
                    Complete(PinCropAction.Save);
                break;
        }
    }

    // ---------- 鼠标框选 ----------

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_finished)
            return;

        var point = e.GetCurrentPoint(Root);
        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            Finish(null);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (IsInsideActionBar(e.OriginalSource as DependencyObject))
            return;

        _dragging = true;
        _hasSelection = true;
        _start = point.Position;
        _selection = new Rect(_start, _start);
        Root.CapturePointer(e.Pointer);
        UpdateVisuals();
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _finished)
            return;

        var pos = e.GetCurrentPoint(Root).Position;
        var w = Math.Max(1, Root.ActualWidth);
        var h = Math.Max(1, Root.ActualHeight);
        var x1 = Math.Clamp(Math.Min(_start.X, pos.X), 0, w);
        var y1 = Math.Clamp(Math.Min(_start.Y, pos.Y), 0, h);
        var x2 = Math.Clamp(Math.Max(_start.X, pos.X), 0, w);
        var y2 = Math.Clamp(Math.Max(_start.Y, pos.Y), 0, h);
        _selection = new Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
        UpdateVisuals();
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);

        if (_selection.Width < 4 || _selection.Height < 4)
            _hasSelection = false;

        UpdateVisuals();
    }

    private bool IsInsideActionBar(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ActionBar))
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    // ---------- 视觉 ----------

    private void UpdateVisuals()
    {
        var w = Math.Max(1, Root.ActualWidth);
        var h = Math.Max(1, Root.ActualHeight);

        if (!_hasSelection)
        {
            MaskAll.Visibility = Visibility.Visible;
            MaskTop.Visibility = Visibility.Collapsed;
            MaskBottom.Visibility = Visibility.Collapsed;
            MaskLeft.Visibility = Visibility.Collapsed;
            MaskRight.Visibility = Visibility.Collapsed;
            SelectionBorder.Visibility = Visibility.Collapsed;
            MetaChip.Visibility = Visibility.Collapsed;
            ActionBar.Visibility = Visibility.Collapsed;
            CenterHintPlate.Visibility = Visibility.Visible;
            HudHintText.Text = "拖拽 框选 · Esc / 右键 退出";
            return;
        }

        var sel = _selection;
        MaskAll.Visibility = Visibility.Collapsed;
        CenterHintPlate.Visibility = Visibility.Collapsed;

        MaskTop.Visibility = Visibility.Visible;
        MaskTop.Width = w;
        MaskTop.Height = Math.Max(0, sel.Y);
        Canvas.SetLeft(MaskTop, 0);
        Canvas.SetTop(MaskTop, 0);

        MaskBottom.Visibility = Visibility.Visible;
        MaskBottom.Width = w;
        MaskBottom.Height = Math.Max(0, h - sel.Bottom);
        Canvas.SetLeft(MaskBottom, 0);
        Canvas.SetTop(MaskBottom, sel.Bottom);

        MaskLeft.Visibility = Visibility.Visible;
        MaskLeft.Width = Math.Max(0, sel.X);
        MaskLeft.Height = Math.Max(0, sel.Height);
        Canvas.SetLeft(MaskLeft, 0);
        Canvas.SetTop(MaskLeft, sel.Y);

        MaskRight.Visibility = Visibility.Visible;
        MaskRight.Width = Math.Max(0, w - sel.Right);
        MaskRight.Height = Math.Max(0, sel.Height);
        Canvas.SetLeft(MaskRight, sel.Right);
        Canvas.SetTop(MaskRight, sel.Y);

        SelectionBorder.Visibility = Visibility.Visible;
        SelectionBorder.Margin = new Thickness(sel.X, sel.Y, 0, 0);
        SelectionBorder.Width = Math.Max(0, sel.Width);
        SelectionBorder.Height = Math.Max(0, sel.Height);

        MetaSize.Text = $"{Math.Round(sel.Width)} × {Math.Round(sel.Height)}";
        var pxW = Math.Max(1, (int)Math.Round(sel.Width / w * _pixelWidth));
        var pxH = Math.Max(1, (int)Math.Round(sel.Height / h * _pixelHeight));
        MetaPixels.Text = $"{pxW} × {pxH} px";
        MetaChip.Visibility = Visibility.Visible;

        MetaChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var mw = Math.Max(1, MetaChip.DesiredSize.Width);
        var mh = Math.Max(1, MetaChip.DesiredSize.Height);
        var mx = sel.X + 8;
        var my = sel.Y + 8;
        if (sel.Width < mw + 16 || sel.Height < mh + 16)
        {
            mx = sel.X;
            my = sel.Y - mh - 6;
            if (my < 4)
                my = sel.Bottom + 6;
            if (my + mh > h - 4)
                my = Math.Max(4, sel.Y + 8);
        }
        mx = Math.Clamp(mx, 4, Math.Max(4, w - mw - 4));
        my = Math.Clamp(my, 4, Math.Max(4, h - mh - 4));
        MetaChip.Margin = new Thickness(mx, my, 0, 0);

        ActionBar.Visibility = Visibility.Visible;
        HudHintText.Text = "Enter 贴图 · O 识别 · Ctrl+S 保存 · Esc 退出";
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => Complete(PinCropAction.Pin);

    private void Ocr_Click(object sender, RoutedEventArgs e) => Complete(PinCropAction.Ocr);

    private void Save_Click(object sender, RoutedEventArgs e) => Complete(PinCropAction.Save);

    private void Complete(PinCropAction action)
    {
        if (_finished || !_hasSelection)
            return;

        var w = Math.Max(1, Root.ActualWidth);
        var h = Math.Max(1, Root.ActualHeight);
        var left = (int)Math.Floor(_selection.X / w * _pixelWidth);
        var top = (int)Math.Floor(_selection.Y / h * _pixelHeight);
        var right = (int)Math.Ceiling(_selection.Right / w * _pixelWidth);
        var bottom = (int)Math.Ceiling(_selection.Bottom / h * _pixelHeight);

        left = Math.Clamp(left, 0, _pixelWidth - 1);
        top = Math.Clamp(top, 0, _pixelHeight - 1);
        right = Math.Clamp(right, left + 1, _pixelWidth);
        bottom = Math.Clamp(bottom, top + 1, _pixelHeight);

        var result = new PinCropResult(action, _pngBytes, left, top, right - left, bottom - top);
        App.Log($"Pin crop: selection {result.Width}x{result.Height} at {result.Left},{result.Top}");
        Finish(result);
    }

    private void Finish(PinCropResult? result)
    {
        if (_finished)
            return;
        _finished = true;
        UninstallHook();
        Interlocked.Exchange(ref _active, 0);
        ClosedAtTick = Environment.TickCount64;
        if (!_doneCalled)
        {
            _doneCalled = true;
            _done(result);
        }
        Close();
    }

    private static async Task<BitmapImage> DecodeAsync(byte[] pngBytes, int decodeWidth)
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(pngBytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        stream.Seek(0);

        var image = new BitmapImage { DecodePixelWidth = Math.Max(1, decodeWidth) };
        await image.SetSourceAsync(stream);
        return image;
    }
}
