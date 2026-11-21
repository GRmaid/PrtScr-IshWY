using System;
using System.Collections.Generic;

namespace PrtScr.Core;

/// <summary>
/// 冒烟运行的单终态状态机：三个检查必须齐全才算成功；一旦失败/成功即终态，
/// 迟到的成功不能覆盖失败，重复的检查条目被忽略（保持 canonical 顺序）。
/// </summary>
public sealed class SmokeRunState
{
    private readonly object _gate = new();
    private readonly List<string> _checks = [];
    private bool _completed;
    private string? _error;

    /// <summary>记录一个真实完成事件；只有第三项补齐时才返回终态结果。</summary>
    public SmokeRunOutcome? TryRecordCheck(string check)
    {
        lock (_gate)
        {
            if (_completed || !SmokeProtocol.IsKnownCheck(check) || _checks.Contains(check))
                return null;

            _checks.Add(check);
            if (!SmokeProtocol.HasExactSuccessChecks(Canonical()))
                return null;

            _completed = true;
            _error = null;
            return new SmokeRunOutcome(true, Canonical(), null);
        }
    }

    /// <summary>失败终态；成功之后调用返回 null，不改变已经写出的结果。</summary>
    public SmokeRunOutcome? TryFail(string error)
    {
        lock (_gate)
        {
            if (_completed)
                return null;

            _completed = true;
            _error = string.IsNullOrWhiteSpace(error) ? "unspecified" : error;
            return new SmokeRunOutcome(false, Canonical(), _error);
        }
    }

    private List<string> Canonical() => [.. _checks];
}

public sealed record SmokeRunOutcome(bool Success, IReadOnlyList<string> Checks, string? Error);
