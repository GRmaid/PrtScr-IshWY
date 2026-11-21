using Microsoft.UI.Xaml;
using PrtScr.App.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App;

public partial class App : Application
{
    private static ConfigService? _config;

    /// <summary>
    /// 配置服务延迟创建：冒烟启动先确定隔离数据根，再允许任何组件按 DataRoot 取配置，
    /// 避免字段初始化阶段回落到真实 data/ 目录。
    /// </summary>
    public static ConfigService Config => _config ??= new(SmokeSession.DataRoot, Log);

    /// <summary>主 UI 线程队列：贴图线程要弹 WinUI 菜单时，必须回到这里创建窗口。</summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue? UiQueue { get; private set; }

    private MainWindow? _window;
    private EventWaitHandle? _activateEvent;

    public App()
    {
        // 冒烟会话必须在读配置/建日志目录/装系统钩子之前判定；非法契约在这里直接失败关闭。
        SmokeSession.Begin();

        // 先挂异常处理，再初始化 XAML：InitializeComponent 里的资源/PRI 失败也要能落日志并提示，
        // 否则进程会在没有任何窗口的情况下静默退出，用户只看到「启动失败」。
        UnhandledException += (_, e) =>
        {
            var detail = $"{e.Message}\n\n{e.Exception?.StackTrace}";
            Log($"未处理异常: {detail}");

            // 冒烟：任何未处理异常都必须立刻写失败结果并非 0 退出，不能等到 20 秒看门狗超时，
            // 也不能弹模态框挡住 UI 线程（Fail 内部 ExitProcess，之后不再继续）。
            if (SmokeSession.IsRequested)
                SmokeSession.Fail($"unhandled-exception: {e.Exception?.GetType().Name ?? "unknown"}");

            ShowFatal(detail);
            e.Handled = true;
        };

        Log("App: ctor start");
        try
        {
            InitializeComponent();
            Log("App: InitializeComponent done");
        }
        catch (Exception ex)
        {
            Log($"App: InitializeComponent 失败 {ex}");
            // 冒烟失败必须写 smoke-result.json 并非 0 退出；Fail 内部直接 ExitProcess，不会继续弹窗。
            SmokeSession.Fail($"xaml-initialization-failed: {ex.GetType().Name}");
            ShowFatal($"初始化界面失败，请确认 IshWY.pri 与 XBF 资源完整。\n\n{ex}");
            throw;
        }

        if (!SmokeSession.IsRequested)
        {
            try
            {
                Services.EscOverPinHook.Install();
            }
            catch (Exception ex)
            {
                // 钩子安装失败只影响 Esc 兜底，没有理由拖死整个程序。
                Log($"App: EscOverPinHook 安装失败 {ex.Message}");
            }
        }

        PrtScr.Capture.WgcWindowCapture.Log = Log;
        PrtScr.Capture.DesktopDuplicationCapture.Log = Log;

        // XAML 初始化真正返回成功后才记这一项；之后主窗 Loaded 与 UI 队列往返再补齐另外两项。
        SmokeSession.MarkXamlInitialized();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("OnLaunched: start");
        try
        {
            // 冒烟不读真实配置：隔离根里没有 data/config.json，落默认值即可，也绝不写回真实目录。
            if (!SmokeSession.IsRequested)
            {
                Config.Load();
                Log("OnLaunched: config loaded");
            }
        }
        catch (Exception ex)
        {
            // 配置损坏不应阻止启动：ConfigService 会用默认值继续，最坏只是这次不读用户设置。
            Log($"OnLaunched: 配置读取失败，使用默认值 {ex}");
        }

        // 单实例：托盘常驻程序被再次启动时，唤醒已有实例的设置窗后退出
        bool isFirstInstance;
        try
        {
            // 冒烟遇到已有实例时只判冲突，绝不 Set 命名事件去激活对方的设置窗。
            isFirstInstance = TryAcquireSingleInstance(activateExisting: !SmokeSession.IsRequested);
        }
        catch (Exception ex)
        {
            // 冒烟不能把「检测失败」当首实例：那会绕过冲突判定，最后还可能写出 success=true。
            if (SmokeSession.IsRequested)
            {
                Log($"OnLaunched: 冒烟单实例检测失败，失败关闭 {ex}");
                SmokeSession.Fail("single-instance-check-failed");
                return;
            }

            // 命名事件可能因权限/会话隔离失败；宁可允许启动，也不要让程序完全打不开。
            Log($"OnLaunched: 单实例检测失败，按首实例继续 {ex.Message}");
            isFirstInstance = true;
        }

        if (!isFirstInstance)
        {
            if (SmokeSession.IsRequested)
            {
                // 冒烟不得把成功转交给已有实例：TryAcquireSingleInstance 在冒烟下不 Set 激活事件。
                Log("OnLaunched: 冒烟遇到已有实例，失败关闭");
                SmokeSession.FailInstanceConflict();
                return;
            }

            Log("OnLaunched: 已有实例，已请求其显示设置窗，本进程退出");
            PInvoke.ExitProcess(0);
            return;
        }

        try
        {
            // 便携版被移动后，登录启动项仍指向旧路径；每次启动顺手校正（冒烟绝不碰 HKCU Run）
            if (!SmokeSession.IsRequested)
                StartupService.RefreshIfEnabled();
        }
        catch (Exception ex)
        {
            Log($"OnLaunched: 启动项校正失败 {ex.Message}");
        }

        var startHidden = !SmokeSession.IsRequested &&
            Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        try
        {
            _window = new MainWindow(startHidden);
            UiQueue = _window.DispatcherQueue;
            Log($"OnLaunched: window created (startHidden={startHidden})");

            // 冒烟只需要主窗 Loaded + UI 队列往返；不装激活监听，避免外部事件干扰终态。
            if (!SmokeSession.IsRequested)
                StartActivationListener();

            // 旧的「睡 3 秒就 ExitProcess(0)」冒烟已删除：它不校验收任何真实完成事件，
            // 是包内缺依赖时仍报「冒烟通过」的根因。成功现在只能由 SmokeSession 的三个
            // 真实事件决定，计时（20 秒看门狗）只能触发失败。

            if (startHidden)
            {
                _window.PrepareHiddenStartup();
                Log("OnLaunched: startup 模式，静默驻留托盘");
            }
            else
            {
                _window.Activate();
                Log("OnLaunched: window activated");
            }
        }
        catch (Exception ex)
        {
            Log($"OnLaunched 异常: {ex}");
            // 冒烟必须真实反映启动失败：写失败结果并非 0 退出，不再伪装成功。
            if (SmokeSession.IsRequested)
            {
                SmokeSession.Fail($"on-launched-failed: {ex.GetType().Name}");
                return;
            }

            ShowFatal(ex.ToString());
            Exit();
        }
    }

    /// <summary>
    /// 用命名事件做单实例。第一个实例拿到 createdNew=true 并保持句柄；
    /// 后启动的实例 Set 事件后立即退出，由第一个实例把设置窗叫出来。
    /// </summary>
    private bool TryAcquireSingleInstance(bool activateExisting)
    {
        _activateEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: @"Local\IshWY.Activate",
            createdNew: out var createdNew);

        if (createdNew)
            return true;

        if (activateExisting)
        {
            try
            {
                _activateEvent.Set();
            }
            catch
            {
            }
        }

        _activateEvent.Dispose();
        _activateEvent = null;
        return false;
    }

    private void StartActivationListener()
    {
        var activateEvent = _activateEvent;
        if (activateEvent is null)
            return;

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!activateEvent.WaitOne())
                        break;
                }
                catch
                {
                    break;
                }

                var window = _window;
                if (window is not null)
                    window.DispatcherQueue.TryEnqueue(window.ShowFromTray);
            }
        })
        {
            IsBackground = true,
            Name = "PrtScr.Activate",
        };
        thread.Start();
    }

    /// <summary>M0 诊断：致命异常直接弹窗（日志在部分启动环境下不可用）。</summary>
    private static void ShowFatal(string text)
    {
        try
        {
            unsafe
            {
                fixed (char* body = text)
                fixed (char* caption = AppInfo.Name + " 启动异常")
                {
                    _ = PInvoke.MessageBox(
                        default,
                        new PCWSTR(body),
                        new PCWSTR(caption),
                        MESSAGEBOX_STYLE.MB_ICONERROR | MESSAGEBOX_STYLE.MB_SETFOREGROUND);
                }
            }
        }
        catch
        {
            // 连弹窗都失败时只能放弃
        }
    }

    private static readonly object LogLock = new();

    public static void Log(string message)
    {
        // 低级键盘钩子回调必须立刻返回：钩子线程上的任何日志都转给无锁队列，
        // 由 EscOverPinHook 的日志线程落盘，绝不在这里抢锁/建目录/写文件。
        if (EscOverPinHook.IsHookThread)
        {
            EscOverPinHook.LogDeferred(message);
            return;
        }

        try
        {
            lock (LogLock)
            {
                // 便携式布局（Snipaste 同款）：日志放在程序目录 data/logs 下；冒烟时落在隔离根。
                var dir = SmokeSession.LogDirectory;
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "app.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }
}
