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
    // 本会话是否已收尾：Prepare 时复位；旧会话由 FinishCaptureSession 的归属闸门挡住，不会污染这里。
    private bool _currentSessionFinished;
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
        _currentSessionFinished = false;
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


}
