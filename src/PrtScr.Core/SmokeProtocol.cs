using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PrtScr.Core;

/// <summary>
/// App 启动冒烟（--smoke）的冻结协议 v1。
/// 本类型只做纯规则判定与结果序列化，不读取真实配置、不触碰 UI、不写系统状态。
/// </summary>
public static class SmokeProtocol
{
    public const string ModeArgument = "--smoke";
    public const string RootVariable = "ISHWY_SMOKE_ROOT";
    public const string NonceVariable = "ISHWY_SMOKE_NONCE";
    public const string ResultFileName = "smoke-result.json";
    public const int SchemaVersion = 1;

    public const string XamlInitializedCheck = "xaml-initialized";
    public const string MainWindowLoadedCheck = "main-window-loaded";
    public const string DispatcherRoundtripCheck = "dispatcher-roundtrip";

    /// <summary>成功时 checks 必须恰好是这三项（顺序固定）。</summary>
    public static readonly IReadOnlyList<string> SuccessChecks =
    [
        XamlInitializedCheck,
        MainWindowLoadedCheck,
        DispatcherRoundtripCheck,
    ];

    private const string SmokeRootName = "IshWY-smoke";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>解析命令行与环境，判定是否进入冒烟以及隔离根是否可信。</summary>
    public static SmokeParseResult Parse(string[]? args, Func<string, string?>? environment, string? tempPath)
    {
        if (args is null || !HasModeArgument(args))
            return SmokeParseResult.NotRequested();

        var nonce = TrimToNull(environment?.Invoke(NonceVariable));
        var rootValue = TrimToNull(environment?.Invoke(RootVariable));
        if (nonce is null)
            return SmokeParseResult.Invalid("missing-nonce");
        if (rootValue is null)
            return SmokeParseResult.Invalid("missing-root");
        if (!IsNonce(nonce))
            return SmokeParseResult.Invalid("nonce-not-guid-n");

        string root;
        try
        {
            if (!Path.IsPathFullyQualified(rootValue))
                return SmokeParseResult.Invalid("root-not-absolute");
            if (HasDotSegments(rootValue))
                return SmokeParseResult.Invalid("root-has-dot-segments");
            root = Path.GetFullPath(rootValue);
        }
        catch (Exception)
        {
            return SmokeParseResult.Invalid("root-not-a-path");
        }

        try
        {
            var rootDirectory = new DirectoryInfo(root);
            if (!string.Equals(rootDirectory.Name, nonce, StringComparison.Ordinal))
                return SmokeParseResult.Invalid("root-leaf-not-nonce");
            var smokeRoot = rootDirectory.Parent;
            if (smokeRoot is null || !string.Equals(smokeRoot.Name, SmokeRootName, StringComparison.Ordinal))
                return SmokeParseResult.Invalid("root-parent-not-smoke-root");

            var declaredTemp = TryNormalizeDirectory(tempPath);
            if (declaredTemp is null)
                return SmokeParseResult.Invalid("temp-unavailable");
            if (smokeRoot.Parent is null || !string.Equals(smokeRoot.Parent.FullName, declaredTemp, StringComparison.OrdinalIgnoreCase))
                return SmokeParseResult.Invalid("root-outside-temp");

            if (!rootDirectory.Exists)
                return SmokeParseResult.Invalid("root-missing");
            if (HasReparsePoint(rootDirectory))
                return SmokeParseResult.Invalid("root-reparse-point");
            if (!IsEmpty(rootDirectory))
                return SmokeParseResult.Invalid("root-not-empty");
            if (!CanWrite(rootDirectory))
                return SmokeParseResult.Invalid("root-not-writable");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // 目录属性/枚举/重解析点探测本身可能因权限或 IO 失败。冒烟只接受「能证明隔离」的根，
            // 所以环境读取异常统一归 Invalid，不能让它穿透到 App 构造变成非契约的崩溃退出。
            return SmokeParseResult.Invalid("root-unreadable");
        }

        return SmokeParseResult.Valid(new SmokeLaunch(nonce, root));
    }

    public static bool IsNonce(string? value) =>
        value is { Length: 32 } && Guid.TryParseExact(value, "N", out _);

    /// <summary>成功时必须恰好包含三项且无多余项。</summary>
    public static bool HasExactSuccessChecks(IReadOnlyList<string>? checks) =>
        checks is { Count: 3 } &&
        checks[0] == XamlInitializedCheck &&
        checks[1] == MainWindowLoadedCheck &&
        checks[2] == DispatcherRoundtripCheck;

    public static bool IsKnownCheck(string? check) =>
        check is XamlInitializedCheck or MainWindowLoadedCheck or DispatcherRoundtripCheck;

    public static string Serialize(SmokeResult result) => JsonSerializer.Serialize(result, SerializerOptions);

    private static bool HasModeArgument(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg?.Trim(), ModeArgument, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? TrimToNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool HasDotSegments(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(IsDotSegment);

    private static bool IsDotSegment(string segment) => segment is "." or "..";

    private static string? TryNormalizeDirectory(string? path)
    {
        var trimmed = TrimToNull(path);
        if (trimmed is null)
            return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>根目录或它的父级是重解析点（符号链接/联接）时拒绝。</summary>
    private static bool HasReparsePoint(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            return true;
        for (var current = directory.Parent; current is not null; current = current.Parent)
        {
            if (string.Equals(current.FullName, current.Root.FullName, StringComparison.OrdinalIgnoreCase))
                return false;
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
        }

        return false;
    }

    private static bool IsEmpty(DirectoryInfo directory)
    {
        using var enumerator = directory.EnumerateFileSystemInfos().GetEnumerator();
        return !enumerator.MoveNext();
    }

    /// <summary>用「新建探测文件 + DeleteOnClose」验证可写，宁可失败也不往 root 留残留文件。</summary>
    private static bool CanWrite(DirectoryInfo directory)
    {
        try
        {
            var probe = Path.Combine(directory.FullName, ".write-probe");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                FileOptions.DeleteOnClose);
            stream.WriteByte(0x00);
            stream.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public enum SmokeParseKind
{
    NotRequested,
    Invalid,
    Valid,
}

public sealed class SmokeParseResult
{
    private SmokeParseResult(SmokeParseKind kind, string? error, SmokeLaunch? launch)
    {
        Kind = kind;
        Error = error;
        Launch = launch;
    }

    public SmokeParseKind Kind { get; }
    public string? Error { get; }
    public SmokeLaunch? Launch { get; }

    public static SmokeParseResult NotRequested() => new(SmokeParseKind.NotRequested, null, null);
    public static SmokeParseResult Invalid(string error) => new(SmokeParseKind.Invalid, error, null);
    public static SmokeParseResult Valid(SmokeLaunch launch) => new(SmokeParseKind.Valid, null, launch);
}

/// <summary>已校验的冒烟启动参数；DataRoot 永远等于隔离根。</summary>
public sealed record SmokeLaunch(string Nonce, string Root)
{
    public string DataDirectory => Path.Combine(Root, "data");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string HistoryDirectory => Path.Combine(DataDirectory, "history");
}

/// <summary>smoke-result.json 的形状（camelCase）。</summary>
public sealed class SmokeResult
{
    public int SchemaVersion { get; init; } = SmokeProtocol.SchemaVersion;
    public string Nonce { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public string ExecutablePath { get; init; } = string.Empty;
    public string DataRoot { get; init; } = string.Empty;
    public bool Success { get; init; }
    public IReadOnlyList<string> Checks { get; init; } = [];
    public string? Error { get; init; }
}
