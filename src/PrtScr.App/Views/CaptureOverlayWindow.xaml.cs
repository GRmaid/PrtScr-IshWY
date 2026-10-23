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

/// <summary>
/// 冻屏取景覆盖窗：整屏静态截图 + 遮罩，支持窗口吸附、拖拽框选、回车/双击确认进剪贴板。
/// 坐标体系：XAML 层全部用 DIP；物理像素 = DIP × RasterizationScale；虚拟屏坐标 = 显示器原点 + 物理坐标。
/// 交互语义（微信同款）：按下记住悬浮窗；拖动超过阈值 = 自由框选；纯点击 = 选中/取消窗口。
/// </summary>
public sealed partial class CaptureOverlayWindow : Window
{
    private const double DragThresholdSq = 16;   // 4 DIP 的平方
    private const int HitTolerancePx = 2;        // 窗口边界容差：贴着边框起手也能选中
    private const int LoupeSampleSize = 19;      // 取色器采样 19×19 像素
    private const int LoupeCell = 10;            // 每像素放大 10 倍 → 190×190
    private const int LoupePixelSize = LoupeSampleSize * LoupeCell;
    private const double ResizeHandleTolerance = 10;  // 选区边缘 10 DIP 内起手 = 缩放（手柄 12 DIP）
    private const double MinSelectionSize = 12;       // 缩放后选区的最小边长（DIP）
    private const int WheelUnitsPerWindow = 240;      // 滚轮 2 格才切一个窗口，避免过快
    private const long WheelResetMs = 400;            // 停顿超过该时长，滚轮累计清零
    private const int OverlayPrevWindowKey = 0x1F0;   // 钩子 → 取景器的内部虚拟键：上一个窗口
    private const int OverlayNextWindowKey = 0x1F1;   // 钩子 → 取景器的内部虚拟键：下一个窗口
    private const int EscPollIntervalMs = 30;         // 取景器可见时轮询 Esc 物理键态（钩子失活兜底）

    private enum CaptureMode
    {
        Window,   // 窗口截图：重建窗口本体，被遮挡也能取
        Normal,   // 常规截图：直接裁冻结画面，菜单/提示/指针所见即所得（默认起始）
    }

    private enum ResizeEdge
    {
        None,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    private Bitmap _full;
    private WindowRect _monitor;
    private IReadOnlyList<WindowInfo> _windows;
    private readonly nint _selfHwnd;

    private double _scale = 1;
    private bool _dragging;
    private bool _dragMoved;
    private bool _hasSelection;
    private CaptureMode _captureMode = CaptureMode.Window;
    private Windows.Foundation.Point _pressPoint;
    private Windows.Foundation.Point _lastHoverPoint;
    private nint _lastHoverHandle;
    private Rect _selection;            // DIP，相对本显示器
    private WindowInfo? _hoverWindow;   // 鼠标下吸附的窗口
    private WindowInfo? _clickedWindow; // 按下那一刻吸附的窗口
    private WindowInfo? _subregionCandidate; // 本次拖拽可用的“窗口局部”目标
    private WindowInfo? _subregionWindow;    // 当前选区属于该窗口的局部（从窗口本体裁剪）
    private bool _movingSelection;      // 按住已有选区内部拖动 = 移动选框（常规/窗口模式都支持）
    private Rect _moveStartSelection;   // 移动开始时的选区，按总位移计算避免累计误差
    private bool _moveWholeWindow;      // 窗口模式下移动的是整窗选区；一旦拖动就脱离窗口，改为冻结画面裁切
    private bool _resizingSelection;    // 拖动选区边缘 = 缩放
    private ResizeEdge _resizeEdge;
    private Rect _resizeStartSelection;
    private Windows.Foundation.Point _resizeStartPoint;
    private double _resizeAnchorX;       // 缩放时保持不动的对边 X（支持拖过对边继续缩放）
    private double _resizeAnchorY;       // 缩放时保持不动的对边 Y
    private System.Threading.Timer? _escPollTimer;   // 钩子失效时仍能收到 Esc
    private int _escPollHeld;
    private int _wheelAccum;            // 滚轮累计量（120 = 一格）
    private long _lastWheelTick;
    private WindowInfo? _lastTipWindow; // 尺寸提示当前显示的窗口（避免每帧重设文本）
    private readonly List<WindowInfo> _hoverCandidates = new();   // 光标下按 Z 序排列的全部候选窗口（复用，零分配）
    private DateTimeOffset _lastPress;
    private volatile bool _closed;
    private bool _saving;
    private bool _confirming;
    private bool _sessionFinished;
    private volatile bool _keysSuspended;   // 文件另存对话框打开时暂停取景器按键
    private PrtScr.App.Services.KeyInterceptor? _keys;

    private WriteableBitmap? _loupeBitmap;
    private readonly byte[] _loupePixels = new byte[LoupePixelSize * LoupePixelSize * 4];
    private readonly byte[] _loupeSample = new byte[LoupeSampleSize * LoupeSampleSize * 4];
    private string _loupeHex = "";
    private string _loupeRgb = "";
    private bool _loupeErrorLogged;
    private DispatcherQueueTimer? _toastTimer;

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

        _loupeBitmap = new WriteableBitmap(LoupePixelSize, LoupePixelSize);
        LoupeImage.Source = _loupeBitmap;

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
                    ToggleCaptureMode();
                    break;
                case 0x4F:
                    if (_hasSelection)
                        _ = OcrSelectionAsync();
                    break;
            }
        });

        // 焦点仍可能被系统抢走：再挂一条低级键盘钩子，取景窗亮着时 Tab/Esc/Ctrl+C 不依赖焦点。
        EscOverPinHook.SetOverlayKeyHandler(OnOverlayHotkey);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;

            // 全屏取景窗没有任务栏按钮：否则每次按截图热键，任务栏都会闪出一个
            // “实践小梅IshWY”图标再消失。必须在第一次 Show 之前设置。
            AppWindow.IsShownInSwitchers = false;
        }

        // WinUI 无边框窗仍保留 1px 非客户边框：鼠标贴屏幕边缘时命中 HTBORDER，
        // PointerPressed 根本收不到。把客户区扩到整窗，并强制所有点命中 HTCLIENT。
        _keys.MakeClientAreaFullWindow();

        // 首次亮窗时窗口可能尚未就绪，Focus 会被静默丢弃 → Tab/回车失灵；
        // 每次激活都补焦，另有 60ms 延迟补投兜底
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                // 取景窗失焦时只保留 Esc：鼠标点一下别的窗口后，仍能一键退出取景，
                // 但 Tab/Ctrl+C 等组合键必须交还给前台程序。另存对话框期间完全放行。
                if (!_closed && !_keysSuspended)
                    EscOverPinHook.SetOverlayKeysEscOnly(true);
                else
                    EscOverPinHook.SetOverlayKeysActive(false);
                return;
            }

            if (!_closed && !_keysSuspended)
                EscOverPinHook.SetOverlayKeysActive(true);
            _ = FocusSink.Focus(FocusState.Programmatic);
            RefreshHoverFromCursor();
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

        Closed += (_, _) =>
        {
            EscOverPinHook.SetOverlayKeysActive(false);
            EscOverPinHook.SetOverlayKeyHandler(null);
            _full.Dispose();
        };
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
        // 跳过本进程窗口；跳过“几乎整屏的点击穿透层”（例如 DSH 歌词 toast 的透明 Chrome 窗口）。
        // 后者永远压在 Z 序最前且鼠标穿过去，若不排除，任何位置的首选都会是它，
        // 其他窗口只能靠滚轮轮换，表现为“鼠标悬停高亮不跟手”。
        var selfPid = Environment.ProcessId;
        _windows = windows
            .Where(w => !w.IsCloaked &&
                        w.Handle != _selfHwnd &&
                        w.ProcessId != selfPid &&
                        w.FrameBounds.Width > 0 &&
                        !IsFullScreenClickThrough(w))
            .ToList();

        ResetSessionState();

        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height));

        // 每次取景默认窗口截图；主人可在取景中按 Tab 切到常规截图
        _captureMode = string.Equals(App.Config.Current.DefaultCaptureMode, "normal", StringComparison.OrdinalIgnoreCase)
            ? CaptureMode.Normal
            : CaptureMode.Window;
        _scale = DetectScale();
        ApplySelectionColor();
        UpdateVisuals();

        InitializeFrozen();
        App.Log("Overlay: prepared");
    }

    private void ResetSessionState()
    {
        _closed = false;
        _confirming = false;
        _sessionFinished = false;
        _dragging = false;
        _dragMoved = false;
        _movingSelection = false;
        _moveWholeWindow = false;
        _resizingSelection = false;
        _resizeEdge = ResizeEdge.None;
        _wheelAccum = 0;
        _lastWheelTick = 0;
        _keysSuspended = false;
        _hasSelection = false;
        _selection = Rect.Empty;
        _hoverWindow = null;
        _clickedWindow = null;
        _subregionWindow = null;
        _subregionCandidate = null;
        _lastTipWindow = null;
        _lastHoverHandle = 0;
        _hoverCandidates.Clear();
        _lastHoverPoint = default;
        _lastPress = default;
        _saving = false;
        _loupeHex = "";
        _loupeRgb = "";
        SelectionBorder.Visibility = Visibility.Collapsed;
        HoverBorder.Visibility = Visibility.Collapsed;
        SizeTip.Visibility = Visibility.Collapsed;
        SelectionActions.Visibility = Visibility.Collapsed;
        HudBar.Visibility = Visibility.Visible;
        LoupePanel.Visibility = Visibility.Collapsed;
        CopyToast.Visibility = Visibility.Collapsed;
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
    /// <summary>
    /// 取景窗可见期间的 Esc 物理键轮询：低级键盘钩子可能被系统跳过/摘除，
    /// 此时 XAML 焦点也未必在取景窗上，只有 Win32 键态不依赖钩子与焦点。
    /// </summary>
    private void StartEscPolling()
    {
        StopEscPolling();
        _escPollHeld = IsEscDown() ? 1 : 0;   // 进取景前就按住的 Esc 不算一次新的按下
        _escPollTimer = new System.Threading.Timer(
            _ => PollEscKey(), null, EscPollIntervalMs, EscPollIntervalMs);
    }

    private void StopEscPolling()
    {
        _escPollTimer?.Dispose();
        _escPollTimer = null;
    }

    private void PollEscKey()
    {
        if (_closed)
            return;

        var down = IsEscDown();
        if (!down)
        {
            Interlocked.Exchange(ref _escPollHeld, 0);
            return;
        }

        if (Interlocked.Exchange(ref _escPollHeld, 1) != 0)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed || _keysSuspended)
                return;
            App.Log("Overlay: Esc polled (hook/focus bypass)");
            Cancel();
        });
    }

    private static bool IsEscDown() => (GetAsyncKeyState(0x1B) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

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

            EscOverPinHook.SetOverlayKeysActive(true);
            EscOverPinHook.EnsureOverlayHook();
            StartEscPolling();
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
        var pointInfo = e.GetCurrentPoint(Root);
        var point = pointInfo.Position;

        // 右键等价 Esc：取景阶段随时取消，不进入选区/拖拽状态。
        if (pointInfo.Properties.IsRightButtonPressed)
        {
            App.Log("Overlay: right-click cancel");
            Cancel();
            return;
        }

        var left = pointInfo.Properties.IsLeftButtonPressed;
        var middle = pointInfo.Properties.IsMiddleButtonPressed && !left;
        // 左键负责框选；滚轮键只用来拖动已有选区，不在空白处重新起框。
        if (!left && !middle)
            return;

        Layer.CapturePointer(e.Pointer);

        // 双击已有选区 = 确认（滚轮键不参与双击）
        if (left && _hasSelection && (now - _lastPress).TotalMilliseconds < 500 &&
            _selection.Contains(point))
        {
            _lastPress = now;
            _ = ConfirmAsync();
            return;
        }
        _lastPress = now;

        // 按下：记住吸附窗口。拖动 = 自由框选；不动 = 单击选中窗口
        _pressPoint = point;
        _lastHoverPoint = point;

        // 左键落在选区四边/四角 8 DIP 内 = 缩放。必须先于“内部移动”判断，
        // 否则靠边起手会被当成移动，永远拉不动边框。
        if (left && TryHitResizeEdge(point, out var resizeEdge))
        {
            BeginResize(resizeEdge, point);
            return;
        }

        // 两种模式：按在已有选区内部拖动 = 移动整个选框（左键或按住滚轮均可）。
        // 窗口模式下整窗选区一动就会脱离原窗口；窗口局部选区则保留窗口归属，
        // 并限制在窗口矩形内移动，确认时仍从窗口本体裁剪。
        if (_hasSelection && _selection.Contains(point))
        {
            _movingSelection = true;
            _moveStartSelection = _selection;
            _moveWholeWindow = _captureMode == CaptureMode.Window &&
                               _clickedWindow is not null && _subregionWindow is null;
            _dragging = true;
            _dragMoved = false;
            HideColorLoupe();
            UpdateVisuals();
            return;
        }

        // 滚轮键只用于移动已有选区：点在空白处时不做任何事，更不会重新框选。
        if (!left)
        {
            Layer.ReleasePointerCapture(e.Pointer);
            return;
        }

        _dragging = true;
        _dragMoved = false;
        HideColorLoupe();
        // 两种模式都允许预选/单击选中窗口；区别只在取图来源：
        // 窗口模式重建窗口本体，常规模式仍从冻结画面裁切（菜单/提示所见即所得）。
        _clickedWindow = _hoverWindow;
        _subregionWindow = null;
        // 悬浮在普通窗口上开始拖拽 = 窗口局部（悬浮类窗口仍按屏幕所见处理）
        _subregionCandidate = _captureMode == CaptureMode.Window &&
                              _hoverWindow is { IsOverlayClass: false } &&
                              _hoverWindow.FrameBounds.Width > 0
            ? _hoverWindow
            : null;
        // 不在按下时直接提交整窗选区：否则边框/内部按下会先变成“整窗选中”，
        // 拖拽被误判为已有选区，边界起拖也会被吞。整窗选中等松手再确认。
        _selection = Rect.Empty;
        _hasSelection = false;
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

            if (_resizingSelection)
            {
                if (_dragMoved)
                    ResizeSelection(point);
                UpdateVisuals();
                return;
            }

            if (_movingSelection)
            {
                if (_dragMoved)
                {
                    HideColorLoupe();

                    // 整窗选区一旦被拖动，就不再代表原来那个窗口；改为常规截图，
                    // 从冻结画面裁切，避免把移动后的矩形错误地映射回窗口内容。
                    if (_moveWholeWindow)
                    {
                        _moveWholeWindow = false;
                        _clickedWindow = null;
                        _subregionWindow = null;
                        _subregionCandidate = null;
                        _captureMode = CaptureMode.Normal;
                        ShowCopyToast("已移动整窗选区，改为常规截图");
                        App.Log("Overlay: whole-window selection moved -> normal screen crop");
                    }

                    var dx = point.X - _pressPoint.X;
                    var dy = point.Y - _pressPoint.Y;

                    var minX = 0.0;
                    var minY = 0.0;
                    var maxX = Math.Max(0, Root.ActualWidth - _moveStartSelection.Width);
                    var maxY = Math.Max(0, Root.ActualHeight - _moveStartSelection.Height);

                    // 窗口局部选区：只能在原窗口矩形内移动，确认时仍走窗口本体裁剪。
                    if (_subregionWindow is { } sub)
                    {
                        var wr = DipRect(sub.FrameBounds);
                        var localMaxX = Math.Min(maxX, wr.X + wr.Width - _moveStartSelection.Width);
                        var localMaxY = Math.Min(maxY, wr.Y + wr.Height - _moveStartSelection.Height);
                        if (localMaxX >= wr.X && localMaxY >= wr.Y)
                        {
                            minX = Math.Max(0, wr.X);
                            minY = Math.Max(0, wr.Y);
                            maxX = localMaxX;
                            maxY = localMaxY;
                        }
                    }

                    var x = Math.Clamp(_moveStartSelection.X + dx, minX, Math.Max(minX, maxX));
                    var y = Math.Clamp(_moveStartSelection.Y + dy, minY, Math.Max(minY, maxY));
                    _selection = new Rect(x, y, _moveStartSelection.Width, _moveStartSelection.Height);
                }

                UpdateVisuals();
                return;
            }

            if (_dragMoved)
            {
                HideColorLoupe();
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

        if (_resizingSelection)
        {
            NormalizeResizedSelection();
            _resizingSelection = false;
            _resizeEdge = ResizeEdge.None;
            UpdateVisuals();
            HideColorLoupe();
            return;
        }

        if (_movingSelection)
        {
            _movingSelection = false;
            _moveWholeWindow = false;
            // 只是点了一下选框内部、没有拖动：保留选区，不进入“新框选”。
            UpdateVisuals();
            HideColorLoupe();
            return;
        }

        if (!_dragMoved)
        {
            if (_clickedWindow is null)
            {
                // 空白处纯点击 = 清除选区
                _selection = Rect.Empty;
                _hasSelection = false;
            }
            else
            {
                // 单击窗口 = 选中整窗（两种模式都支持；常规模式仍从冻结画面裁切）
                _selection = DipRect(_clickedWindow.FrameBounds);
                _hasSelection = true;
                _subregionWindow = null;
            }
        }
        else if (_dragMoved && _selection.Width * _selection.Height < 16)
        {
            // 拖拽距离太短 = 无效选区
            _selection = Rect.Empty;
            _hasSelection = false;
            _subregionWindow = null;
        }

        UpdateVisuals();
        if (_hasSelection)
            HideColorLoupe();
        else
            UpdateColorLoupe(e.GetCurrentPoint(Root).Position);
    }

    private bool TryHitResizeEdge(Windows.Foundation.Point point, out ResizeEdge edge)
    {
        edge = ResizeEdge.None;
        if (!_hasSelection)
            return false;

        var s = _selection;
        var tol = ResizeHandleTolerance;
        if (point.X < s.X - tol || point.X > s.Right + tol ||
            point.Y < s.Y - tol || point.Y > s.Bottom + tol)
            return false;

        var nearLeft = Math.Abs(point.X - s.X) <= tol;
        var nearRight = Math.Abs(point.X - s.Right) <= tol;
        var nearTop = Math.Abs(point.Y - s.Y) <= tol;
        var nearBottom = Math.Abs(point.Y - s.Bottom) <= tol;
        var inY = point.Y >= s.Y - tol && point.Y <= s.Bottom + tol;
        var inX = point.X >= s.X - tol && point.X <= s.Right + tol;

        var left = nearLeft && inY;
        var right = nearRight && inY;
        var top = nearTop && inX;
        var bottom = nearBottom && inX;

        if (left && top) edge = ResizeEdge.TopLeft;
        else if (right && top) edge = ResizeEdge.TopRight;
        else if (left && bottom) edge = ResizeEdge.BottomLeft;
        else if (right && bottom) edge = ResizeEdge.BottomRight;
        else if (left) edge = ResizeEdge.Left;
        else if (right) edge = ResizeEdge.Right;
        else if (top) edge = ResizeEdge.Top;
        else if (bottom) edge = ResizeEdge.Bottom;

        return edge != ResizeEdge.None;
    }

    private void BeginResize(ResizeEdge edge, Windows.Foundation.Point point)
    {
        _resizingSelection = true;
        _resizeEdge = edge;
        _resizeStartSelection = _selection;
        _resizeStartPoint = point;

        // 锚住“对边/对角”：拖过对边时选框翻转继续缩放，而不是卡在最小尺寸。
        _resizeAnchorX = edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft
            ? _selection.Right
            : _selection.X;
        _resizeAnchorY = edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight
            ? _selection.Bottom
            : _selection.Y;

        // 窗口模式整窗选区缩放时，先把原窗口记为局部裁剪目标；缩出来的矩形
        // 仍然从窗口本体重建，不会因为缩小而丢掉抗遮挡能力。
        if (_captureMode == CaptureMode.Window)
        {
            if (_subregionWindow is null && _clickedWindow is not null)
                _subregionWindow = _clickedWindow;
            _subregionCandidate = null;
        }
        else
        {
            _clickedWindow = null;
            _subregionWindow = null;
            _subregionCandidate = null;
        }

        _movingSelection = false;
        _moveWholeWindow = false;
        _dragging = true;
        _dragMoved = false;
        HideColorLoupe();
        App.Log($"Overlay: resize start edge={edge} mode={_captureMode} rect={_selection.X:0},{_selection.Y:0} {_selection.Width:0}x{_selection.Height:0}");
        UpdateVisuals();
    }

    private void ResizeSelection(Windows.Foundation.Point point)
    {
        var start = _resizeStartSelection;
        var bounds = GetResizeBounds();
        var x = Math.Clamp(point.X, bounds.X, bounds.Right);
        var y = Math.Clamp(point.Y, bounds.Y, bounds.Bottom);

        var left = start.X;
        var top = start.Y;
        var right = start.Right;
        var bottom = start.Bottom;

        // 拖动的边跟随指针，对边固定为锚点；min/max 归一化后允许指针越过锚点，
        // 选框会自然翻转（如左上角拖到右下角），不会停在另一条边上。
        if (_resizeEdge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
        {
            left = Math.Min(_resizeAnchorX, x);
            right = Math.Max(_resizeAnchorX, x);
        }
        if (_resizeEdge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight)
        {
            left = Math.Min(_resizeAnchorX, x);
            right = Math.Max(_resizeAnchorX, x);
        }
        if (_resizeEdge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
        {
            top = Math.Min(_resizeAnchorY, y);
            bottom = Math.Max(_resizeAnchorY, y);
        }
        if (_resizeEdge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight)
        {
            top = Math.Min(_resizeAnchorY, y);
            bottom = Math.Max(_resizeAnchorY, y);
        }

        _selection = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>松手后把过薄的选区补到最小可操作尺寸（保持锚定边，不跳回旧框）。</summary>
    private void NormalizeResizedSelection()
    {
        var bounds = GetResizeBounds();
        var sel = _selection;
        var w = sel.Width;
        var h = sel.Height;
        if (w >= MinSelectionSize && h >= MinSelectionSize)
            return;

        var x = sel.X;
        var y = sel.Y;
        if (w < MinSelectionSize)
        {
            if (_resizeEdge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
                x = Math.Clamp(sel.Right - MinSelectionSize, bounds.X, Math.Max(bounds.X, bounds.Right - MinSelectionSize));
            else
                x = Math.Clamp(sel.X, bounds.X, Math.Max(bounds.X, bounds.Right - MinSelectionSize));
            w = MinSelectionSize;
        }
        if (h < MinSelectionSize)
        {
            if (_resizeEdge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
                y = Math.Clamp(sel.Bottom - MinSelectionSize, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - MinSelectionSize));
            else
                y = Math.Clamp(sel.Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - MinSelectionSize));
            h = MinSelectionSize;
        }
        _selection = new Rect(x, y, w, h);
    }

    private Rect GetResizeBounds()
    {
        if (_captureMode == CaptureMode.Window && _subregionWindow is { } sub)
        {
            var wr = DipRect(sub.FrameBounds);
            if (wr.Width >= MinSelectionSize && wr.Height >= MinSelectionSize)
                return wr;
        }

        return new Rect(0, 0,
            Math.Max(MinSelectionSize, Root.ActualWidth),
            Math.Max(MinSelectionSize, Root.ActualHeight));
    }

    private void UpdateHover(Windows.Foundation.Point dipPoint)
    {
        _lastHoverPoint = dipPoint;
        HitTestHover(dipPoint);
        LogHoverChange();
        UpdateVisuals();
        UpdateColorLoupe(dipPoint);
    }

    /// <summary>命中目标变化时记一行，方便主人复现“某个弹窗选不中”时直接看日志。</summary>
    private void LogHoverChange()
    {
        var handle = _hoverWindow?.Handle ?? 0;
        if (handle == _lastHoverHandle)
            return;

        _lastHoverHandle = handle;
        if (_hoverWindow is { } win)
        {
            var title = win.Title.Length > 48 ? win.Title[..48] + "…" : win.Title;
            App.Log($"Overlay: hover 0x{win.Handle:X} class=\"{win.ClassName}\" child={win.IsChild} process=\"{win.ProcessName}\" rect={win.FrameBounds.X},{win.FrameBounds.Y} {win.FrameBounds.Width}x{win.FrameBounds.Height} title=\"{title}\"");
        }
        else
        {
            App.Log("Overlay: hover none");
        }
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

        // 两种模式都做预选/单击选中：常规模式也先高亮窗口，但确认时仍从冻结画面裁切。
        // 枚举列表本身按 Z 序（顶层在前）；悬浮类窗口（歌词/桌宠/提示）也一并纳入，
        // 与窗口截图合并后不再需要单独的「识别悬浮窗口」开关。
        // 注意命中的是“矩形包含该点”的全部窗口——被遮挡的窗口也在候选里，滚轮可轮换。
        // 复用列表，鼠标高频移动下零分配（分配会引起 GC 停顿 → 闪动感）
        _hoverCandidates.Clear();
        foreach (var w in _windows)
        {
            if (ContainsNear(w.FrameBounds, vx, vy, HitTolerancePx) &&
                w.FrameBounds.Width < _monitor.Width * 2)
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
            var now = Environment.TickCount64;
            if (now - _lastWheelTick > WheelResetMs)
                _wheelAccum = 0;
            _lastWheelTick = now;
            _wheelAccum += delta;
            if (Math.Abs(_wheelAccum) < WheelUnitsPerWindow)
                return;

            // 一次输入最多切一个窗口；累计值清掉，避免高速滚轮一次跨过多个候选。
            var step = _wheelAccum > 0 ? 1 : -1;
            _wheelAccum = 0;
            CycleHover(step);
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
            // 切到新窗口后选区回到“整窗”，不能再带着上一个窗口的局部裁剪归属，
            // 否则确认时会把新窗口的矩形映射到旧窗口位图上裁错。
            _subregionWindow = null;
            _subregionCandidate = null;
        }

        UpdateVisuals();
    }

    /// <summary>Tab：窗口截图 ↔ 常规截图。两种模式共用同一套框选与确认流程，只换取图来源。</summary>
    private void ToggleCaptureMode()
    {
        try
        {
            var hadSelection = _hasSelection;
            var previousSelection = _selection;
            var previousMode = _captureMode;
            _captureMode = _captureMode == CaptureMode.Window ? CaptureMode.Normal : CaptureMode.Window;

            _dragging = false;
            _dragMoved = false;
            _movingSelection = false;
            _moveWholeWindow = false;
            _resizingSelection = false;
            _resizeEdge = ResizeEdge.None;
            _wheelAccum = 0;
            _lastTipWindow = null;
            HideColorLoupe();

            if (!hadSelection)
            {
                // 没有选区时沿用旧行为：清空窗口引用，光标下重新吸附。
                _hasSelection = false;
                _selection = Rect.Empty;
                _hoverWindow = null;
                _clickedWindow = null;
                _subregionCandidate = null;
                _subregionWindow = null;
                _hoverCandidates.Clear();
                RefreshHoverFromCursor();
                UpdateVisuals();
                ShowModeToast($"已切换到{ModeName()}");
                App.Log($"Overlay: mode={(ModeName())}");
                return;
            }

            _hasSelection = true;
            _selection = previousSelection;

            if (_captureMode == CaptureMode.Normal)
            {
                // 窗口 -> 常规：保留原选区，直接按冻结画面裁切。
                _hoverWindow = null;
                _clickedWindow = null;
                _subregionCandidate = null;
                _subregionWindow = null;
                _hoverCandidates.Clear();
                UpdateVisuals();
                ShowModeToast("已切换到常规截图，选区保留");
                App.Log("Overlay: mode=normal (selection preserved)");
                return;
            }

            // 常规 -> 窗口：找能完整包住当前选区的最小窗口，用窗口本体重建后再按同一选区裁切。
            // 跨窗口或没有窗口能包容时保持常规模式，宁可少切也不裁错。
            var target = FindContainingWindow(_selection);
            if (target is null)
            {
                _captureMode = CaptureMode.Normal;
                _hoverWindow = null;
                _clickedWindow = null;
                _subregionCandidate = null;
                _subregionWindow = null;
                _hoverCandidates.Clear();
                UpdateVisuals();
                ShowModeToast("没有窗口能完整包住选区，保持常规截图");
                App.Log("Overlay: mode=normal (no containing window, selection preserved)");
                return;
            }

            var frame = DipRect(target.FrameBounds);
            _clickedWindow = target;
            _hoverWindow = target;
            _subregionCandidate = null;
            _subregionWindow = NearlyEquals(frame, _selection, 2) ? null : target;
            _hoverCandidates.Clear();
            UpdateVisuals();
            ShowModeToast("已切换到窗口截图，选区保留");
            App.Log($"Overlay: mode=window (selection preserved, title={target.Title}, local={(_subregionWindow is not null)})");
        }
        catch (Exception ex)
        {
            App.Log($"Overlay Tab 切换异常: {ex.Message}");
        }
    }

    private string ModeName() => _captureMode == CaptureMode.Window ? "窗口截图" : "常规截图";

    /// <summary>在候选窗口里找完整包住选区的最小窗口；排除自身、全屏点击穿透层与隐藏窗口。</summary>
    private WindowInfo? FindContainingWindow(Rect selection)
    {
        WindowInfo? best = null;
        var bestArea = double.MaxValue;
        foreach (var w in _windows)
        {
            if (w.Handle == _selfHwnd || w.IsCloaked || w.FrameBounds.Width <= 0 || w.FrameBounds.Height <= 0)
                continue;
            if (IsFullScreenClickThrough(w))
                continue;

            var frame = DipRect(w.FrameBounds);
            const double tolerance = 0.5;
            if (frame.X > selection.X + tolerance ||
                frame.Y > selection.Y + tolerance ||
                frame.X + frame.Width < selection.X + selection.Width - tolerance ||
                frame.Y + frame.Height < selection.Y + selection.Height - tolerance)
                continue;

            var area = frame.Width * frame.Height;
            if (area < bestArea)
            {
                bestArea = area;
                best = w;
            }
        }
        return best;
    }

    private static bool NearlyEquals(Rect a, Rect b, double tolerance) =>
        Math.Abs(a.X - b.X) <= tolerance &&
        Math.Abs(a.Y - b.Y) <= tolerance &&
        Math.Abs(a.Width - b.Width) <= tolerance &&
        Math.Abs(a.Height - b.Height) <= tolerance;

    /// <summary>低级键盘钩子回调（钩子线程）：只切回 UI 线程，绝不做其他事。</summary>
    private void OnOverlayHotkey(int vk, bool ctrl)
    {
        DispatcherQueue.TryEnqueue(() => HandleOverlayHotkey(vk, ctrl));
    }

    /// <summary>取景窗可见时的键盘直达，不依赖 XAML 焦点。</summary>
    private void HandleOverlayHotkey(int vk, bool ctrl)
    {
        if (_closed)
            return;

        switch (vk)
        {
            case 0x1B: // Esc
                Cancel();
                break;
            case 0x0D: // Enter
                _ = ConfirmCurrentTargetAsync();
                break;
            case 0x09: // Tab
                ToggleCaptureMode();
                break;
            case OverlayPrevWindowKey: // 取景器内：上一个窗口
                CycleHover(-1);
                break;
            case OverlayNextWindowKey: // 取景器内：下一个窗口
                CycleHover(1);
                break;
            case 0x4F: // O：OCR 选区
                if (_hasSelection)
                    _ = OcrSelectionAsync();
                break;
            case 0x43 when ctrl: // Ctrl+C
                if (_hasSelection)
                    _ = ConfirmAsync();
                else
                    _ = CopyLoupeColorAsync();
                break;
            case 0x53 when ctrl: // Ctrl+S
                if (_hasSelection)
                    _ = SaveSelectionAsync();
                break;
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
                _ = ConfirmCurrentTargetAsync();
                break;

            case Windows.System.VirtualKey.Tab:
                e.Handled = true;
                ToggleCaptureMode();
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
        _ = ConfirmCurrentTargetAsync();
    }

    // ---------- 收尾 ----------

    public void Cancel() => Dismiss();

    /// <summary>
    /// 确认截图时先把取景窗收掉：冻结图已经够用，没必要让遮罩/选区一直停在屏幕上
    /// 等裁剪、PNG 编码、写剪贴板和落历史——那会看起来像“回车卡住”。
    /// _closed 置位后重复按键不会再次进入。
    /// </summary>
    private void HideForCapture()
    {
        if (_closed)
            return;
        _closed = true;
        StopEscPolling();
        EscOverPinHook.SetOverlayKeysActive(false);
        AppWindow.Hide();
        try
        {
            HideColorLoupe();
            _toastTimer?.Stop();
            CopyToast.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.Log($"Overlay hide 异常: {ex.Message}");
        }
        App.Log("Overlay: hidden for capture");
    }

    /// <summary>捕获已经取完，释放冻结图并通知服务本次取景结束。</summary>
    private void FinishCaptureSession()
    {
        if (_sessionFinished)
            return;
        _sessionFinished = true;

        var before = GC.GetTotalMemory(false);
        try
        {
            Frozen.Source = null;
            _full.Dispose();
            _full = new Bitmap(1, 1);
        }
        catch (Exception ex)
        {
            App.Log($"Overlay cleanup 异常: {ex.Message}");
        }

        // 2560×1600 的 WriteableBitmap + GDI Bitmap 各约 16 MB，PixelBuffer 会进大对象堆。
        // 但不能在 UI 线程上阻塞式 GC——那正是下一次截图“卡一下”的来源；放到后台延迟压缩。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180);
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                App.Log($"Overlay: frozen buffers released, managed={before / 1048576}->{GC.GetTotalMemory(false) / 1048576}MB");
            }
            catch (Exception ex)
            {
                App.Log($"Overlay GC 异常: {ex.Message}");
            }
        });

        Dismissed?.Invoke();
    }

    /// <summary>隐藏窗口（复用），并通知服务本次取景结束。</summary>
    private void Dismiss()
    {
        if (_closed)
            return;
        HideForCapture();
        FinishCaptureSession();
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
        // 常规截图：不重建任何窗口，直接裁冻结画面——菜单、悬停提示、下拉框、指针所见即所得
        if (_captureMode == CaptureMode.Normal)
            return CropFullScreenSelection();

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

        return CropFullScreenSelection();
    }

    /// <summary>按选区从冻结画面裁切（DIP → 显示器本地物理坐标，夹在显示器内）。</summary>
    private Bitmap? CropFullScreenSelection()
    {
        var dip = _selection;
        if (dip.IsEmpty)
            return null;

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

    /// <summary>
    /// 回车确认当前目标：已有选区走原确认流程；只有滚轮/悬停高亮时，
    /// 把高亮窗口当作整窗选区再确认，这样滚轮选完不必再点一下。
    /// </summary>
    private async Task ConfirmCurrentTargetAsync()
    {
        if (_closed || _confirming)
            return;

        _confirming = true;
        try
        {
            if (!_hasSelection)
            {
                if (_hoverWindow is null)
                    return;

                _clickedWindow = _hoverWindow;
                _selection = DipRect(_clickedWindow.FrameBounds);
                _hasSelection = true;
                _subregionWindow = null;
                // 不在这里 UpdateVisuals：回车是“直接截当前高亮窗口”，
                // 若先画选区边框/遮罩，窗口捕获的几十毫秒里主人会看到亮框先缩到窗口再截图。
                // 保持原有悬停高亮，等 ConfirmAsync 捕获完直接 Dismiss。
                App.Log($"Overlay: enter captures hovered window 0x{_clickedWindow.Handle:X} process=\"{_clickedWindow.ProcessName}\"");
            }

            await ConfirmAsync();
        }
        finally
        {
            _confirming = false;
        }
    }

    private async Task ConfirmAsync()
    {
        Bitmap? captured = null;
        var ended = false;
        try
        {
            // 先藏窗再取图：窗口捕获/PNG 编码期间主人不会再看到遮罩和选区。
            HideForCapture();
            captured = await CaptureSelectionAsync();

            // 图已经拿到，冻结缓冲可以释放，取景服务也立即进入空闲状态；
            // 后面的编码/剪贴板/历史都在后台，不挡下一次热键。
            FinishCaptureSession();
            ended = true;

            if (captured is null)
            {
                PrtScr.App.Services.ToastService.ShowCaptureFailure();
                return;
            }

            var bytes = await Task.Run(() => EncodePng(captured));

            var target = ResolveHistoryTarget();
            var copyTask = PrtScr.App.Services.ClipboardService.SetImageAsync(bytes);
            var historyTask = PrtScr.App.Services.CaptureHistoryService.Instance.AddAsync(
                bytes, target.Mode, target.ProcessName, target.WindowTitle);
            await Task.WhenAll(copyTask, historyTask);

            var ok = copyTask.Result;
            App.Log(ok ? "Capture: copied" : "Capture: clipboard final failure");
            if (historyTask.Result is { } savedPath)
                App.Log($"Capture: history saved {savedPath}");
            if (ok)
                PrtScr.App.Services.ToastService.ShowCaptureSuccess();
            else
                PrtScr.App.Services.ToastService.ShowCaptureFailure();
        }
        catch (Exception ex)
        {
            App.Log($"截图确认失败: {ex}");
            PrtScr.App.Services.ToastService.ShowCaptureFailure();
        }
        finally
        {
            captured?.Dispose();
            if (!ended)
                FinishCaptureSession();
        }
    }

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private void Capture_Click(object sender, RoutedEventArgs e) => _ = ConfirmAsync();

    private void CancelCapture_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Ocr_Click(object sender, RoutedEventArgs e)
    {
        if (_hasSelection)
            _ = OcrSelectionAsync();
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
        // 文件选择器会抢前台：期间暂停取景器按键，避免 Esc/Tab/回车被钩子当成取景操作。
        _keysSuspended = true;
        EscOverPinHook.SetOverlayKeysActive(false);
        Bitmap? captured = null;
        try
        {
            captured = await CaptureSelectionAsync();
            if (captured is null)
            {
                PrtScr.App.Services.ToastService.ShowCaptureFailure();
                return;
            }

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
            var target = ResolveHistoryTarget();
            var copyTask = PrtScr.App.Services.ClipboardService.SetImageAsync(bytes);
            var historyTask = PrtScr.App.Services.CaptureHistoryService.Instance.AddAsync(
                bytes, target.Mode, target.ProcessName, target.WindowTitle);
            await Task.WhenAll(copyTask, historyTask);

            var copied = copyTask.Result;
            App.Log(copied ? "Capture: copied" : "Capture: clipboard final failure");
            App.Log($"Capture: saved as {path}");
            if (historyTask.Result is { } historyPath)
                App.Log($"Capture: history saved {historyPath}");
            if (copied)
                PrtScr.App.Services.ToastService.ShowCaptureSuccess();
            else
                PrtScr.App.Services.ToastService.ShowCaptureFailure();
            Dismiss();
        }
        catch (Exception ex)
        {
            App.Log($"另存为失败: {ex}");
            PrtScr.App.Services.ToastService.ShowCaptureFailure();
        }
        finally
        {
            captured?.Dispose();
            _saving = false;
            _keysSuspended = false;
            if (!_closed)
            {
                EscOverPinHook.SetOverlayKeysActive(true);
                FocusSink.Focus(FocusState.Programmatic);
            }
        }
    }

    /// <summary>
    /// 解析本次截图应归入的历史分类：窗口模式且确实落在某个窗口上时按进程归类；
    /// 常规模式、或窗口模式下自由拖拽没命中窗口（实际裁的是冻屏），都算常规截图。
    /// </summary>
    private (HistoryCaptureMode Mode, string ProcessName, string WindowTitle) ResolveHistoryTarget()
    {
        if (_captureMode == CaptureMode.Normal)
            return (HistoryCaptureMode.Normal, string.Empty, string.Empty);

        var target = _subregionWindow ?? _clickedWindow;
        if (target is null)
            return (HistoryCaptureMode.Normal, string.Empty, string.Empty);

        return (HistoryCaptureMode.Window, target.ProcessName, target.Title);
    }

    // ---------- 取色放大镜 ----------

    /// <summary>
    /// 跟随鼠标更新 19×19 取色器：中心像素放大 10 倍，显示 HEX 与 RGB。
    /// 直接锁 19×19 小块再展开，避免 361 次 GetPixel；缓冲区全部复用，不产生每帧分配。
    /// </summary>
    private void UpdateColorLoupe(Windows.Foundation.Point dipPoint)
    {
        if (_closed || _hasSelection || _dragging || _scale <= 0 ||
            _full.Width < LoupeSampleSize || _full.Height < LoupeSampleSize ||
            _loupeBitmap is null)
        {
            HideColorLoupe();
            return;
        }

        try
        {
            // 用 Root 的实际 DIP 尺寸映射到冻结帧，而不是直接用 RasterizationScale：
            // 无边框窗曾有 1px 非客户区，客户区比屏幕小 2px，靠边时取色坐标会偏。
            var rootW = Root.ActualWidth > 0 ? Root.ActualWidth : _monitor.Width / _scale;
            var rootH = Root.ActualHeight > 0 ? Root.ActualHeight : _monitor.Height / _scale;
            if (rootW <= 0 || rootH <= 0)
            {
                HideColorLoupe();
                return;
            }

            var px = (int)Math.Round(dipPoint.X * _full.Width / rootW);
            var py = (int)Math.Round(dipPoint.Y * _full.Height / rootH);
            if (px < 0 || py < 0)
            {
                HideColorLoupe();
                return;
            }
            px = Math.Min(px, _full.Width - 1);
            py = Math.Min(py, _full.Height - 1);

            var left = Math.Clamp(px - LoupeSampleSize / 2, 0, _full.Width - LoupeSampleSize);
            var top = Math.Clamp(py - LoupeSampleSize / 2, 0, _full.Height - LoupeSampleSize);

            var data = _full.LockBits(
                new System.Drawing.Rectangle(left, top, LoupeSampleSize, LoupeSampleSize),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes = LoupeSampleSize * 4;
                for (var row = 0; row < LoupeSampleSize; row++)
                {
                    Marshal.Copy(
                        IntPtr.Add(data.Scan0, row * data.Stride),
                        _loupeSample,
                        row * rowBytes,
                        rowBytes);
                }
            }
            finally
            {
                _full.UnlockBits(data);
            }

            var center = (py - top) * LoupeSampleSize + (px - left);
            var centerOffset = center * 4;
            var b = _loupeSample[centerOffset];
            var g = _loupeSample[centerOffset + 1];
            var r = _loupeSample[centerOffset + 2];
            _loupeHex = $"#{r:X2}{g:X2}{b:X2}";
            _loupeRgb = $"R {r}  G {g}  B {b}";

            // 每个采样像素铺成 10×10 色块，整张 190×190
            for (var sy = 0; sy < LoupeSampleSize; sy++)
            {
                for (var sx = 0; sx < LoupeSampleSize; sx++)
                {
                    var so = (sy * LoupeSampleSize + sx) * 4;
                    var sb = _loupeSample[so];
                    var sg = _loupeSample[so + 1];
                    var sr = _loupeSample[so + 2];
                    var bx = sx * LoupeCell;
                    var by = sy * LoupeCell;
                    for (var dy = 0; dy < LoupeCell; dy++)
                    {
                        var rowBase = ((by + dy) * LoupePixelSize + bx) * 4;
                        for (var dx = 0; dx < LoupeCell; dx++)
                        {
                            var di = rowBase + dx * 4;
                            _loupePixels[di] = sb;
                            _loupePixels[di + 1] = sg;
                            _loupePixels[di + 2] = sr;
                            _loupePixels[di + 3] = 255;
                        }
                    }
                }
            }

            // 中心像素框：黑白双线，深色/浅色背景上都看得见
            var ccx = (px - left) * LoupeCell;
            var ccy = (py - top) * LoupeCell;
            for (var i = -1; i <= LoupeCell; i++)
            {
                SetLoupePixel(ccx + i, ccy - 1, 255, 255, 255);
                SetLoupePixel(ccx + i, ccy + LoupeCell, 255, 255, 255);
                SetLoupePixel(ccx - 1, ccy + i, 255, 255, 255);
                SetLoupePixel(ccx + LoupeCell, ccy + i, 255, 255, 255);
            }
            for (var i = -2; i <= LoupeCell + 1; i++)
            {
                SetLoupePixel(ccx + i, ccy - 2, 0, 0, 0);
                SetLoupePixel(ccx + i, ccy + LoupeCell + 1, 0, 0, 0);
                SetLoupePixel(ccx - 2, ccy + i, 0, 0, 0);
                SetLoupePixel(ccx + LoupeCell + 1, ccy + i, 0, 0, 0);
            }

            using (var stream = _loupeBitmap.PixelBuffer.AsStream())
            {
                stream.Position = 0;
                stream.Write(_loupePixels, 0, _loupePixels.Length);
            }
            _loupeBitmap.Invalidate();

            LoupeHex.Text = _loupeHex;
            LoupeRgb.Text = _loupeRgb;
            LoupePanel.Visibility = Visibility.Visible;

            var panelW = LoupePanel.ActualWidth > 0 ? LoupePanel.ActualWidth : 202;
            var panelH = LoupePanel.ActualHeight > 0 ? LoupePanel.ActualHeight : 256;
            // 底部 HUD 常驻，放大镜不能压住它：把可用高度收窄后再决定翻到光标上方
            var hudReserve = HudBar.Visibility == Visibility.Visible
                ? Math.Max(32, HudBar.ActualHeight) + 20
                : 0;
            var bottomLimit = rootH - hudReserve;
            var x = dipPoint.X + 18;
            if (x + panelW > rootW - 4)
                x = dipPoint.X - panelW - 18;
            var y = dipPoint.Y + 18;
            if (y + panelH > bottomLimit - 4)
                y = dipPoint.Y - panelH - 18;
            Canvas.SetLeft(LoupePanel, Math.Clamp(x, 4, Math.Max(4, rootW - panelW - 4)));
            Canvas.SetTop(LoupePanel, Math.Clamp(y, 4, Math.Max(4, bottomLimit - panelH - 4)));
            _loupeErrorLogged = false;
        }
        catch (Exception ex)
        {
            HideColorLoupe();
            if (!_loupeErrorLogged)
            {
                _loupeErrorLogged = true;
                App.Log($"Overlay 取色器异常: {ex.Message}");
            }
        }
    }

    private void HideColorLoupe()
    {
        if (LoupePanel is not null)
            LoupePanel.Visibility = Visibility.Collapsed;
    }

    private void SetLoupePixel(int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= LoupePixelSize || y >= LoupePixelSize)
            return;
        var o = (y * LoupePixelSize + x) * 4;
        _loupePixels[o] = b;
        _loupePixels[o + 1] = g;
        _loupePixels[o + 2] = r;
        _loupePixels[o + 3] = 255;
    }

    private void OnCopyColorShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_hasSelection)
        {
            // 有选区时 Ctrl+C 仍然是“复制这张截图”，和常见截图工具一致
            _ = ConfirmAsync();
            return;
        }
        _ = CopyLoupeColorAsync();
    }

    private async Task CopyLoupeColorAsync()
    {
        try
        {
            if (_loupeHex.Length == 0)
            {
                RefreshHoverFromCursor();
                if (_loupeHex.Length == 0)
                    return;
            }

            var text = _loupeHex;
            var ok = await PrtScr.App.Services.ClipboardService.SetTextAsync(text);
            App.Log(ok ? $"Overlay: color copied {text}" : $"Overlay: color copy failed {text}");
            ShowCopyToast(ok ? $"已复制 {text}" : "复制色值失败");
        }
        catch (Exception ex)
        {
            App.Log($"Overlay 复制色值异常: {ex.Message}");
        }
    }

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