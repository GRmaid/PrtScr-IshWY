using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using PrtScr.Capture;
using PrtScr.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;

namespace PrtScr.App.Views;

/// <summary>
/// 冻屏取景覆盖窗：整屏静态截图 + 遮罩，支持窗口吸附、拖拽框选、回车/双击确认进剪贴板。
/// 坐标体系：XAML 层全部用 DIP；物理像素 = DIP × RasterizationScale；虚拟屏坐标 = 显示器原点 + 物理坐标。
/// 交互语义（微信同款）：按下记住悬浮窗；拖动超过阈值 = 自由框选；纯点击 = 选中/取消窗口。
/// </summary>
public sealed partial class CaptureOverlayWindow : Window
{
    private const double DragThresholdSq = 16;   // 4 DIP 的平方

    private Bitmap _full;
    private WindowRect _monitor;
    private IReadOnlyList<WindowInfo> _windows;
    private readonly nint _selfHwnd;

    private double _scale = 1;
    private bool _dragging;
    private bool _dragMoved;
    private bool _hasSelection;
    private bool _includeOverlays;                       // 是否识别悬浮类窗口（Tab 切换）
    private Windows.Foundation.Point _pressPoint;
    private Windows.Foundation.Point _lastHoverPoint;
    private Rect _selection;            // DIP，相对本显示器
    private WindowInfo? _hoverWindow;   // 鼠标下吸附的窗口
    private WindowInfo? _clickedWindow; // 按下那一刻吸附的窗口
    private WindowInfo? _subregionCandidate; // 本次拖拽可用的“窗口局部”目标
    private WindowInfo? _subregionWindow;    // 当前选区属于该窗口的局部（从窗口本体裁剪）
    private WindowInfo? _lastTipWindow; // 尺寸提示当前显示的窗口（避免每帧重设文本）
    private readonly List<WindowInfo> _hoverCandidates = new();   // 光标下按 Z 序排列的全部候选窗口（复用，零分配）
    private DateTimeOffset _lastPress;
    private bool _closed;
    private bool _saving;
    private PrtScr.App.Services.KeyInterceptor? _keys;

    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _normalHoverBrush =
        new(Windows.UI.Color.FromArgb(0xFF, 0x00, 0xC8, 0xFF));
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _overlayHoverBrush =
        new(Microsoft.UI.Colors.Orange);

    /// <summary>取景结束（确认或取消）后触发。窗口不销毁，只隐藏复用——避免每次 F1 出现白底首帧爆闪。</summary>
    public event Action? Dismissed;

    public CaptureOverlayWindow()
    {
        _full = new Bitmap(1, 1);
        _monitor = default;
        _windows = Array.Empty<WindowInfo>();
        _selfHwnd = WindowNative.GetWindowHandle(this);

        InitializeComponent();
        Title = $"{PrtScr.App.AppInfo.Name} 取景";

        // 消息层拦截 Enter/Esc/Tab：不依赖 XAML 焦点，杜绝偶发失灵
        _keys = new PrtScr.App.Services.KeyInterceptor(WindowNative.GetWindowHandle(this), interceptO: true);
        _keys.KeyDown += vk => DispatcherQueue.TryEnqueue(() =>
        {
            switch (vk)
            {
                case 0x0D:
                    if (_hasSelection)
                        _ = ConfirmAsync();
                    break;
                case 0x1B:
                    Cancel();
                    break;
                case 0x09:
                    ToggleOverlays();
                    break;
                case 0x4F:
                    if (_hasSelection)
                        _ = OcrSelectionAsync();
                    break;
            }
        });

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
        }

        // 首次亮窗时窗口可能尚未就绪，Focus 会被静默丢弃 → Tab/回车失灵；
        // 每次激活都补焦，另有 60ms 延迟补投兜底
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                _ = FocusSink.Focus(FocusState.Programmatic);
                RefreshHoverFromCursor();
            }
        };

        // 注：不做“失焦自动取消”——在 Activated 事件回调里调 Close() 会触发 XAML
        // 暂存异常（0xc000027b）直接杀进程。取消路径：Esc / 再按一次热键
        Root.SizeChanged += (_, _) =>
        {
            try
            {
                _scale = Root.XamlRoot?.RasterizationScale ?? _scale;
                if (_scale <= 0)
                    _scale = 1;
                UpdateVisuals();
            }
            catch (Exception ex)
            {
                App.Log($"Overlay SizeChanged 异常: {ex.Message}");
            }
        };

        Closed += (_, _) => _full.Dispose();
    }

    /// <summary>
    /// 开始一次新取景（窗口复用：只换内容与位置，不再创建/销毁窗口）。
    /// 调用前窗口应处于隐藏状态。
    /// </summary>
    public void Prepare(Bitmap frozen, WindowRect monitor, IReadOnlyList<WindowInfo> windows)
    {
        _full.Dispose();
        _full = frozen;
        _monitor = monitor;
        _windows = windows
            .Where(w => !w.IsCloaked && w.Handle != _selfHwnd && w.FrameBounds.Width > 0)
            .ToList();

        ResetSessionState();

        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height));

        // 布局完成前就把遮罩按物理尺寸铺好：亮窗第一帧即完整画面
        _includeOverlays = App.Config.Current.CaptureOverlayWindows;
        _scale = DetectScale();
        UpdateVisuals();

        InitializeFrozen();
        App.Log("Overlay: prepared");
    }

    private void ResetSessionState()
    {
        _closed = false;
        _dragging = false;
        _dragMoved = false;
        _hasSelection = false;
        _selection = Rect.Empty;
        _hoverWindow = null;
        _clickedWindow = null;
        _subregionWindow = null;
        _subregionCandidate = null;
        _lastTipWindow = null;
        _hoverCandidates.Clear();
        _lastHoverPoint = default;
        _lastPress = default;
        _saving = false;
        SelectionBorder.Visibility = Visibility.Collapsed;
        HoverBorder.Visibility = Visibility.Collapsed;
        SizeTip.Visibility = Visibility.Collapsed;
        SaveButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 启动时离屏热身：把窗口在屏幕外以 1×1 亮一次再隐藏。
    /// 全屏无边框窗口的“首帧白底”只会在第一次呈现时出现——让它消耗在启动阶段。
    /// </summary>
    public void WarmUpHidden()
    {
        try
        {
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-10000, -10000, 1, 1));
            // 用 Show 而非 Activate：不抢激活链，避免影响主窗口/托盘菜单的宿主窗口
            AppWindow.Show();
            AppWindow.Hide();
        }
        catch (Exception ex)
        {
            App.Log($"Overlay 预热失败: {ex.Message}");
        }
    }

    /// <summary>窗口创建期（未激活、XamlRoot 不可用）先从窗口 DPI 推算缩放。</summary>
    private double DetectScale()
    {
        try
        {
            var hwnd = new HWND(WindowNative.GetWindowHandle(this));
            var dpi = PInvoke.GetDpiForWindow(hwnd);
            if (dpi > 0)
                return dpi / 96.0;
        }
        catch
        {
            // 拿不到就先用 1，SizeChanged 会纠正
        }
        return 1;
    }

    /// <summary>
    /// 冻结画面直接按像素拷进 WriteableBitmap（省掉 PNG 编码/解码约几百毫秒延迟——
    /// 延迟越长，亮窗时屏幕内容与冻结画面差异越大，闪屏感越明显）。
    /// 传入的位图本身就是显示器本地区域，无需再裁剪。
    /// </summary>
    private void InitializeFrozen()
    {
        try
        {
            var writeable = new WriteableBitmap(_monitor.Width, _monitor.Height);

            // 冻结图统一是“显示器本地”语义（常驻池与 BitBlt 兜底都裁到显示器区域）
            var w = Math.Min(_monitor.Width, _full.Width);
            var h = Math.Min(_monitor.Height, _full.Height);
            using var region = _full.Clone(
                new System.Drawing.Rectangle(0, 0, w, h),
                PixelFormat.Format32bppArgb);

            var data = region.LockBits(
                new System.Drawing.Rectangle(0, 0, region.Width, region.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                using var target = writeable.PixelBuffer.AsStream();
                // stride 与 WriteableBitmap 行宽一致（32bpp 4 字节对齐）时整块拷；
                // 不一致则逐行拷，防止斜纹错位
                var rowBytes = w * 4;
                if (data.Stride == _monitor.Width * 4)
                {
                    target.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    for (var y = 0; y < h; y++)
                        target.Write(bytes, y * data.Stride, rowBytes);
                }
            }
            finally
            {
                region.UnlockBits(data);
            }

            writeable.Invalidate();
            Frozen.Source = writeable;
            App.Log("Overlay: frozen image ready");

            // 强制完成布局，再亮窗——避免首帧白底/空帧造成全屏爆闪
            try
            {
                Root.UpdateLayout();
            }
            catch
            {
            }

            Activate();
            App.Log($"Overlay: activated, scale dpi={_scale:0.###} xaml={Root.XamlRoot?.RasterizationScale:0.###}");
            _scale = Root.XamlRoot?.RasterizationScale ?? _scale;
            if (_scale <= 0)
                _scale = 1;
            RefreshHoverFromCursor();
            _ = FocusSink.Focus(FocusState.Programmatic);
            DispatcherQueue.TryEnqueue(() => _ = FocusSink.Focus(FocusState.Programmatic));
            var focusRetry = DispatcherQueue.CreateTimer();
            focusRetry.Interval = TimeSpan.FromMilliseconds(60);
            focusRetry.IsRepeating = false;
            focusRetry.Tick += (_, _) =>
            {
                _ = FocusSink.Focus(FocusState.Programmatic);
                RefreshHoverFromCursor();
            };
            focusRetry.Start();
            DispatcherQueue.TryEnqueue(UpdateVisuals);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay 初始化异常: {ex}");
        }
    }

    // ---------- 交互 ----------

    private void Layer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            HandlePressed(e);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay PointerPressed 异常: {ex.Message}");
        }
    }

    private void HandlePressed(PointerRoutedEventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var point = e.GetCurrentPoint(Root).Position;
        Layer.CapturePointer(e.Pointer);

        // 双击已有选区 = 确认
        if (_hasSelection && (now - _lastPress).TotalMilliseconds < 500 &&
            _selection.Contains(point))
        {
            _lastPress = now;
            _ = ConfirmAsync();
            return;
        }
        _lastPress = now;

        // 按下：记住吸附窗口。拖动 = 自由框选；不动 = 单击选中窗口
        _pressPoint = point;
        _dragging = true;
        _dragMoved = false;
        _clickedWindow = _hoverWindow;
        _subregionWindow = null;
        // 悬浮在普通窗口上开始拖拽 = 窗口局部（悬浮类窗口仍按屏幕所见处理）
        _subregionCandidate = _hoverWindow is { IsOverlayClass: false } && _hoverWindow.FrameBounds.Width > 0
            ? _hoverWindow
            : null;
        if (_clickedWindow is not null)
        {
            _selection = DipRect(_clickedWindow.FrameBounds);
            _hasSelection = true;
        }
        else
        {
            _selection = Rect.Empty;
            _hasSelection = false;
        }
        UpdateVisuals();
    }

    private void Layer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            HandleMoved(e);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay PointerMoved 异常: {ex.Message}");
        }
    }

    private void HandleMoved(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root).Position;

        if (_dragging)
        {
            if (!_dragMoved)
            {
                var dx = point.X - _pressPoint.X;
                var dy = point.Y - _pressPoint.Y;
                if (dx * dx + dy * dy > DragThresholdSq)
                    _dragMoved = true;
            }

            if (_dragMoved)
            {
                _hoverWindow = null;
                _clickedWindow = null;

                var rect = Normalize(_pressPoint, point);
                if (_subregionCandidate is { } target)
                {
                    _subregionWindow = target;
                    var wr = DipRect(target.FrameBounds);
                    var left = Math.Max(rect.X, wr.X);
                    var top = Math.Max(rect.Y, wr.Y);
                    var right = Math.Min(rect.X + rect.Width, wr.X + wr.Width);
                    var bottom = Math.Min(rect.Y + rect.Height, wr.Y + wr.Height);
                    rect = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
                }
                else
                {
                    _subregionWindow = null;
                }

                _selection = rect;
                _hasSelection = true;
            }

            UpdateVisuals();
            return;
        }

        UpdateHover(point);
    }

    private void Layer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            HandleReleased(e);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay PointerReleased 异常: {ex.Message}");
        }
    }

    private void HandleReleased(PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        Layer.ReleasePointerCapture(e.Pointer);
        _dragging = false;

        // 焦点拉回锚点，保证 Esc/回车/Tab 随时可用
        _ = FocusSink.Focus(FocusState.Programmatic);

        if (!_dragMoved && _clickedWindow is null)
        {
            // 空白处纯点击 = 清除选区
            _selection = Rect.Empty;
            _hasSelection = false;
        }
        else if (_dragMoved && _selection.Width * _selection.Height < 16)
        {
            // 拖拽距离太短 = 无效选区
            _selection = Rect.Empty;
            _hasSelection = false;
            _subregionWindow = null;
        }

        UpdateVisuals();
    }

    private void UpdateHover(Windows.Foundation.Point dipPoint)
    {
        _lastHoverPoint = dipPoint;
        HitTestHover(dipPoint);
        UpdateVisuals();
    }

    /// <summary>
    /// 取景窗亮起时鼠标往往没有移动，PointerMoved 不会触发；
    /// 主动按当前光标做一次命中，避免“必须先动一下鼠标才认窗口”。
    /// </summary>
    private void RefreshHoverFromCursor()
    {
        try
        {
            if (_closed || _scale <= 0)
                return;
            if (!PInvoke.GetCursorPos(out var pt))
                return;
            var dip = new Windows.Foundation.Point(
                (pt.X - _monitor.X) / _scale,
                (pt.Y - _monitor.Y) / _scale);
            UpdateHover(dip);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay 初始命中异常: {ex.Message}");
        }
    }

    private void HitTestHover(Windows.Foundation.Point dipPoint)
    {
        // DIP → 虚拟屏物理坐标
        var vx = _monitor.X + (int)Math.Round(dipPoint.X * _scale);
        var vy = _monitor.Y + (int)Math.Round(dipPoint.Y * _scale);

        // 枚举列表本身按 Z 序（顶层在前）；纯净模式跳过悬浮类窗口（歌词/桌宠/会议提示），Tab 可切换。
        // 注意命中的是“矩形包含该点”的全部窗口——被遮挡的窗口也在候选里，滚轮可轮换。
        // 复用列表，鼠标高频移动下零分配（分配会引起 GC 停顿 → 闪动感）
        _hoverCandidates.Clear();
        foreach (var w in _windows)
        {
            if (w.FrameBounds.Contains(vx, vy) &&
                w.FrameBounds.Width < _monitor.Width * 2 &&
                (_includeOverlays || !w.IsOverlayClass))
            {
                _hoverCandidates.Add(w);
            }
        }

        _hoverWindow = _hoverCandidates.Count > 0 ? _hoverCandidates[0] : null;
    }

    private void Layer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (_dragging)
                return;

            var delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
            CycleHover(delta);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay 滚轮异常: {ex.Message}");
        }
    }

    /// <summary>滚轮循环切换光标下的候选窗口（顶层→底层），被遮挡的窗口由此可达（微信同款）。</summary>
    private void CycleHover(int delta)
    {
        if (_hoverCandidates.Count == 0)
        {
            HitTestHover(_lastHoverPoint);
            if (_hoverCandidates.Count == 0)
                return;
        }

        var currentIndex = _hoverWindow is null
            ? -1
            : _hoverCandidates.FindIndex(c => c.Handle == _hoverWindow.Handle);

        var nextIndex = delta > 0 ? currentIndex + 1 : currentIndex - 1;
        if (nextIndex < 0)
            nextIndex = _hoverCandidates.Count - 1;
        if (nextIndex >= _hoverCandidates.Count)
            nextIndex = 0;

        _hoverWindow = _hoverCandidates[nextIndex];

        // 已处于“窗口选中”状态时，滚轮同时切换选中的目标
        if (_hasSelection && _clickedWindow is not null)
        {
            _clickedWindow = _hoverWindow;
            _selection = DipRect(_clickedWindow.FrameBounds);
        }

        UpdateVisuals();
    }

    private void OnToggleOverlays(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleOverlays();
    }

    private void ToggleOverlays()
    {
        try
        {
            _includeOverlays = !_includeOverlays;
            _lastTipWindow = null;   // 强制刷新提示文本
            HitTestHover(_lastHoverPoint);   // 立即按新策略重新命中
        }
        catch (Exception ex)
        {
            App.Log($"Overlay Tab 切换异常: {ex.Message}");
        }
    }

    /// <summary>焦点锚的键盘直达：Esc/回车/Tab 全部不依赖窗口焦点漂移。</summary>
    private void FocusSink_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                Cancel();
                break;

            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                if (_hasSelection)
                    _ = ConfirmAsync();
                break;

            case Windows.System.VirtualKey.Tab:
                e.Handled = true;
                ToggleOverlays();
                break;
        }
    }

    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Cancel();
    }

    private void OnEnter(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_hasSelection)
            _ = ConfirmAsync();
    }

    // ---------- 收尾 ----------

    public void Cancel() => Dismiss();

    /// <summary>隐藏窗口（复用），并通知服务本次取景结束。</summary>
    private void Dismiss()
    {
        if (_closed)
            return;
        _closed = true;
        AppWindow.Hide();

        // 取景器常驻复用，但冻结图（2560×1600 WriteableBitmap + GDI Bitmap 各约 16 MB）
        // 不能一直挂在窗口上；隐藏后立即释放，下次 Prepare 会重新填充。
        try
        {
            Frozen.Source = null;
            _full.Dispose();
            // 这两块（WriteableBitmap + GDI Bitmap）各约 16 MB，且 PixelBuffer 会进大对象堆。
            // 不主动收一次，下次截图前私有内存会一直停在 ~120 MB；收一次回到 ~60 MB。
            var managedBefore = GC.GetTotalMemory(false);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            var managedAfter = GC.GetTotalMemory(false);
            App.Log($"Overlay: frozen buffers released, managed={managedBefore / 1048576}->{managedAfter / 1048576}MB");
        }
        catch (Exception ex)
        {
            App.Log($"Overlay cleanup 异常: {ex.Message}");
        }
        _full = new Bitmap(1, 1);

        Dismissed?.Invoke();
    }

    /// <summary>把选区按“窗口矩形 → 捕获位图”的比率映射到窗口图上并裁剪。</summary>
    private Bitmap? CropFromWindow(Bitmap windowBitmap, WindowInfo window)
    {
        var sel = _selection;
        if (sel.IsEmpty || windowBitmap.Width <= 0 || windowBitmap.Height <= 0)
            return null;

        var frame = window.FrameBounds;
        if (frame.Width <= 0 || frame.Height <= 0)
            return null;

        // 选区 DIP → 显示器本地物理 → 虚拟屏物理 → 窗口内相对坐标
        var vx0 = _monitor.X + sel.X * _scale;
        var vy0 = _monitor.Y + sel.Y * _scale;
        var vx1 = _monitor.X + (sel.X + sel.Width) * _scale;
        var vy1 = _monitor.Y + (sel.Y + sel.Height) * _scale;

        // 捕获位图与窗口矩形可能存在几像素边框差，按比率归一化
        var sx = windowBitmap.Width / (double)frame.Width;
        var sy = windowBitmap.Height / (double)frame.Height;

        var x0 = (int)Math.Round((vx0 - frame.X) * sx);
        var y0 = (int)Math.Round((vy0 - frame.Y) * sy);
        var x1 = (int)Math.Round((vx1 - frame.X) * sx);
        var y1 = (int)Math.Round((vy1 - frame.Y) * sy);

        x0 = Math.Clamp(x0, 0, windowBitmap.Width - 1);
        y0 = Math.Clamp(y0, 0, windowBitmap.Height - 1);
        x1 = Math.Clamp(x1, x0 + 1, windowBitmap.Width);
        y1 = Math.Clamp(y1, y0 + 1, windowBitmap.Height);

        var cropped = windowBitmap.Clone(
            new System.Drawing.Rectangle(x0, y0, x1 - x0, y1 - y0),
            PixelFormat.Format32bppArgb);
        App.Log($"Capture: 窗口局部 {x1 - x0}×{y1 - y0}（窗口 {frame.Width}×{frame.Height}，捕获图 {windowBitmap.Width}×{windowBitmap.Height}）");
        return cropped;
    }

    /// <summary>按当前选区/窗口取回一张位图（窗口模式抗遮挡；否则裁冻结画面）。调用方负责 Dispose。</summary>
    private async Task<Bitmap?> CaptureSelectionAsync()
    {
        // 窗口局部：先抓完整窗口（抗遮挡），再按选区裁剪
        if (_subregionWindow is { } sub)
        {
            var fullWindow = await WindowCapture.CaptureAsync(sub);
            if (fullWindow is not null)
            {
                var cropped = CropFromWindow(fullWindow, sub);
                fullWindow.Dispose();
                if (cropped is not null)
                    return cropped;
            }
            App.Log("Capture: 窗口局部捕获失败，回退屏幕裁剪");
        }

        // 窗口模式：PrintWindow 直接取窗口自身内容（抗遮挡，被挡住的部分也完整），
        // 失败（不支持/全黑）则回退 WGC
        if (_clickedWindow is { } win)
        {
            var windowBitmap = await WindowCapture.CaptureAsync(win);
            if (windowBitmap is not null)
                return windowBitmap;
        }

        var dip = _selection;
        if (dip.IsEmpty)
            return null;

        // DIP → 显示器本地物理坐标（冻结位图就是显示器本地区域），并夹在显示器内
        var x = Math.Clamp((int)Math.Round(dip.X * _scale), 0, _monitor.Width - 1);
        var y = Math.Clamp((int)Math.Round(dip.Y * _scale), 0, _monitor.Height - 1);
        var right = Math.Clamp((int)Math.Round((dip.X + dip.Width) * _scale), x + 1, _monitor.Width);
        var bottom = Math.Clamp((int)Math.Round((dip.Y + dip.Height) * _scale), y + 1, _monitor.Height);

        return _full.Clone(
            new System.Drawing.Rectangle(x, y, right - x, bottom - y),
            PixelFormat.Format32bppArgb);
    }

    /// <summary>对当前选区做 OCR（O 键）。</summary>
    private async Task OcrSelectionAsync()
    {
        Bitmap? captured = null;
        try
        {
            captured = await CaptureSelectionAsync();
            if (captured is null)
                return;

            using var ms = new MemoryStream();
            captured.Save(ms, ImageFormat.Png);
            var bytes = ms.ToArray();

            Dismiss();
            await PrtScr.App.Services.OcrUiService.RecognizeAndShowAsync(bytes);
        }
        catch (Exception ex)
        {
            App.Log($"OCR 选区失败: {ex}");
        }
        finally
        {
            captured?.Dispose();
        }
    }

    private async Task ConfirmAsync()
    {
        Bitmap? captured = null;
        try
        {
            captured = await CaptureSelectionAsync();
            if (captured is null)
                return;

            using var ms = new MemoryStream();
            captured.Save(ms, ImageFormat.Png);
            var bytes = ms.ToArray();

            var copyTask = PrtScr.App.Services.ClipboardService.SetImageAsync(bytes);
            var historyTask = PrtScr.App.Services.CaptureHistoryService.Instance.AddAsync(bytes, fullScreen: false);
            await Task.WhenAll(copyTask, historyTask);

            var ok = copyTask.Result;
            App.Log(ok ? "Capture: copied" : "Capture: clipboard final failure");
            if (historyTask.Result is { } savedPath)
                App.Log($"Capture: history saved {savedPath}");
        }
        catch (Exception ex)
        {
            App.Log($"截图确认失败: {ex}");
        }
        finally
        {
            captured?.Dispose();
            Dismiss();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveSelectionAsync();

    private void OnSaveShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_hasSelection)
            _ = SaveSelectionAsync();
    }

    /// <summary>选区另存为 PNG/JPEG；保存成功后同样复制到剪贴板并写入历史，再关闭取景。</summary>
    private async Task SaveSelectionAsync()
    {
        if (!_hasSelection || _closed || _saving)
            return;

        _saving = true;
        Bitmap? captured = null;
        try
        {
            captured = await CaptureSelectionAsync();
            if (captured is null)
                return;

            using var ms = new MemoryStream();
            captured.Save(ms, ImageFormat.Png);
            var bytes = ms.ToArray();

            var path = await PrtScr.App.Services.FileSaveService.SavePngAsync(
                WindowNative.GetWindowHandle(this), bytes, PrtScr.App.Services.FileSaveService.SuggestedName("shot"));
            if (path is null)
            {
                FocusSink.Focus(FocusState.Programmatic);
                return;
            }

            // 另存为不是“只写文件”：保存成功后仍要进剪贴板和截图历史，保持与回车确认一致
            var copyTask = PrtScr.App.Services.ClipboardService.SetImageAsync(bytes);
            var historyTask = PrtScr.App.Services.CaptureHistoryService.Instance.AddAsync(bytes, fullScreen: false);
            await Task.WhenAll(copyTask, historyTask);

            var copied = copyTask.Result;
            App.Log(copied ? "Capture: copied" : "Capture: clipboard final failure");
            App.Log($"Capture: saved as {path}");
            if (historyTask.Result is { } historyPath)
                App.Log($"Capture: history saved {historyPath}");
            Dismiss();
        }
        catch (Exception ex)
        {
            App.Log($"另存为失败: {ex}");
        }
        finally
        {
            captured?.Dispose();
            _saving = false;
        }
    }

    // ---------- 绘制 ----------

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

        var sel = _hasSelection ? _selection : Rect.Empty;

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

            SizeText.Text = hover.IsOverlayClass && !_includeOverlays
                ? "悬浮窗口 · 按 Tab 识别"
                : $"{hover.FrameBounds.Width} × {hover.FrameBounds.Height}"
                    + (_hoverCandidates.Count > 1 ? " · 滚轮切换" : "")
                    + " · 拖拽截窗口局部";
            _lastTipWindow = hover;
            var tipX = Math.Clamp(r.X + r.Width - 110, 4, Math.Max(4, width - 120));
            var tipY = r.Y + r.Height + 6 < height - 30 ? r.Y + r.Height + 6 : r.Y - 30;
            Canvas.SetLeft(SizeTip, tipX);
            Canvas.SetTop(SizeTip, Math.Max(4, tipY));
            SizeTip.Visibility = Visibility.Visible;
        }
        else
        {
            HoverBorder.Visibility = Visibility.Collapsed;
        }

        // 选区边框
        if (_hasSelection)
        {
            SetRect(SelectionBorder, sel.X, sel.Y, sel.Width, sel.Height);
            SelectionBorder.Visibility = Visibility.Visible;

            var prefix = _subregionWindow is not null ? "窗口内容 " : "";
            SizeText.Text = prefix + $"{(int)Math.Round(sel.Width * _scale)} × {(int)Math.Round(sel.Height * _scale)} · 回车复制 / Ctrl+S 保存 / O 识别";
            var tipX = Math.Clamp(sel.Right - SizeTip.ActualWidth - 8, 4, Math.Max(4, width - 120));
            var tipY = sel.Bottom + 6 < height - 30 ? sel.Bottom + 6 : sel.Y - 30;
            Canvas.SetLeft(SizeTip, tipX);
            Canvas.SetTop(SizeTip, Math.Max(4, tipY));
            SizeTip.Visibility = Visibility.Visible;

            // 保存按钮贴在选区右上角内侧；选区太矮时挪到下方，避免压住尺寸提示
            var saveW = SaveButton.ActualWidth > 0 ? SaveButton.ActualWidth : 56;
            var saveH = Math.Max(26, SaveButton.ActualHeight);
            var saveX = Math.Clamp(sel.Right - saveW - 6, 4, Math.Max(4, width - saveW - 4));
            var saveY = sel.Height >= saveH + 12 ? sel.Y + 6 : sel.Bottom + 6;
            Canvas.SetLeft(SaveButton, saveX);
            Canvas.SetTop(SaveButton, Math.Clamp(saveY, 4, Math.Max(4, height - saveH - 4)));
            SaveButton.Visibility = Visibility.Visible;
        }
        else
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeTip.Visibility = Visibility.Collapsed;
            SaveButton.Visibility = Visibility.Collapsed;
        }
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

    private static Rect Normalize(Windows.Foundation.Point a, Windows.Foundation.Point b)
    {
        return new Rect(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));
    }
}
