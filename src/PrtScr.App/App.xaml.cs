using Microsoft.UI.Xaml;
using PrtScr.App.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App;

public partial class App : Application
{
    public static ConfigService Config { get; } = new(log: Log);

    /// <summary>主 UI 线程队列：贴图线程要弹 WinUI 菜单时，必须回到这里创建窗口。</summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue? UiQueue { get; private set; }

    private MainWindow? _window;
    private EventWaitHandle? _activateEvent;

    public App()
    {
        // 先挂异常处理，再初始化 XAML：InitializeComponent 里的资源/PRI 失败也要能落日志并提示，
        // 否则进程会在没有任何窗口的情况下静默退出，用户只看到「启动失败」。
        UnhandledException += (_, e) =>
        {
            var detail = $"{e.Message}\n\n{e.Exception?.StackTrace}";
            Log($"未处理异常: {detail}");
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
            ShowFatal($"初始化界面失败，请确认 IshWY.pri 与 XBF 资源完整。\n\n{ex}");
            throw;
        }

        try
        {
            Services.EscOverPinHook.Install();
        }
        catch (Exception ex)
        {
            // 钩子安装失败只影响 Esc 兜底，没有理由拖死整个程序。
            Log($"App: EscOverPinHook 安装失败 {ex.Message}");
        }

        PrtScr.Capture.WgcWindowCapture.Log = Log;
        PrtScr.Capture.DesktopDuplicationCapture.Log = Log;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("OnLaunched: start");
        try
        {
            Config.Load();
            Log("OnLaunched: config loaded");
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
            isFirstInstance = TryAcquireSingleInstance();
        }
        catch (Exception ex)
        {
            // 命名事件可能因权限/会话隔离失败；宁可允许启动，也不要让程序完全打不开。
            Log($"OnLaunched: 单实例检测失败，按首实例继续 {ex.Message}");
            isFirstInstance = true;
        }

        if (!isFirstInstance)
        {
            Log("OnLaunched: 已有实例，已请求其显示设置窗，本进程退出");
            PInvoke.ExitProcess(0);
            return;
        }

        try
        {
            // 便携版被移动后，登录启动项仍指向旧路径；每次启动顺手校正
            StartupService.RefreshIfEnabled();
        }
        catch (Exception ex)
        {
            Log($"OnLaunched: 启动项校正失败 {ex.Message}");
        }

        var startHidden = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));

        try
        {
            _window = new MainWindow(startHidden);
            UiQueue = _window.DispatcherQueue;
            Log($"OnLaunched: window created (startHidden={startHidden})");
            StartActivationListener();

            // 冒烟测试：--smoke 启动 3 秒后自动退出，供 CI / 脚本验证启动链路。
            // 用独立线程而非 DispatcherTimer：不依赖 UI 消息泵，哪怕界面卡死也能退出。
            // 注：LaunchActivatedEventArgs.Arguments 对命令行启动不可靠，直接读进程命令行
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--smoke", StringComparison.OrdinalIgnoreCase)))
            {
                new Thread(() =>
                {
                    Thread.Sleep(3000);
                    Log("OnLaunched: smoke exiting");
                    // Application.Exit/Environment.Exit 在 WASDK 下有“不终止进程”的已知问题，
                    // 直接调用 Win32 ExitProcess 机械保证退出
                    PInvoke.ExitProcess(0);
                })
                { IsBackground = true, Name = "PrtScr.SmokeExit" }.Start();
            }

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
            ShowFatal(ex.ToString());
            Exit();
        }
    }

    /// <summary>
    /// 用命名事件做单实例。第一个实例拿到 createdNew=true 并保持句柄；
    /// 后启动的实例 Set 事件后立即退出，由第一个实例把设置窗叫出来。
    /// </summary>
    private bool TryAcquireSingleInstance()
    {
        _activateEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: @"Local\IshWY.Activate",
            createdNew: out var createdNew);

        if (createdNew)
            return true;

        try
        {
            _activateEvent.Set();
        }
        catch
        {
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
        try
        {
            lock (LogLock)
            {
                // 便携式布局（Snipaste 同款）：日志放在程序目录 data/logs 下
                var dir = Path.Combine(AppContext.BaseDirectory, "data", "logs");
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
