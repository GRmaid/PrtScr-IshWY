namespace PrtScr.Core;

/// <summary>
/// 打开会话槽的入口种类（接口综合裁定里的 surface）。同一时刻最多一种入口占用槽位。
/// </summary>
public enum CaptureSessionKind
{
    /// <summary>没有入口（空槽）。</summary>
    None = 0,

    /// <summary>取景器：冻结帧、选区/选窗、确认。</summary>
    Overlay = 1,

    /// <summary>快速全屏截图：不打开取景器，直接抓光标所在显示器。</summary>
    Quick = 2,
}

/// <summary>
/// 调用方打算执行的动作。只喂给 <see cref="CaptureSessionCoordinator.Decide"/> 做提示，
/// 不改变任何状态；正确性必须由对应的 Try 方法在同一个同步边界里重新判定。
/// </summary>
public enum CaptureIntent
{
    /// <summary>未指定（占位）。</summary>
    None = 0,

    /// <summary>进入取景（F1 的取景意图）。</summary>
    StartOverlay = 1,

    /// <summary>快速全屏截图（不取景）。</summary>
    StartQuick = 2,

    /// <summary>请求提交权。</summary>
    LatchCommit = 3,

    /// <summary>退出封口。</summary>
    Shutdown = 4,
}

/// <summary>一次仲裁的结论。Granted 才是允许；其余都是拒绝原因（AlreadyLatched 为幂等重复的专用结论）。</summary>
public enum CaptureArbitration
{
    /// <summary>未仲裁（占位）。</summary>
    None = 0,

    /// <summary>允许。</summary>
    Granted = 1,

    /// <summary>槽已被其他会话或预留占用。</summary>
    Busy = 2,

    /// <summary>取景入口缺少本次有效预留。</summary>
    ReservationRequired = 3,

    /// <summary>给出的预留不是当前代际（已被消费、已释放或已过期）。</summary>
    ReservationConflict = 4,

    /// <summary>槽上有一个已 latch 的会话，抢占会破坏不可逆写入。</summary>
    CommitLatched = 5,

    /// <summary>同一所有者重复请求提交权（幂等）。</summary>
    AlreadyLatched = 6,

    /// <summary>归属或状态不满足，且没有更具体的拒绝原因。</summary>
    Denied = 7,

    /// <summary>退出已封口，不再接受新开槽/新预留。</summary>
    ShuttingDown = 8,
}

/// <summary>
/// 提交租约：latch 成功后给出的一次性凭据，证明该会话已取得不可逆写入的提交权。
/// 租约只描述事实，释放仍由 End 统一完成；占位（<see cref="None"/>）表示没有租约。
/// </summary>
public readonly record struct CaptureCommitLease(
    Guid SessionId,
    CaptureSessionKind Kind,
    long SlotGeneration)
{
    /// <summary>没有租约。</summary>
    public static CaptureCommitLease None => default;

    /// <summary>是否为空租约。</summary>
    public bool IsEmpty => SessionId == Guid.Empty;
}

/// <summary>
/// 会话槽的一份完整事实快照：Current/Owner/surface/state/结果字段/commit/预留/退出。
/// 由 <see cref="CaptureSessionCoordinator.Snapshot(Guid)"/> 在同一同步边界内生成，只读。
/// </summary>
public sealed record CaptureSessionSnapshot(
    Guid RequestedSessionId,
    Guid CurrentSessionId,
    Guid OwnerSessionId,
    CaptureSessionKind OwnerKind,
    CaptureSessionState State,
    CaptureOrigin Origin,
    CaptureEngine Engine,
    CaptureFailure Failure,
    bool PrintWindowTimedOut,
    string? Detail,
    bool CommitLatched,
    CaptureCommitLease CommitLease,
    Guid ReservationId,
    long ReservationGeneration,
    bool Shutdown,
    long SlotGeneration,
    bool Active,
    bool IsCurrent,
    bool IsOwner)
{
    /// <summary>槽是否有活动会话或预留（预留本身也算忙；已结束的最近身份不算忙）。</summary>
    public bool IsBusy => Active || ReservationId != Guid.Empty;

    /// <summary>退出是否已封口。</summary>
    public bool IsShuttingDown => Shutdown;

    /// <summary>按当前结果字段构造的取景结果；图像成功（Completed）不等于 Failure 为 None。</summary>
    public CaptureResult Result
        => new(OwnerSessionId, Origin, Engine, Failure, PrintWindowTimedOut, 0, Detail);
}

/// <summary>
/// 截图会话的唯一槽位/状态/结果元数据真源（P-14b′）。所有读写都在同一个同步边界内完成：
/// 取景、快速截图、预留、抢占、提交权与退出封口不再各自持有第二份真源。
/// </summary>
/// <remarks>
/// 活动真源是内部 active 标记：owner 非空只表示最近一次 Begin 的身份，会话结束后保留但不代表忙碌。
/// 本类型只做纯规则，不引用 UI/Drawing/Capture/OCR，也不等待任何线程。
/// 历史日志模板（begin/state/terminate/finish/finished/result/invalidated）与既有 CaptureService 逐字一致；
/// 收尾日志顺序保持 finished → state → result。
/// </remarks>
public sealed class CaptureSessionCoordinator
{
    /// <summary>退出时协作等待采集线程的单个总预算。Core 只给常量，不等待任何线程。</summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private readonly Action<string> _log;

    private bool _active;
    private CaptureSessionState _state = CaptureSessionState.Idle;
    private Guid _currentSessionId;
    private Guid _ownerSessionId;
    private CaptureSessionKind _ownerKind;
    private long _slotGeneration;
    private CaptureOrigin _origin;
    private CaptureEngine _engine;
    private CaptureFailure _failure;
    private bool _printWindowTimedOut;
    private string? _detail;
    private bool _commitLatched;
    private CaptureCommitLease _commitLease;
    private Guid _reservationId;
    private long _reservationGeneration;
    private bool _shutdown;

    /// <summary>以日志回调构造协调者；回调在锁外逐行调用，不会在持有同步边界时重入。</summary>
    public CaptureSessionCoordinator(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>槽是否忙：有活动会话或持有预留（已结束的最近身份不算忙）。</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
                return IsBusyLocked;
        }
    }

    /// <summary>槽当前状态；没有活动会话时为 Idle。</summary>
    public CaptureSessionState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    /// <summary>当前会话的有效身份；取消/失效或 Quick 收尾后为 Guid.Empty；成功取景收尾保留到 Invalidate 或下一次 Begin。</summary>
    public Guid CurrentSessionId
    {
        get
        {
            lock (_gate)
                return _currentSessionId;
        }
    }

    /// <summary>槽的归属会话（最近一次 Begin 的 owner）；结束后仍保留供身份与代际判断，下一次 Begin 覆盖。</summary>
    public Guid OwnerSessionId
    {
        get
        {
            lock (_gate)
                return _ownerSessionId;
        }
    }

    /// <summary>槽的入口种类（surface）。</summary>
    public CaptureSessionKind OwnerKind
    {
        get
        {
            lock (_gate)
                return _ownerKind;
        }
    }

    /// <summary>当前会话是否已取得提交权。</summary>
    public bool IsCommitLatched
    {
        get
        {
            lock (_gate)
                return _commitLatched;
        }
    }

    /// <summary>退出是否已永久封口。</summary>
    public bool IsShuttingDown
    {
        get
        {
            lock (_gate)
                return _shutdown;
        }
    }

    /// <summary>当前取景预留；没有预留时为 Guid.Empty。</summary>
    public Guid OverlayReservationId
    {
        get
        {
            lock (_gate)
                return _reservationId;
        }
    }

    /// <summary>最近一次预留的代际（单调递增）；预留释放后仍保留最后代际，供调用方识别 ABA。</summary>
    public long OverlayReservationGeneration
    {
        get
        {
            lock (_gate)
                return _reservationGeneration;
        }
    }

    /// <summary>槽代际；每次 Begin 递增，用于识别旧会话。</summary>
    public long SlotGeneration
    {
        get
        {
            lock (_gate)
                return _slotGeneration;
        }
    }

    /// <summary>当前会话是否有效（晚到写入前校验）。Guid.Empty 表示启动期无身份，按原语义返回 false。</summary>
    public bool IsCurrent(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return false;

        lock (_gate)
            return sessionId == _currentSessionId;
    }

    /// <summary>会话是否仍是槽的归属（End 的归属判定）。Guid.Empty 是启动期失败，按原语义放行。</summary>
    public bool IsOwner(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return true;

        lock (_gate)
            return sessionId == _ownerSessionId;
    }

    /// <summary>
    /// 仲裁提示：回答「如果现在做这件事会得到什么结论」，不改变任何状态、不写日志。
    /// 正确性不得依赖 Decide 之后再做一次未经校验的变更——开槽/预留/提交/封口必须在 Try 方法内重新判定。
    /// </summary>
    public CaptureArbitration Decide(
        CaptureIntent intent,
        Guid sessionId = default,
        Guid reservationId = default)
    {
        lock (_gate)
        {
            switch (intent)
            {
                case CaptureIntent.StartOverlay:
                    if (_shutdown)
                        return CaptureArbitration.ShuttingDown;
                    // 与 Begin 的预留校验同序同判：有预留但未给/给错时一律拒绝。
                    if (_reservationId == Guid.Empty)
                        return CaptureArbitration.ReservationRequired;
                    if (reservationId == Guid.Empty)
                        return CaptureArbitration.ReservationRequired;
                    if (reservationId != _reservationId)
                        return CaptureArbitration.ReservationConflict;
                    if (_active)
                    {
                        if (_ownerKind == CaptureSessionKind.Overlay)
                            return CaptureArbitration.Busy;
                        return _commitLatched ? CaptureArbitration.CommitLatched : CaptureArbitration.Busy;
                    }
                    return CaptureArbitration.Granted;

                case CaptureIntent.StartQuick:
                    if (_shutdown)
                        return CaptureArbitration.ShuttingDown;
                    if (_active)
                        return CaptureArbitration.Busy;
                    if (_reservationId != Guid.Empty)
                        return CaptureArbitration.Busy;
                    return CaptureArbitration.Granted;

                case CaptureIntent.LatchCommit:
                    if (!_active || sessionId == Guid.Empty || sessionId != _currentSessionId || sessionId != _ownerSessionId)
                        return CaptureArbitration.Denied;
                    if (_commitLatched)
                        return CaptureArbitration.AlreadyLatched;
                    if (_state != CaptureSessionState.Committing)
                        return CaptureArbitration.Denied;
                    return _shutdown ? CaptureArbitration.ShuttingDown : CaptureArbitration.Granted;

                case CaptureIntent.Shutdown:
                    return _shutdown ? CaptureArbitration.ShuttingDown : CaptureArbitration.Granted;

                default:
                    return CaptureArbitration.None;
            }
        }
    }

    /// <summary>
    /// 打开会话槽（Idle → Preparing）。Quick 只在空槽、无预留且未封口时可开；
    /// Overlay 必须消费本次有效的预留（不允任意 Guid 抢槽）。成功返回快照，拒绝返回 null。
    /// </summary>
    public CaptureSessionSnapshot? Begin(
        Guid sessionId,
        CaptureSessionKind kind,
        Guid reservationId = default)
    {
        var logs = new List<string>(3);
        CaptureSessionSnapshot? snapshot;
        lock (_gate)
            TryBeginLocked(sessionId, kind, reservationId, logs, out snapshot);
        Flush(logs);
        return snapshot;
    }

    /// <summary>
    /// 原子地预留取景能力：判断 + 未 latch 的 Quick 失效/End(Cancel) + 空槽预留一次完成。
    /// 遇活动 Overlay、已 latch 的 Quick、已有其他预留或退出封口时返回 false。
    /// </summary>
    public bool TryReserveOverlay(out Guid reservationId, out Guid preemptedQuickId)
    {
        var logs = new List<string>(8);
        bool reserved;
        lock (_gate)
            reserved = TryReserveOverlayLocked(logs, out reservationId, out preemptedQuickId);
        Flush(logs);
        return reserved;
    }

    /// <summary>按当前代际释放预留；旧代际的释放不能清掉新预留。</summary>
    public bool ReleaseOverlayReservation(Guid reservationId)
    {
        var logs = new List<string>(3);
        bool released;
        lock (_gate)
            released = ReleaseReservationLocked(reservationId, _reservationGeneration, logs);
        Flush(logs);
        return released;
    }

    /// <summary>按显式代际释放预留；代际不符视为旧释放（ABA），不清当前预留。</summary>
    public bool ReleaseOverlayReservation(Guid reservationId, long generation)
    {
        var logs = new List<string>(3);
        bool released;
        lock (_gate)
            released = ReleaseReservationLocked(reservationId, generation, logs);
        Flush(logs);
        return released;
    }

    /// <summary>取消/放弃一次取景：只让身份失效，终态与结果仍由 End 统一写出。latch 后不得取消。</summary>
    public bool Invalidate(Guid sessionId)
    {
        var logs = new List<string>(2);
        bool invalidated;
        lock (_gate)
            invalidated = InvalidateLocked(sessionId, logs);
        Flush(logs);
        return invalidated;
    }

    /// <summary>状态迁移（owner 判定 + CaptureRules.CanTransition）；被拒绝时只记日志。</summary>
    public bool TryTransition(Guid sessionId, CaptureSessionState to, string context)
    {
        var logs = new List<string>(3);
        bool applied;
        lock (_gate)
            applied = TransitionLocked(sessionId, to, context, logs);
        Flush(logs);
        return applied;
    }

    /// <summary>取图成功：记录来源/引擎/超时标记，Capturing → Committing，并写一条结果日志。</summary>
    public bool TryMarkSucceeded(
        Guid sessionId,
        CaptureOrigin origin,
        CaptureEngine engine,
        bool printWindowTimedOut = false)
    {
        var logs = new List<string>(4);
        bool applied;
        lock (_gate)
            applied = TryMarkSucceededLocked(sessionId, origin, engine, printWindowTimedOut, logs);
        Flush(logs);
        return applied;
    }

    /// <summary>取图失败：记录失败分类与原因，写结果日志后由 Capturing/Committing → Selecting（允许直接重试；准备/选择态整体拒绝）。</summary>
    public bool TryMarkFailed(Guid sessionId, CaptureFailure failure, string? detail)
    {
        var logs = new List<string>(4);
        bool applied;
        lock (_gate)
            applied = TryMarkFailedLocked(sessionId, failure, detail, logs);
        Flush(logs);
        return applied;
    }

    /// <summary>请求提交权：只有 active + current + owner + Committing 才 Granted；重复请求为 AlreadyLatched（返回既有租约，不是新发许可）。</summary>
    public CaptureArbitration TryLatchCommit(Guid sessionId, out CaptureCommitLease lease)
    {
        var logs = new List<string>(3);
        CaptureArbitration decision;
        lock (_gate)
            decision = TryLatchCommitLocked(sessionId, logs, out lease);
        Flush(logs);
        return decision;
    }

    /// <summary>
    /// 记录提交阶段的附属写入失败（只接受 ClipboardFailed / HistoryFailed）。
    /// 第一次失败成为 Failure 主值，同时失败只追加 Detail，不覆盖第一项，也不发中间结果。
    /// </summary>
    public bool TryNoteCommitFailure(Guid sessionId, CaptureFailure failure)
    {
        var logs = new List<string>(2);
        bool noted;
        lock (_gate)
            noted = TryNoteCommitFailureLocked(sessionId, failure, logs);
        Flush(logs);
        return noted;
    }

    /// <summary>会话收尾的唯一漏斗（字符串原因，日志原文逐字保留）。返回终态快照；被忽略返回 null。</summary>
    public CaptureSessionSnapshot? End(Guid sessionId, string reason)
    {
        var logs = new List<string>(5);
        CaptureSessionSnapshot? snapshot;
        lock (_gate)
            snapshot = EndLocked(sessionId, reason, logs);
        Flush(logs);
        return snapshot;
    }

    /// <summary>会话收尾（强类型原因）；日志 reason 文本由 CaptureRules.EndReasonText 映射。</summary>
    public CaptureSessionSnapshot? End(Guid sessionId, CaptureEndReason reason)
        => End(sessionId, CaptureRules.EndReasonText(reason));

    /// <summary>
    /// 退出封口：永久拒绝新开槽/新预留，未 latch 的活动会话失效（Owner 保留给 End 写终态），
    /// 已 latch 的会话保留到收尾。纯规则，不等待线程；首次封口返回 true。
    /// </summary>
    public bool BeginShutdown()
    {
        var logs = new List<string>(4);
        bool sealedNow;
        lock (_gate)
            sealedNow = BeginShutdownLocked(logs);
        Flush(logs);
        return sealedNow;
    }

    /// <summary>槽的完整事实快照（请求会话视角的 IsCurrent/IsOwner）。</summary>
    public CaptureSessionSnapshot Snapshot(Guid sessionId)
    {
        lock (_gate)
            return BuildSnapshotLocked(sessionId);
    }

    /// <summary>槽的完整事实快照（不针对具体会话；Guid.Empty 的 Owner 语义与 IsOwner 一致）。</summary>
    public CaptureSessionSnapshot Snapshot() => Snapshot(Guid.Empty);

    private bool IsBusyLocked => _active || _reservationId != Guid.Empty;

    private void Flush(List<string> logs)
    {
        foreach (var line in logs)
            _log(line);
    }

    private bool TryBeginLocked(
        Guid sessionId,
        CaptureSessionKind kind,
        Guid reservationId,
        List<string> logs,
        out CaptureSessionSnapshot? snapshot)
    {
        snapshot = null;
        var text = SessionText(sessionId);

        if (sessionId == Guid.Empty || kind == CaptureSessionKind.None)
        {
            logs.Add($"Capture: session {text} begin denied (invalid session or kind={kind})");
            return false;
        }

        if (_shutdown)
        {
            logs.Add($"Capture: session {text} begin denied (shutdown)");
            return false;
        }

        if (_active)
        {
            logs.Add($"Capture: session {text} begin denied (slot busy, owner={SessionText(_ownerSessionId)})");
            return false;
        }

        if (kind == CaptureSessionKind.Quick)
        {
            if (_reservationId != Guid.Empty)
            {
                logs.Add($"Capture: session {text} begin denied (overlay reservation held, reservation={SessionText(_reservationId)})");
                return false;
            }
        }
        else
        {
            if (_reservationId == Guid.Empty || reservationId == Guid.Empty || reservationId != _reservationId)
            {
                logs.Add($"Capture: session {text} begin denied (overlay reservation required, requested={SessionText(reservationId)}, reserved={SessionText(_reservationId)})");
                return false;
            }

            // 消费本次有效预留：之后同一 Guid 不能再次开槽。
            _reservationId = Guid.Empty;
        }

        var from = _state;
        _active = true;
        _ownerSessionId = sessionId;
        _ownerKind = kind;
        _currentSessionId = sessionId;
        _slotGeneration++;
        _commitLatched = false;
        _commitLease = CaptureCommitLease.None;
        _origin = CaptureOrigin.None;
        _engine = CaptureEngine.None;
        _failure = CaptureFailure.None;
        _printWindowTimedOut = false;
        _detail = null;

        _state = CaptureSessionState.Preparing;
        logs.Add(CaptureRules.CanTransition(from, CaptureSessionState.Preparing)
            ? $"Capture: session {SessionText(sessionId)} state {from} -> {CaptureSessionState.Preparing} (begin)"
            : $"Capture: session {SessionText(sessionId)} state {from} -> {CaptureSessionState.Preparing} forced (begin)");

        snapshot = BuildSnapshotLocked(sessionId);
        return true;
    }

    private bool TryReserveOverlayLocked(
        List<string> logs,
        out Guid reservationId,
        out Guid preemptedQuickId)
    {
        reservationId = Guid.Empty;
        preemptedQuickId = Guid.Empty;

        if (_shutdown)
        {
            logs.Add("Capture: overlay reservation denied (shutdown)");
            return false;
        }

        if (_reservationId != Guid.Empty)
        {
            logs.Add($"Capture: overlay reservation denied (already reserved {SessionText(_reservationId)})");
            return false;
        }

        if (_active)
        {
            if (_ownerKind == CaptureSessionKind.Overlay)
            {
                logs.Add($"Capture: overlay reservation denied (overlay session {SessionText(_ownerSessionId)} active)");
                return false;
            }

            if (_commitLatched)
            {
                logs.Add($"Capture: overlay reservation denied (quick session {SessionText(_ownerSessionId)} commit latched)");
                return false;
            }

            // 未 latch 的 Quick：先失效（晚到结果不得写入），再按取消收尾；两步与预留同处一个同步边界。
            var quick = _ownerSessionId;
            InvalidateLocked(quick, logs);
            EndLocked(quick, CaptureRules.EndReasonText(CaptureEndReason.Cancel), logs);
            preemptedQuickId = quick;
        }

        _reservationId = Guid.NewGuid();
        _reservationGeneration++;
        reservationId = _reservationId;
        logs.Add($"Capture: overlay reservation {SessionText(_reservationId)} acquired (generation={_reservationGeneration}, preempted={SessionText(preemptedQuickId)})");
        return true;
    }

    private bool ReleaseReservationLocked(Guid reservationId, long generation, List<string> logs)
    {
        if (reservationId == Guid.Empty)
        {
            logs.Add("Capture: overlay reservation release ignored (empty)");
            return false;
        }

        if (_reservationId != reservationId)
        {
            logs.Add($"Capture: overlay reservation {SessionText(reservationId)} release ignored (stale, current={SessionText(_reservationId)})");
            return false;
        }

        if (generation != _reservationGeneration)
        {
            logs.Add($"Capture: overlay reservation {SessionText(reservationId)} release ignored (generation {generation} != {_reservationGeneration})");
            return false;
        }

        _reservationId = Guid.Empty;
        logs.Add($"Capture: overlay reservation {SessionText(reservationId)} released (generation={generation})");
        return true;
    }

    private bool InvalidateLocked(Guid sessionId, List<string> logs)
    {
        if (sessionId == Guid.Empty)
            return false;

        if (_currentSessionId != sessionId)
            return false;

        if (_commitLatched)
        {
            logs.Add($"Capture: session {SessionText(sessionId)} invalidate ignored (commit latched)");
            return false;
        }

        _currentSessionId = Guid.Empty;
        if (_active && _ownerSessionId == sessionId && CaptureRules.IsActive(_state))
            _failure = CaptureFailure.Canceled;

        logs.Add($"Capture: session {SessionText(sessionId)} invalidated");
        return true;
    }

    private bool TransitionLocked(Guid sessionId, CaptureSessionState to, string context, List<string> logs)
    {
        if (!_active || sessionId == Guid.Empty || sessionId != _currentSessionId || sessionId != _ownerSessionId)
            return false;

        var from = _state;

        if (_commitLatched)
        {
            logs.Add($"Capture: session {SessionText(sessionId)} state {from} -> {to} rejected ({context}, commit latched)");
            return false;
        }

        if (CaptureRules.CanTransition(from, to))
        {
            _state = to;
            logs.Add($"Capture: session {SessionText(sessionId)} state {from} -> {to} ({context})");
            return true;
        }

        logs.Add($"Capture: session {SessionText(sessionId)} state {from} -> {to} rejected ({context})");
        return false;
    }

    private bool TryMarkSucceededLocked(
        Guid sessionId,
        CaptureOrigin origin,
        CaptureEngine engine,
        bool printWindowTimedOut,
        List<string> logs)
    {
        if (!_active || sessionId == Guid.Empty || sessionId != _currentSessionId || sessionId != _ownerSessionId)
            return false;

        if (_commitLatched)
        {
            logs.Add($"Capture: session {SessionText(sessionId)} attempt-success ignored (commit latched)");
            return false;
        }

        var from = _state;
        if (!CaptureRules.CanTransition(from, CaptureSessionState.Committing))
        {
            logs.Add($"Capture: session {SessionText(sessionId)} attempt-success rejected (state={from})");
            return false;
        }

        _origin = origin;
        _engine = engine;
        _printWindowTimedOut = printWindowTimedOut;
        _failure = CaptureFailure.None;
        _detail = null;

        _state = CaptureSessionState.Committing;
        logs.Add($"Capture: session {SessionText(sessionId)} state {from} -> {CaptureSessionState.Committing} (attempt-success)");
        logs.Add(FormatResultLog(BuildResultLocked(), _state, "attempt-success"));
        return true;
    }

    private bool TryMarkFailedLocked(Guid sessionId, CaptureFailure failure, string? detail, List<string> logs)
    {
        if (!_active || sessionId == Guid.Empty || sessionId != _currentSessionId || sessionId != _ownerSessionId)
            return false;

        if (_commitLatched)
        {
            logs.Add($"Capture: session {SessionText(sessionId)} attempt-failed:{failure} ignored (commit latched)");
            return false;
        }

        var from = _state;
        // 只有「取图尝试中」的 Capturing 与「提交阶段」的 Committing 可以回到 Selecting；
        // Preparing/Selecting 上的 Mark 是无尝试的迟到写入，必须整体拒绝且不改元数据。
        if (from != CaptureSessionState.Capturing && from != CaptureSessionState.Committing)
        {
            logs.Add($"Capture: session {SessionText(sessionId)} attempt-failed:{failure} rejected (state={from})");
            return false;
        }

        _failure = failure;
        _detail = detail;
        _printWindowTimedOut = false;

        logs.Add(FormatResultLog(BuildResultLocked(), _state, $"attempt-failed:{failure}"));
        _state = CaptureSessionState.Selecting;
        logs.Add($"Capture: session {SessionText(sessionId)} state {from} -> {CaptureSessionState.Selecting} (attempt-failed:{failure})");
        return true;
    }

    private CaptureArbitration TryLatchCommitLocked(
        Guid sessionId,
        List<string> logs,
        out CaptureCommitLease lease)
    {
        lease = CaptureCommitLease.None;
        var text = SessionText(sessionId);

        if (!_active || sessionId == Guid.Empty || sessionId != _currentSessionId || sessionId != _ownerSessionId)
        {
            logs.Add($"Capture: session {text} commit latch Denied (state={_state}, current={SessionText(_currentSessionId)}, owner={SessionText(_ownerSessionId)})");
            return CaptureArbitration.Denied;
        }

        if (_commitLatched)
        {
            lease = _commitLease;
            logs.Add($"Capture: session {text} commit latch AlreadyLatched (slot={_slotGeneration})");
            return CaptureArbitration.AlreadyLatched;
        }

        if (_state != CaptureSessionState.Committing)
        {
            logs.Add($"Capture: session {text} commit latch Denied (state={_state})");
            return CaptureArbitration.Denied;
        }

        _commitLatched = true;
        _commitLease = new CaptureCommitLease(sessionId, _ownerKind, _slotGeneration);
        lease = _commitLease;
        logs.Add($"Capture: session {text} commit latched (kind={_ownerKind}, slot={_slotGeneration})");
        return CaptureArbitration.Granted;
    }

    private bool TryNoteCommitFailureLocked(Guid sessionId, CaptureFailure failure, List<string> logs)
    {
        var text = SessionText(sessionId);

        if (failure is not (CaptureFailure.ClipboardFailed or CaptureFailure.HistoryFailed))
        {
            logs.Add($"Capture: session {text} commit failure note rejected (failure={failure})");
            return false;
        }

        if (!_active || sessionId == Guid.Empty || sessionId != _currentSessionId || sessionId != _ownerSessionId)
        {
            logs.Add($"Capture: session {text} commit failure note rejected (not active owner)");
            return false;
        }

        if (!_commitLatched)
        {
            logs.Add($"Capture: session {text} commit failure note rejected (commit not latched)");
            return false;
        }

        if (_failure == CaptureFailure.None)
            _failure = failure;

        _detail = AppendCommitFailureDetail(_detail, failure);
        logs.Add($"Capture: session {text} commit failure noted failure={failure} primary={_failure} detail=\"{_detail}\"");
        return true;
    }

    private CaptureSessionSnapshot? EndLocked(Guid sessionId, string reason, List<string> logs)
    {
        var text = SessionText(sessionId);

        // 活动真源是 _active：已结束的最近身份不算 capturing，重复 End 只记 not capturing。
        if (!_active)
        {
            logs.Add($"Capture: session {text} finish ignored (not capturing, reason={reason})");
            return null;
        }

        // Guid.Empty 是启动期无身份查询；活动槽上它不等于 owner，绝不能借它拆掉当前会话。
        if (sessionId != _ownerSessionId)
        {
            logs.Add($"Capture: session {text} finish ignored (stale session, current={SessionText(_ownerSessionId)}, reason={reason})");
            return null;
        }

        string? stateLog = null;
        string? resultLog = null;
        CaptureSessionSnapshot? snapshot = null;

        var endReason = ParseEndReason(reason);
        var from = _state;
        var decision = CaptureRules.TerminateDecision(from, endReason);
        if (decision == CaptureTerminateDecision.Allowed)
        {
            var terminal = TerminalStateFor(endReason);
            _state = terminal;
            stateLog = $"Capture: session {text} state {from} -> {terminal} (terminate:{reason})";
            resultLog = FormatResultLog(BuildResultLocked(), terminal, reason);
            snapshot = BuildSnapshotLocked(sessionId);
        }
        else
        {
            stateLog = $"Capture: session {text} terminate {decision} (state={from}, reason={reason})";
            if (CaptureRules.IsTerminal(from))
                snapshot = BuildSnapshotLocked(sessionId);
        }

        // 收尾只结束活动：保留最近 owner/kind 与结果元数据。成功取景保留 current 到 Invalidate/新 Begin，
        // 其余收尾清 current 以阻断晚到写入；槽位回到空闲，下一次 Begin 覆盖最近身份。
        var keepCurrent = _ownerKind == CaptureSessionKind.Overlay
            && _state == CaptureSessionState.Completed;
        ReleaseActivityLocked(keepCurrent);

        logs.Add($"Capture: session {text} finished reason={reason}");
        if (stateLog is not null)
            logs.Add(stateLog);
        if (resultLog is not null)
            logs.Add(resultLog);

        return snapshot;
    }

    private bool BeginShutdownLocked(List<string> logs)
    {
        if (_shutdown)
        {
            logs.Add("Capture: shutdown already sealed");
            return false;
        }

        // 封口与是否有活动会话无关：空槽、刚结束的槽、活动槽都必须永久拒绝新开槽/新预留。
        _shutdown = true;

        var clearedReservation = _reservationId;
        _reservationId = Guid.Empty;

        var invalidated = Guid.Empty;
        if (_currentSessionId != Guid.Empty && !_commitLatched)
        {
            invalidated = _currentSessionId;
            _currentSessionId = Guid.Empty;
            // 只对仍在活动的会话补 Canceled；已结束的最近身份只失效 current，终态字段保持不变。
            if (_active && _ownerSessionId == invalidated && CaptureRules.IsActive(_state))
                _failure = CaptureFailure.Canceled;
            logs.Add($"Capture: session {SessionText(invalidated)} invalidated");
        }

        logs.Add($"Capture: shutdown sealed (owner={SessionText(_ownerSessionId)}, latched={_commitLatched}, reservation={SessionText(clearedReservation)}, invalidated={SessionText(invalidated)})");
        return true;
    }

    private void ReleaseActivityLocked(bool keepCurrentIdentity)
    {
        _active = false;
        _state = CaptureSessionState.Idle;
        _commitLatched = false;
        _commitLease = CaptureCommitLease.None;

        if (!keepCurrentIdentity)
            _currentSessionId = Guid.Empty;

        // 最近 owner/kind 与结果元数据保留到下一次 Begin 覆盖，供结束后的身份判定与结果查询。
    }

    private CaptureSessionSnapshot BuildSnapshotLocked(Guid sessionId)
        => new(
            sessionId,
            _currentSessionId,
            _ownerSessionId,
            _ownerKind,
            _state,
            _origin,
            _engine,
            _failure,
            _printWindowTimedOut,
            _detail,
            _commitLatched,
            _commitLease,
            _reservationId,
            _reservationGeneration,
            _shutdown,
            _slotGeneration,
            _active,
            sessionId != Guid.Empty && sessionId == _currentSessionId,
            sessionId == Guid.Empty || sessionId == _ownerSessionId);

    private CaptureResult BuildResultLocked()
        => new(
            _ownerSessionId,
            _origin,
            _engine,
            _failure,
            _printWindowTimedOut,
            0,
            _detail);

    private static string AppendCommitFailureDetail(string? detail, CaptureFailure failure)
    {
        var token = failure == CaptureFailure.ClipboardFailed ? "clipboard-failed" : "history-failed";
        if (string.IsNullOrEmpty(detail))
            return token;
        if (detail.Contains(token, StringComparison.Ordinal))
            return detail;
        return detail + ";" + token;
    }

    private static string SessionText(Guid sessionId)
        => sessionId == Guid.Empty ? "<none>" : sessionId.ToString("N");

    private static CaptureEndReason ParseEndReason(string reason)
    {
        return reason switch
        {
            "cancel" => CaptureEndReason.Cancel,
            "confirm-success" => CaptureEndReason.ConfirmSuccess,
            "confirm-end" => CaptureEndReason.ConfirmEnd,
            "ocr" => CaptureEndReason.Ocr,
            "save" => CaptureEndReason.Save,
            "start-failed" => CaptureEndReason.StartFailed,
            "quick-success" => CaptureEndReason.QuickSuccess,
            "quick-failed" => CaptureEndReason.QuickFailed,
            _ => CaptureEndReason.None,
        };
    }

    private static CaptureSessionState TerminalStateFor(CaptureEndReason reason)
    {
        return reason switch
        {
            CaptureEndReason.Cancel => CaptureSessionState.Canceled,
            CaptureEndReason.ConfirmSuccess
                or CaptureEndReason.Ocr
                or CaptureEndReason.Save
                or CaptureEndReason.QuickSuccess => CaptureSessionState.Completed,
            _ => CaptureSessionState.Failed,
        };
    }

    private static string FormatResultLog(CaptureResult result, CaptureSessionState state, string reason)
    {
        var notice = string.IsNullOrWhiteSpace(result.Notice)
            ? "-"
            : result.Notice.Replace('\r', ' ').Replace('\n', ' ');
        return $"Capture: session {SessionText(result.SessionId)} result state={state} origin={result.Origin} engine={result.Engine} failure={result.Failure} timedOut={result.PrintWindowTimedOut} success={result.IsSuccess} canceled={result.IsCanceled} history={result.HistoryMode} notice=\"{notice}\" reason={reason}";
    }
}
