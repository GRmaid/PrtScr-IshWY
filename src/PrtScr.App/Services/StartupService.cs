using Microsoft.Win32;

namespace PrtScr.App.Services;

/// <summary>
/// 开机自启动：写 HKCU 的 Run 项（无需管理员权限）。
/// 启动命令带 --startup，程序启动后静默驻留托盘，不弹设置窗。
/// </summary>
internal static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "IshWY";
    private const string StartupArgument = "--startup";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            App.Log($"Startup: 读取开机启动项失败 {ex.Message}");
            return false;
        }
    }

    public static string? GetRegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    public static bool TrySetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "无法打开当前用户的启动项注册表";
                return false;
            }

            if (enabled)
            {
                var command = BuildCommand();
                key.SetValue(ValueName, command, RegistryValueKind.String);
                App.Log($"Startup: 已启用，command={command}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                App.Log("Startup: 已禁用");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            App.Log($"Startup: 写入开机启动项失败 {ex}");
            return false;
        }
    }

    /// <summary>
    /// 便携版被移动/改名后，Run 项还指向旧路径。每次启动时顺手校正。
    /// </summary>
    public static void RefreshIfEnabled()
    {
        var registered = GetRegisteredCommand();
        if (string.IsNullOrWhiteSpace(registered))
            return;

        var expected = BuildCommand();
        if (string.Equals(registered, expected, StringComparison.OrdinalIgnoreCase))
            return;

        if (TrySetEnabled(true, out var error))
            App.Log($"Startup: 已更新登录启动路径 {registered} -> {expected}");
        else
            App.Log($"Startup: 更新登录启动路径失败 {error}");
    }

    public static string BuildCommand() => $"\"{ResolveExecutablePath()}\" {StartupArgument}";

    private static string ResolveExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) ||
            Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            // dotnet run 时 ProcessPath 是 dotnet.exe，注册它没有意义；优先用同目录的 apphost。
            var candidate = Path.Combine(AppContext.BaseDirectory, "IshWY.exe");
            if (File.Exists(candidate))
                path = candidate;
        }

        return Path.GetFullPath(path ?? Path.Combine(AppContext.BaseDirectory, "IshWY.exe"));
    }
}
