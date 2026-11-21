using System;
using System.IO;
using System.Linq;
using PrtScr.Core;
using Xunit;

namespace PrtScr.Core.Tests;

public sealed class SmokeProtocolTests : IDisposable
{
    private readonly string _nonce = Guid.NewGuid().ToString("N");
    private readonly string _smokeRoot = Path.Combine(Path.GetTempPath(), "IshWY-smoke");
    private readonly string _root;

    public SmokeProtocolTests()
    {
        Directory.CreateDirectory(_smokeRoot);
        _root = Path.Combine(_smokeRoot, _nonce);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private static Func<string, string?> Env(string? nonce, string? root) => name => name switch
    {
        SmokeProtocol.NonceVariable => nonce,
        SmokeProtocol.RootVariable => root,
        _ => null,
    };

    private SmokeParseResult ParseValid(string[]? args = null) =>
        SmokeProtocol.Parse(args ?? ["IshWY.exe", SmokeProtocol.ModeArgument], Env(_nonce, _root), Path.GetTempPath());

    [Fact]
    public void WithoutModeArgument_IsNotRequested()
    {
        var result = SmokeProtocol.Parse(["IshWY.exe"], Env(_nonce, _root), Path.GetTempPath());
        Assert.Equal(SmokeParseKind.NotRequested, result.Kind);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ValidContext_IsAccepted()
    {
        var result = ParseValid();

        Assert.Equal(SmokeParseKind.Valid, result.Kind);
        Assert.True(SmokeProtocol.HasExactSuccessChecks(SmokeProtocol.SuccessChecks));
        Assert.NotNull(result.Launch);
        Assert.Equal(_nonce, result.Launch!.Nonce);
        Assert.Equal(Path.GetFullPath(_root), result.Launch.Root);
        Assert.Equal(Path.Combine(_root, "data", "history"), result.Launch.HistoryDirectory);
    }

    [Theory]
    [InlineData("missing-nonce", "missing-nonce")]
    [InlineData("missing-root", "missing-root")]
    [InlineData("nonce-not-guid-n", "nonce-not-guid-n")]
    public void MissingOrMalformedEnvironment_FailsClosed(string expected, string scenario)
    {
        var result = scenario switch
        {
            "missing-nonce" => SmokeProtocol.Parse(["IshWY.exe", "--smoke"], Env(null, _root), Path.GetTempPath()),
            "missing-root" => SmokeProtocol.Parse(["IshWY.exe", "--smoke"], Env(_nonce, null), Path.GetTempPath()),
            _ => SmokeProtocol.Parse(["IshWY.exe", "--smoke"], Env("not-a-guid", _root), Path.GetTempPath()),
        };

        Assert.Equal(SmokeParseKind.Invalid, result.Kind);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public void RootLeafMustBeNonce()
    {
        var other = Path.Combine(_smokeRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            var result = SmokeProtocol.Parse(["IshWY.exe", "--smoke"], Env(_nonce, other), Path.GetTempPath());
            Assert.Equal("root-leaf-not-nonce", result.Error);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public void RootMustLiveUnderTempIshWySmoke()
    {
        // 叶子名与 nonce 一致、但父目录不是 IshWY-smoke：应命中 root-parent-not-smoke-root。
        var outside = Path.Combine(Path.GetTempPath(), _nonce);
        Directory.CreateDirectory(outside);
        try
        {
            var result = SmokeProtocol.Parse(["IshWY.exe", "--smoke"], Env(_nonce, outside), Path.GetTempPath());
            Assert.Equal("root-parent-not-smoke-root", result.Error);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void NonEmptyRootIsRejected()
    {
        File.WriteAllText(Path.Combine(_root, "real-data.txt"), "x");
        var result = ParseValid();
        Assert.Equal("root-not-empty", result.Error);
    }

    [Fact]
    public void MissingRootIsRejected()
    {
        Directory.Delete(_root);
        var result = ParseValid();
        Assert.Equal("root-missing", result.Error);
    }

    [Fact]
    public void RelativeRootIsRejected()
    {
        var result = SmokeProtocol.Parse(["IshWY.exe", "--smoke"], Env(_nonce, "IshWY-smoke/" + _nonce), Path.GetTempPath());
        Assert.Equal("root-not-absolute", result.Error);
    }

    [Fact]
    public void DotSegmentsAreRejected()
    {
        var raw = Path.Combine(_smokeRoot, "..", "IshWY-smoke", _nonce);
        var result = SmokeProtocol.Parse(["IshWY.exe", "--smoke"], Env(_nonce, raw), Path.GetTempPath());
        Assert.Equal("root-has-dot-segments", result.Error);
    }

    [Fact]
    public void Serialize_UsesCamelCaseFrozenFields()
    {
        var json = SmokeProtocol.Serialize(new SmokeResult
        {
            Nonce = _nonce,
            ProcessId = 4242,
            ExecutablePath = @"C:\tmp\IshWY.exe",
            DataRoot = _root,
            Success = true,
            Checks = SmokeProtocol.SuccessChecks,
            Error = null,
        });

        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"nonce\":\"" + _nonce + "\"", json);
        Assert.Contains("\"processId\":4242", json);
        Assert.Contains("\"executablePath\"", json);
        Assert.Contains("\"dataRoot\"", json);
        Assert.Contains("\"success\":true", json);
        Assert.Contains("\"dispatcher-roundtrip\"", json);
        Assert.Contains("\"error\":null", json);
        Assert.DoesNotContain("\"SchemaVersion\"", json);
    }

    [Fact]
    public void HasExactSuccessChecks_RejectsReorderedOrExtraChecks()
    {
        Assert.True(SmokeProtocol.HasExactSuccessChecks(SmokeProtocol.SuccessChecks));
        Assert.False(SmokeProtocol.HasExactSuccessChecks(
            [SmokeProtocol.MainWindowLoadedCheck, SmokeProtocol.XamlInitializedCheck, SmokeProtocol.DispatcherRoundtripCheck]));
        Assert.False(SmokeProtocol.HasExactSuccessChecks(
            [SmokeProtocol.XamlInitializedCheck, SmokeProtocol.MainWindowLoadedCheck]));
        Assert.False(SmokeProtocol.HasExactSuccessChecks(
        [
            SmokeProtocol.XamlInitializedCheck, SmokeProtocol.MainWindowLoadedCheck,
            SmokeProtocol.DispatcherRoundtripCheck, "extra",
        ]));
    }

    [Fact]
    public void RootOutsideDeclaredTempIsRejected()
    {
        // 叶子名与父目录名都合法，但 IshWY-smoke 不在声明 temp 之下：必须在读取目录前判 root-outside-temp。
        var outside = Path.Combine(Path.GetTempPath(), "IshWY-elsewhere", "IshWY-smoke", _nonce);
        var result = SmokeProtocol.Parse(["IshWY.exe", "--smoke"], Env(_nonce, outside), Path.GetTempPath());

        Assert.Equal(SmokeParseKind.Invalid, result.Kind);
        Assert.Equal("root-outside-temp", result.Error);
    }

    [Fact]
    public void MalformedRootPathIsRejected()
    {
        // 内嵌 NUL 的路径无法规范化：Path.GetFullPath 抛异常，应归一成 root-not-a-path，不能崩出 Parse。
        var malformed = Path.Combine(_smokeRoot, _nonce) + "\0";
        var result = SmokeProtocol.Parse(["IshWY.exe", "--smoke"], Env(_nonce, malformed), Path.GetTempPath());

        Assert.Equal(SmokeParseKind.Invalid, result.Kind);
        Assert.Equal("root-not-a-path", result.Error);
    }

    [Fact]
    public void ReparsePointRootIsRejectedWhenConstructible()
    {
        // 符号链接需要开发者模式或相应权限；构造不了就记为未测（不改 ACL、不提权）。
        var target = Path.Combine(_smokeRoot, "link-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            Directory.Delete(_root);
            try
            {
                Directory.CreateSymbolicLink(_root, target);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            var result = ParseValid();

            Assert.Equal(SmokeParseKind.Invalid, result.Kind);
            Assert.Equal("root-reparse-point", result.Error);
        }
        finally
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }
}
