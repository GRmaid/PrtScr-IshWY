using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.UI.Dispatching;
using PrtScr.Core;
using Windows.Win32;

namespace PrtScr.App.Services;

/// <summary>
/// <c>--smoke</c> 启动会话：校验隔离根、记录三个真实完成事件、写 smoke-result.json 后退出。
/// 成功只能由 xaml-initialized → main-window-loaded → dispatcher-roundtrip 三个真实事件共同决定；
/// 计时（20 秒看门狗）只能触发失败。契约非法时在读配置/日志之前失败关闭（退出码 2），
/// 已有实例时只写失败结果、不激活对方（退出码 3）。
/// </summary>
internal static class SmokeSession
{
    private const int FailureExitCode = 1;
    private const int InvalidContractExitCode = 2;
    private const int InstanceConflictExitCode = 3;
    private const int ResultWriteFailedExitCode = 6;
    private const int WatchdogSeconds = 20;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static SmokeLaunch? _launch;
    private static SmokeRunState? _state;
    private static bool _resultWritten;
    private static bool _roundtripRequested;
    private static int _begun;

    /// <summary>命令行是否带 --smoke（不受契约合法性影响）。</summary>
    public static bool IsRequested { get; private set; }

    public static bool IsValid => _launch is not null;

    /// <summary>冒烟时等于隔离根；非冒烟时是程序目录。配置/日志/历史都从这里派生。</summary>
    public static string DataRoot { get; private set; } = AppContext.BaseDirectory;

    public static string DataDirectory => Path.Combine(DataRoot, "data");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string HistoryDirectory => Path.Combine(DataDirectory, "history");

    /// <summary>必须在读取任何配置、创建日志目录或安装系统钩子之前调用。</summary>
    public static void Begin()
    {
        if (Interlocked.Exchange(ref _begun, 1) != 0)
            return;

        var parsed = SmokeProtocol.Parse(
            Environment.GetCommandLineArgs(),
            Environment.GetEnvironmentVariable,
            Path.GetTempPath());

        if (parsed.Kind == SmokeParseKind.NotRequested)
            return;

        IsRequested = true;
        if (parsed.Kind != SmokeParseKind.Valid || parsed.Launch is null)
        {
            Console.Error.WriteLine($"ISHWY_SMOKE_INVALID_CONTRACT: {parsed.Error}");
            Exit(InvalidContractExitCode);
        }

        _launch = parsed.Launch;
        DataRoot = parsed.Launch!.Root;
        _state = new SmokeRunState();
        StartWatchdog();
    }

    /// <summary>App.InitializeComponent 成功返回后调用。</summary>
    public static void MarkXamlInitialized()
    {
        if (_launch is null)
            return;

        RecordCheck(SmokeProtocol.XamlInitializedCheck);
    }

    /// <summary>
    /// MainWindow.Loaded 里调用：记录主窗已加载，并把 dispatcher-roundtrip 排进 UI 队列。
    /// 只有队列真正执行到才算第三项完成，所以「计时到点」永远无法制造成功。
    /// </summary>
    public static void NotifyMainWindowLoaded(DispatcherQueue queue)
    {
        if (_launch is null)
            return;

        RecordCheck(SmokeProtocol.MainWindowLoadedCheck);

        bool queued;
        lock (Gate)
        {
            if (_roundtripRequested)
                return;

            _roundtripRequested = true;
            queued = queue.TryEnqueue(() => RecordCheck(SmokeProtocol.DispatcherRoundtripCheck));
        }

        if (!queued)
            Fail("dispatcher-queue-unavailable");
    }

    /// <summary>已有正常实例：不 Set 激活事件，只写失败结果并以退出码 3 结束。</summary>
    public static void FailInstanceConflict()
    {
        Complete(InstanceConflictExitCode, "another-instance-is-running");
    }

    public static void Fail(string reason)
    {
        Complete(FailureExitCode, reason);
    }

    private static void RecordCheck(string check)
    {
        var outcome = _state?.TryRecordCheck(check);
        if (outcome is not null)
            Complete(outcome.Success ? 0 : FailureExitCode, outcome.Error);
    }

    private static void Complete(int exitCode, string? error)
    {
        var state = _state;
        var launch = _launch;
        if (state is null || launch is null)
        {
            // 契约还没建立（或已非法）：没有可信的 root，只能直接退出。
            Exit(exitCode == 0 ? FailureExitCode : exitCode);
            return;
        }

        var success = exitCode == 0;
        SmokeRunOutcome? failure = null;
        if (!success)
        {
            failure = state.TryFail(error ?? "unspecified");
            if (failure is null)
                return; // 已有终态：成功的迟到失败不能覆盖，也不再重复写文件。
        }

        var result = new SmokeResult
        {
            Nonce = launch.Nonce,
            ProcessId = Environment.ProcessId,
            ExecutablePath = ResolveExecutablePath(),
            DataRoot = launch.Root,
            Success = success,
            Checks = success ? SmokeProtocol.SuccessChecks : failure!.Checks,
            Error = success ? null : failure!.Error,
        };

        if (!TryWriteResult(launch.Root, result))
        {
            Exit(ResultWriteFailedExitCode);
            return;
        }

        Exit(exitCode);
    }

    private static bool TryWriteResult(string root, SmokeResult result)
    {
        lock (Gate)
        {
            if (_resultWritten)
                return true;

            var path = Path.Combine(root, SmokeProtocol.ResultFileName);
            var temp = path + ".tmp";
            try
            {
                File.WriteAllBytes(temp, Utf8NoBom.GetBytes(SmokeProtocol.Serialize(result)));
                File.Move(temp, path, overwrite: true);
                _resultWritten = true;
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ISHWY_SMOKE_RESULT_WRITE_FAILED: {ex.Message}");
                try
                {
                    File.Delete(temp);
                }
                catch (Exception)
                {
                }

                return false;
            }
        }
    }

    private static string ResolveExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path))
            return path!;

        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>看门狗只允许把「没等到 Loaded + 队列往返」判成失败，永远不产生成功。</summary>
    private static void StartWatchdog()
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(WatchdogSeconds));
            Fail("timeout-waiting-for-main-window-roundtrip");
        })
        {
            IsBackground = true,
            Name = "PrtScr.SmokeWatchdog",
        };
        thread.Start();
    }

    private static void Exit(int exitCode) => PInvoke.ExitProcess((uint)exitCode);
}
