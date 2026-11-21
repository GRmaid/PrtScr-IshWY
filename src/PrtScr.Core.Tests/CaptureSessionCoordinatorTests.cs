using System.Collections.Concurrent;

namespace PrtScr.Core.Tests;

public sealed class CaptureSessionCoordinatorTests
{
    private static (CaptureSessionCoordinator Coordinator, List<string> Log) Create()
    {
        var log = new List<string>();
        return (new CaptureSessionCoordinator(log.Add), log);
    }

    private static (CaptureSessionCoordinator Coordinator, ConcurrentQueue<string> Log) CreateConcurrent()
    {
        var log = new ConcurrentQueue<string>();
        return (new CaptureSessionCoordinator(log.Enqueue), log);
    }

    private static void DriveToCommitting(CaptureSessionCoordinator coordinator, Guid sessionId)
    {
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Selecting, "test-prepared"));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Capturing, "test-attempt"));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Committing, "test-success"));
    }

    private static CaptureCommitLease Latch(
        CaptureSessionCoordinator coordinator,
        Guid sessionId)
    {
        Assert.Equal(CaptureArbitration.Granted, coordinator.TryLatchCommit(sessionId, out var lease));
        return lease;
    }

    [Fact]
    public void Begin_Quick_OnEmptySlot_OpensPreparingSession()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();

        var snapshot = coordinator.Begin(sessionId, CaptureSessionKind.Quick);

        Assert.NotNull(snapshot);
        var value = snapshot!;
        Assert.Equal(CaptureSessionKind.Quick, value.OwnerKind);
        Assert.Equal(CaptureSessionState.Preparing, value.State);
        Assert.Equal(sessionId, value.CurrentSessionId);
        Assert.Equal(sessionId, value.OwnerSessionId);
        Assert.True(value.IsCurrent);
        Assert.True(value.IsOwner);
        Assert.False(value.CommitLatched);
        Assert.True(value.CommitLease.IsEmpty);
        Assert.Equal(Guid.Empty, value.ReservationId);
        Assert.Equal(CaptureOrigin.None, value.Origin);
        Assert.Equal(CaptureFailure.None, value.Failure);
        Assert.True(value.IsBusy);
        Assert.True(coordinator.IsBusy);
        Assert.Equal(1L, coordinator.SlotGeneration);
        Assert.Equal(new[] { $"Capture: session {sessionId:N} state Idle -> Preparing (begin)" }, log);
    }

    [Fact]
    public void Begin_Quick_WhenSessionActive_IsRefused()
    {
        var (coordinator, _) = Create();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(first, CaptureSessionKind.Quick));

        Assert.Null(coordinator.Begin(second, CaptureSessionKind.Quick));
        Assert.Equal(first, coordinator.OwnerSessionId);
        Assert.Equal(first, coordinator.CurrentSessionId);
        Assert.Equal(1L, coordinator.SlotGeneration);
    }

    [Fact]
    public void Begin_Quick_WhenReservationHeld_IsRefused()
    {
        var (coordinator, _) = Create();
        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out _));

        Assert.Null(coordinator.Begin(Guid.NewGuid(), CaptureSessionKind.Quick));
        Assert.Equal(reservationId, coordinator.OverlayReservationId);
        Assert.Equal(Guid.Empty, coordinator.OwnerSessionId);
    }

    [Fact]
    public void Begin_Overlay_WithoutValidReservation_IsRefused()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();

        Assert.Null(coordinator.Begin(sessionId, CaptureSessionKind.Overlay));
        Assert.Null(coordinator.Begin(sessionId, CaptureSessionKind.Overlay, Guid.NewGuid()));
        Assert.False(coordinator.IsBusy);
        Assert.False(coordinator.IsCurrent(sessionId));
    }

    [Fact]
    public void Begin_Overlay_ConsumesReservation_AndCannotReuseIt()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out _));

        var snapshot = coordinator.Begin(sessionId, CaptureSessionKind.Overlay, reservationId);

        Assert.NotNull(snapshot);
        Assert.Equal(CaptureSessionKind.Overlay, snapshot!.OwnerKind);
        Assert.Equal(Guid.Empty, coordinator.OverlayReservationId);
        Assert.True(coordinator.IsBusy);
        Assert.Null(coordinator.Begin(sessionId, CaptureSessionKind.Overlay, reservationId));
        Assert.Equal(sessionId, coordinator.OwnerSessionId);
    }

    [Fact]
    public void Begin_Overlay_WithWrongReservation_KeepsCurrentReservation()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out _));

        Assert.Null(coordinator.Begin(sessionId, CaptureSessionKind.Overlay, Guid.NewGuid()));
        Assert.Equal(reservationId, coordinator.OverlayReservationId);
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Overlay, reservationId));
    }

    [Fact]
    public void Begin_RejectsEmptySessionIdAndNoneKind()
    {
        var (coordinator, _) = Create();

        Assert.Null(coordinator.Begin(Guid.Empty, CaptureSessionKind.Quick));
        Assert.Null(coordinator.Begin(Guid.NewGuid(), CaptureSessionKind.None));
        Assert.False(coordinator.IsBusy);
        Assert.Equal(CaptureSessionState.Idle, coordinator.State);
    }

    [Fact]
    public void Begin_AfterShutdown_IsRefused()
    {
        var (coordinator, _) = Create();
        Assert.True(coordinator.BeginShutdown());

        Assert.Null(coordinator.Begin(Guid.NewGuid(), CaptureSessionKind.Quick));
        Assert.False(coordinator.TryReserveOverlay(out _, out _));
    }

    [Fact]
    public void Reserve_OnEmptySlot_MakesCoordinatorBusy()
    {
        var (coordinator, _) = Create();

        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out var preemptedQuickId));

        Assert.NotEqual(Guid.Empty, reservationId);
        Assert.Equal(Guid.Empty, preemptedQuickId);
        Assert.Equal(reservationId, coordinator.OverlayReservationId);
        Assert.Equal(1L, coordinator.OverlayReservationGeneration);
        Assert.True(coordinator.IsBusy);
        Assert.Equal(CaptureSessionState.Idle, coordinator.State);
        Assert.Equal(Guid.Empty, coordinator.OwnerSessionId);
    }

    [Fact]
    public void Reserve_WhenAlreadyReserved_IsRefused()
    {
        var (coordinator, _) = Create();
        Assert.True(coordinator.TryReserveOverlay(out var first, out _));

        Assert.False(coordinator.TryReserveOverlay(out var second, out _));

        Assert.Equal(Guid.Empty, second);
        Assert.Equal(first, coordinator.OverlayReservationId);
        Assert.Equal(1L, coordinator.OverlayReservationGeneration);
    }

    [Fact]
    public void Reserve_WhenOverlayActive_IsRefused()
    {
        var (coordinator, _) = Create();
        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out _));
        Assert.NotNull(coordinator.Begin(Guid.NewGuid(), CaptureSessionKind.Overlay, reservationId));

        Assert.False(coordinator.TryReserveOverlay(out var second, out var preempted));

        Assert.Equal(Guid.Empty, second);
        Assert.Equal(Guid.Empty, preempted);
        Assert.Equal(CaptureSessionKind.Overlay, coordinator.OwnerKind);
    }

    [Fact]
    public void Reserve_PreemptsUnlatchedQuick_Atomically()
    {
        var (coordinator, log) = Create();
        var quickId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(quickId, CaptureSessionKind.Quick));

        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out var preemptedQuickId));

        var text = quickId.ToString("N");
        Assert.Equal(quickId, preemptedQuickId);
        Assert.Equal(quickId, coordinator.OwnerSessionId);
        Assert.Equal(Guid.Empty, coordinator.CurrentSessionId);
        Assert.Equal(CaptureSessionState.Idle, coordinator.State);
        Assert.False(coordinator.Snapshot().Active);
        Assert.Equal(CaptureFailure.Canceled, coordinator.Snapshot(quickId).Failure);
        Assert.Equal(reservationId, coordinator.OverlayReservationId);
        Assert.True(coordinator.IsBusy);
        Assert.Contains(log, line => line == $"Capture: session {text} invalidated");
        Assert.Contains(log, line => line == $"Capture: session {text} finished reason=cancel");
        Assert.Contains(log, line => line == $"Capture: session {text} state Preparing -> Canceled (terminate:cancel)");
        Assert.Contains(log, line => line == $"Capture: session {text} result state=Canceled origin=None engine=None failure=Canceled timedOut=False success=False canceled=True history=Normal notice=\"-\" reason=cancel");
    }

    [Fact]
    public void Reserve_PreemptedQuick_LateEndDoesNotClearReservation()
    {
        var (coordinator, _) = Create();
        var quickId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(quickId, CaptureSessionKind.Quick));
        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out _));

        Assert.Null(coordinator.End(quickId, "cancel"));
        Assert.Null(coordinator.Begin(Guid.NewGuid(), CaptureSessionKind.Quick));
        Assert.Equal(reservationId, coordinator.OverlayReservationId);
        Assert.True(coordinator.IsBusy);
    }

    [Fact]
    public void Reserve_DoesNotPreemptLatchedQuick()
    {
        var (coordinator, _) = Create();
        var quickId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(quickId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, quickId);
        _ = Latch(coordinator, quickId);

        Assert.False(coordinator.TryReserveOverlay(out var reservationId, out var preempted));

        Assert.Equal(Guid.Empty, reservationId);
        Assert.Equal(Guid.Empty, preempted);
        Assert.Equal(quickId, coordinator.OwnerSessionId);
        Assert.Equal(quickId, coordinator.CurrentSessionId);
        Assert.True(coordinator.IsCommitLatched);
        Assert.True(coordinator.IsBusy);
    }

    [Fact]
    public void Reserve_AfterShutdown_IsRefused()
    {
        var (coordinator, _) = Create();
        Assert.True(coordinator.BeginShutdown());

        Assert.False(coordinator.TryReserveOverlay(out var reservationId, out var preempted));

        Assert.Equal(Guid.Empty, reservationId);
        Assert.Equal(Guid.Empty, preempted);
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public void Release_WithCurrentGeneration_ClearsReservation()
    {
        var (coordinator, _) = Create();
        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out _));

        Assert.True(coordinator.ReleaseOverlayReservation(reservationId));

        Assert.Equal(Guid.Empty, coordinator.OverlayReservationId);
        Assert.False(coordinator.IsBusy);
        Assert.Equal(CaptureSessionState.Idle, coordinator.State);
    }

    [Fact]
    public void Release_StaleReservationAfterNewReservation_DoesNotClearIt()
    {
        var (coordinator, _) = Create();
        Assert.True(coordinator.TryReserveOverlay(out var first, out _));
        Assert.True(coordinator.ReleaseOverlayReservation(first));
        Assert.True(coordinator.TryReserveOverlay(out var second, out _));

        Assert.NotEqual(first, second);
        Assert.Equal(2L, coordinator.OverlayReservationGeneration);

        Assert.False(coordinator.ReleaseOverlayReservation(first));
        Assert.Equal(second, coordinator.OverlayReservationId);
        Assert.True(coordinator.IsBusy);
    }

    [Fact]
    public void Release_WithStaleGeneration_IsRefused()
    {
        var (coordinator, _) = Create();
        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out _));
        var generation = coordinator.OverlayReservationGeneration;

        Assert.False(coordinator.ReleaseOverlayReservation(reservationId, generation - 1));
        Assert.Equal(reservationId, coordinator.OverlayReservationId);

        Assert.True(coordinator.ReleaseOverlayReservation(reservationId, generation));
        Assert.Equal(Guid.Empty, coordinator.OverlayReservationId);
    }

    [Fact]
    public void Release_EmptyReservationId_IsRefused()
    {
        var (coordinator, _) = Create();

        Assert.False(coordinator.ReleaseOverlayReservation(Guid.Empty));
    }

    [Fact]
    public void Release_AfterShutdown_DoesNotReopenSealedEntries()
    {
        var (coordinator, _) = Create();
        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out _));
        Assert.True(coordinator.BeginShutdown());

        Assert.Equal(Guid.Empty, coordinator.OverlayReservationId);
        Assert.False(coordinator.ReleaseOverlayReservation(reservationId));
        Assert.False(coordinator.TryReserveOverlay(out _, out _));
        Assert.Null(coordinator.Begin(Guid.NewGuid(), CaptureSessionKind.Quick));
        Assert.True(coordinator.IsShuttingDown);
    }

    [Fact]
    public void Latch_RequiresCurrentOwnerInCommitting()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));

        Assert.Equal(CaptureArbitration.Denied, coordinator.TryLatchCommit(sessionId, out var earlyLease));
        Assert.True(earlyLease.IsEmpty);
        Assert.Equal(CaptureArbitration.Denied, coordinator.TryLatchCommit(Guid.NewGuid(), out _));
        Assert.Equal(CaptureArbitration.Denied, coordinator.TryLatchCommit(Guid.Empty, out _));

        DriveToCommitting(coordinator, sessionId);

        Assert.Equal(CaptureArbitration.Granted, coordinator.TryLatchCommit(sessionId, out var lease));
        Assert.False(lease.IsEmpty);
        Assert.Equal(sessionId, lease.SessionId);
        Assert.Equal(CaptureSessionKind.Quick, lease.Kind);
        Assert.Equal(1L, lease.SlotGeneration);
        Assert.True(coordinator.IsCommitLatched);
        Assert.Equal(lease, coordinator.Snapshot(sessionId).CommitLease);
    }

    [Fact]
    public void Latch_SecondCall_IsAlreadyLatched()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, sessionId);
        var first = Latch(coordinator, sessionId);

        Assert.Equal(CaptureArbitration.AlreadyLatched, coordinator.TryLatchCommit(sessionId, out var second));

        Assert.Equal(first, second);
        Assert.True(coordinator.IsCommitLatched);
    }

    [Fact]
    public void Latch_AfterShutdown_IsDenied()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, sessionId);

        Assert.True(coordinator.BeginShutdown());

        Assert.False(coordinator.IsCurrent(sessionId));
        Assert.Equal(CaptureArbitration.Denied, coordinator.TryLatchCommit(sessionId, out _));
        Assert.False(coordinator.IsCommitLatched);
    }

    [Fact]
    public void Invalidate_AfterLatch_IsRefused()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, sessionId);
        _ = Latch(coordinator, sessionId);

        Assert.False(coordinator.Invalidate(sessionId));

        Assert.True(coordinator.IsCurrent(sessionId));
        Assert.True(coordinator.IsCommitLatched);
        Assert.Equal(CaptureFailure.None, coordinator.Snapshot(sessionId).Failure);
        Assert.Contains(log, line => line == $"Capture: session {sessionId:N} invalidate ignored (commit latched)");
    }

    [Fact]
    public void Invalidate_NonCurrentSession_IsSilent()
    {
        var (coordinator, log) = Create();
        Assert.NotNull(coordinator.Begin(Guid.NewGuid(), CaptureSessionKind.Quick));
        var count = log.Count;

        Assert.False(coordinator.Invalidate(Guid.NewGuid()));

        Assert.Equal(count, log.Count);
    }

    [Fact]
    public void Invalidate_ThenEnd_StillWritesTerminalResult()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));

        Assert.True(coordinator.Invalidate(sessionId));
        Assert.False(coordinator.IsCurrent(sessionId));
        Assert.Equal(sessionId, coordinator.OwnerSessionId);

        var snapshot = coordinator.End(sessionId, "cancel");

        Assert.NotNull(snapshot);
        var value = snapshot!;
        Assert.Equal(CaptureSessionState.Canceled, value.State);
        Assert.Equal(CaptureFailure.Canceled, value.Failure);
        Assert.True(value.Result.IsCanceled);
        Assert.Null(value.Result.Notice);
        Assert.False(coordinator.IsBusy);
        Assert.Equal(CaptureSessionState.Idle, coordinator.State);
    }

    [Fact]
    public void End_WritesFinishedStateResultInLegacyOrder()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        var text = sessionId.ToString("N");

        var snapshot = coordinator.End(sessionId, "quick-success");

        Assert.NotNull(snapshot);
        Assert.Equal(CaptureSessionState.Completed, snapshot!.State);
        Assert.Equal(
            new[]
            {
                $"Capture: session {text} state Idle -> Preparing (begin)",
                $"Capture: session {text} finished reason=quick-success",
                $"Capture: session {text} state Preparing -> Completed (terminate:quick-success)",
                $"Capture: session {text} result state=Completed origin=None engine=None failure=None timedOut=False success=True canceled=False history=Normal notice=\"-\" reason=quick-success",
            },
            log);
    }

    [Theory]
    [InlineData("quick-success", CaptureSessionState.Completed)]
    [InlineData("quick-failed", CaptureSessionState.Failed)]
    [InlineData("confirm-success", CaptureSessionState.Completed)]
    [InlineData("ocr", CaptureSessionState.Completed)]
    [InlineData("save", CaptureSessionState.Completed)]
    [InlineData("start-failed", CaptureSessionState.Failed)]
    [InlineData("confirm-end", CaptureSessionState.Failed)]
    public void End_MapsReasonToTerminalState(string reason, CaptureSessionState expected)
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));

        var snapshot = coordinator.End(sessionId, reason);

        Assert.NotNull(snapshot);
        Assert.Equal(expected, snapshot!.State);
        Assert.Equal(CaptureSessionState.Idle, coordinator.State);
        Assert.Equal(sessionId, coordinator.OwnerSessionId);
        Assert.False(coordinator.Snapshot(sessionId).Active);
    }

    [Fact]
    public void End_TypedCancelReason_MatchesStringContract()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        Assert.True(coordinator.Invalidate(sessionId));

        var snapshot = coordinator.End(sessionId, CaptureEndReason.Cancel);

        Assert.NotNull(snapshot);
        Assert.Equal(CaptureSessionState.Canceled, snapshot!.State);
        Assert.Equal(CaptureFailure.Canceled, snapshot.Failure);
    }

    [Fact]
    public void End_NotCapturing_IsIgnoredWithLegacyText()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();

        Assert.Null(coordinator.End(sessionId, "cancel"));

        Assert.Single(log);
        Assert.Equal($"Capture: session {sessionId:N} finish ignored (not capturing, reason=cancel)", log[0]);
    }

    [Fact]
    public void End_StaleSession_DoesNotClearNewSlot()
    {
        var (coordinator, log) = Create();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(first, CaptureSessionKind.Quick));
        Assert.NotNull(coordinator.End(first, "quick-success"));
        Assert.NotNull(coordinator.Begin(second, CaptureSessionKind.Quick));

        Assert.Null(coordinator.End(first, "cancel"));

        Assert.Equal(second, coordinator.CurrentSessionId);
        Assert.Equal(second, coordinator.OwnerSessionId);
        Assert.Equal(CaptureSessionState.Preparing, coordinator.State);
        Assert.Contains(
            log,
            line => line == $"Capture: session {first:N} finish ignored (stale session, current={second:N}, reason=cancel)");
    }

    [Fact]
    public void End_AfterTerminalTransition_LogsDuplicateAndReleasesSlot()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Canceled, "test-terminal"));

        var snapshot = coordinator.End(sessionId, "cancel");

        Assert.NotNull(snapshot);
        Assert.Equal(CaptureSessionState.Canceled, snapshot!.State);
        Assert.Contains(
            log,
            line => line == $"Capture: session {sessionId:N} terminate Duplicate (state=Canceled, reason=cancel)");
        Assert.Equal(CaptureSessionState.Idle, coordinator.State);
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public void End_EmptySessionId_OnActiveSlot_IsIgnoredWithoutReleasingSlot()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));

        Assert.Null(coordinator.End(Guid.Empty, "start-failed"));

        Assert.Equal(sessionId, coordinator.OwnerSessionId);
        Assert.Equal(sessionId, coordinator.CurrentSessionId);
        Assert.Equal(CaptureSessionState.Preparing, coordinator.State);
        Assert.True(coordinator.IsBusy);
        Assert.True(coordinator.IsCurrent(sessionId));
        var text = sessionId.ToString("N");
        Assert.Contains(log, line => line == $"Capture: session <none> finish ignored (stale session, current={text}, reason=start-failed)");
        Assert.DoesNotContain(log, line => line == "Capture: session <none> finished reason=start-failed");
        Assert.DoesNotContain(log, line => line.Contains("session <none> state", StringComparison.Ordinal));
    }

    [Fact]
    public void End_EmptySessionId_OnEmptySlot_LogsNotCapturingWithoutState()
    {
        var (coordinator, log) = Create();

        Assert.True(coordinator.IsOwner(Guid.Empty));
        Assert.False(coordinator.IsCurrent(Guid.Empty));
        Assert.Null(coordinator.End(Guid.Empty, "start-failed"));

        Assert.Equal(CaptureSessionState.Idle, coordinator.State);
        Assert.False(coordinator.IsBusy);
        Assert.Equal(new[] { "Capture: session <none> finish ignored (not capturing, reason=start-failed)" }, log);
        Assert.True(coordinator.Snapshot(Guid.Empty).IsOwner);
        Assert.False(coordinator.Snapshot(Guid.Empty).IsCurrent);
    }

    [Fact]
    public void Shutdown_InvalidatesUnlatchedSession_AndAllowsOwnerToEnd()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));

        Assert.True(coordinator.BeginShutdown());

        Assert.True(coordinator.IsShuttingDown);
        Assert.False(coordinator.IsCurrent(sessionId));
        Assert.Equal(sessionId, coordinator.OwnerSessionId);
        Assert.Contains(log, line => line == $"Capture: session {sessionId:N} invalidated");

        var snapshot = coordinator.End(sessionId, "cancel");

        Assert.NotNull(snapshot);
        Assert.Equal(CaptureSessionState.Canceled, snapshot!.State);
        Assert.Equal(CaptureFailure.Canceled, snapshot.Failure);
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public void Shutdown_PreservesLatchedSessionUntilEnd()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, sessionId);
        _ = Latch(coordinator, sessionId);

        Assert.True(coordinator.BeginShutdown());

        Assert.True(coordinator.IsCurrent(sessionId));
        Assert.True(coordinator.IsCommitLatched);
        Assert.True(coordinator.IsBusy);
        Assert.False(coordinator.Invalidate(sessionId));

        var snapshot = coordinator.End(sessionId, "confirm-success");

        Assert.NotNull(snapshot);
        Assert.Equal(CaptureSessionState.Completed, snapshot!.State);
        Assert.Equal(CaptureFailure.None, snapshot.Failure);
        Assert.False(coordinator.IsCommitLatched);
        Assert.False(coordinator.IsBusy);
        Assert.True(coordinator.IsShuttingDown);
    }

    [Fact]
    public void Shutdown_IsIdempotent()
    {
        var (coordinator, log) = Create();

        Assert.True(coordinator.BeginShutdown());
        Assert.False(coordinator.BeginShutdown());

        Assert.Contains(log, line => line == "Capture: shutdown already sealed");
        Assert.True(coordinator.IsShuttingDown);
    }

    [Fact]
    public void ShutdownGrace_IsSingleThreeHundredMillisecondBudget()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(300), CaptureSessionCoordinator.ShutdownGrace);
    }

    [Fact]
    public void MarkSucceeded_RecordsMetadataAndAdvancesToCommitting()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Selecting, "test-prepared"));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Capturing, "test-attempt"));

        Assert.True(coordinator.TryMarkSucceeded(sessionId, CaptureOrigin.Window, CaptureEngine.Wgc, true));

        var snapshot = coordinator.Snapshot(sessionId);
        Assert.Equal(CaptureSessionState.Committing, snapshot.State);
        Assert.Equal(CaptureOrigin.Window, snapshot.Origin);
        Assert.Equal(CaptureEngine.Wgc, snapshot.Engine);
        Assert.True(snapshot.PrintWindowTimedOut);
        Assert.Equal(CaptureFailure.None, snapshot.Failure);
        Assert.Null(snapshot.Detail);
        Assert.Contains(log, line => line == $"Capture: session {sessionId:N} state Capturing -> Committing (attempt-success)");
        Assert.Contains(log, line => line == $"Capture: session {sessionId:N} result state=Committing origin=Window engine=Wgc failure=None timedOut=True success=True canceled=False history=Window notice=\"-\" reason=attempt-success");
    }

    [Fact]
    public void MarkFailed_RecordsFailureThenRestoresSelecting()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Selecting, "test-prepared"));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Capturing, "test-attempt"));

        Assert.True(coordinator.TryMarkFailed(sessionId, CaptureFailure.Busy, "busy"));

        var snapshot = coordinator.Snapshot(sessionId);
        Assert.Equal(CaptureSessionState.Selecting, snapshot.State);
        Assert.Equal(CaptureFailure.Busy, snapshot.Failure);
        Assert.Equal("busy", snapshot.Detail);
        var resultIndex = log.FindIndex(line => line.Contains(" result state=Capturing", StringComparison.Ordinal));
        var transitionIndex = log.FindIndex(line => line.EndsWith("state Capturing -> Selecting (attempt-failed:Busy)", StringComparison.Ordinal));
        Assert.True(resultIndex >= 0);
        Assert.True(transitionIndex > resultIndex);
    }

    [Fact]
    public void MarkSucceeded_LateSession_DoesNotTouchNewSlot()
    {
        var (coordinator, _) = Create();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(first, CaptureSessionKind.Quick));
        Assert.NotNull(coordinator.End(first, "quick-success"));
        Assert.NotNull(coordinator.Begin(second, CaptureSessionKind.Quick));

        Assert.False(coordinator.TryMarkSucceeded(first, CaptureOrigin.Window, CaptureEngine.Wgc));
        Assert.False(coordinator.TryMarkFailed(first, CaptureFailure.Timeout, "late"));

        Assert.Equal(CaptureOrigin.None, coordinator.Snapshot(second).Origin);
        Assert.Equal(CaptureFailure.None, coordinator.Snapshot(second).Failure);
    }

    [Fact]
    public void Mark_AfterLatch_IsIgnored()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, sessionId);
        _ = Latch(coordinator, sessionId);

        Assert.False(coordinator.TryMarkFailed(sessionId, CaptureFailure.Timeout, "late"));
        Assert.False(coordinator.TryMarkSucceeded(sessionId, CaptureOrigin.Normal, CaptureEngine.BitBlt));

        Assert.Equal(CaptureFailure.None, coordinator.Snapshot(sessionId).Failure);
        Assert.Contains(log, line => line.EndsWith("attempt-success ignored (commit latched)", StringComparison.Ordinal));
    }

    [Fact]
    public void Transition_IllegalTarget_IsRejectedAndLogged()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));

        Assert.False(coordinator.TryTransition(sessionId, CaptureSessionState.Committing, "test-illegal"));

        Assert.Equal(CaptureSessionState.Preparing, coordinator.State);
        Assert.Contains(log, line => line == $"Capture: session {sessionId:N} state Preparing -> Committing rejected (test-illegal)");
    }

    [Fact]
    public void NoteCommitFailure_RejectsNoneAndUnrelatedFailures()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, sessionId);
        _ = Latch(coordinator, sessionId);

        Assert.False(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.None));
        Assert.False(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.Timeout));
        Assert.False(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.Canceled));

        var snapshot = coordinator.Snapshot(sessionId);
        Assert.Equal(CaptureFailure.None, snapshot.Failure);
        Assert.Null(snapshot.Detail);
    }

    [Fact]
    public void NoteCommitFailure_RequiresLatchAndOwnership()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, sessionId);

        Assert.False(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.ClipboardFailed));

        _ = Latch(coordinator, sessionId);
        Assert.False(coordinator.TryNoteCommitFailure(Guid.NewGuid(), CaptureFailure.ClipboardFailed));
        Assert.False(coordinator.TryNoteCommitFailure(Guid.Empty, CaptureFailure.ClipboardFailed));
        Assert.Equal(CaptureFailure.None, coordinator.Snapshot(sessionId).Failure);
    }

    [Fact]
    public void NoteCommitFailure_FirstFailureIsPrimary_SecondKeptInDetail()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, sessionId);
        _ = Latch(coordinator, sessionId);

        Assert.True(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.ClipboardFailed));
        Assert.True(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.HistoryFailed));

        var snapshot = coordinator.Snapshot(sessionId);
        Assert.Equal(CaptureFailure.ClipboardFailed, snapshot.Failure);
        Assert.Equal("clipboard-failed;history-failed", snapshot.Detail);
    }

    [Fact]
    public void NoteCommitFailure_ReverseOrder_KeepsHistoryAsPrimary()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, sessionId);
        _ = Latch(coordinator, sessionId);

        Assert.True(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.HistoryFailed));
        Assert.True(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.ClipboardFailed));

        var snapshot = coordinator.Snapshot(sessionId);
        Assert.Equal(CaptureFailure.HistoryFailed, snapshot.Failure);
        Assert.Equal("history-failed;clipboard-failed", snapshot.Detail);
    }

    [Fact]
    public void NoteCommitFailure_EmitsNoIntermediateResultLog()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, sessionId);
        _ = Latch(coordinator, sessionId);

        Assert.True(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.ClipboardFailed));
        Assert.True(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.HistoryFailed));

        Assert.DoesNotContain(log, line => line.Contains(" result state=Committing", StringComparison.Ordinal));
    }

    [Fact]
    public void CompletedImage_WithBothAttachmentFailures_KeepsFailureAndDetail()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, sessionId);
        _ = Latch(coordinator, sessionId);
        Assert.True(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.ClipboardFailed));
        Assert.True(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.HistoryFailed));

        var snapshot = coordinator.End(sessionId, "confirm-success");

        Assert.NotNull(snapshot);
        var value = snapshot!;
        Assert.Equal(CaptureSessionState.Completed, value.State);
        Assert.Equal(CaptureFailure.ClipboardFailed, value.Failure);
        Assert.Equal("clipboard-failed;history-failed", value.Detail);
        Assert.False(value.Result.IsSuccess);
        Assert.Equal("复制失败：剪贴板被占用，请稍后重试", value.Result.Notice);
        Assert.Contains(log, line => line.StartsWith($"Capture: session {sessionId:N} result state=Completed", StringComparison.Ordinal));
    }

    [Fact]
    public void Decide_IsReadOnlyHintOnly()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        var before = coordinator.Snapshot(sessionId);
        var logCount = log.Count;

        Assert.Equal(CaptureArbitration.Busy, coordinator.Decide(CaptureIntent.StartQuick));
        Assert.Equal(CaptureArbitration.ReservationRequired, coordinator.Decide(CaptureIntent.StartOverlay));
        Assert.Equal(CaptureArbitration.Denied, coordinator.Decide(CaptureIntent.LatchCommit, sessionId));
        Assert.Equal(CaptureArbitration.Granted, coordinator.Decide(CaptureIntent.Shutdown));
        Assert.Equal(CaptureArbitration.None, coordinator.Decide(CaptureIntent.None));

        Assert.Equal(before, coordinator.Snapshot(sessionId));
        Assert.Equal(logCount, log.Count);
    }

    [Fact]
    public void Decide_TracksReservationPreemptionAndShutdown()
    {
        var (coordinator, _) = Create();
        var quickId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(quickId, CaptureSessionKind.Quick));

        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out var preemptedQuickId));

        Assert.Equal(quickId, preemptedQuickId);
        Assert.Equal(CaptureArbitration.Granted, coordinator.Decide(CaptureIntent.StartOverlay, Guid.Empty, reservationId));
        Assert.Equal(CaptureArbitration.ReservationConflict, coordinator.Decide(CaptureIntent.StartOverlay, Guid.Empty, Guid.NewGuid()));
        Assert.Equal(CaptureArbitration.Busy, coordinator.Decide(CaptureIntent.StartQuick));

        Assert.True(coordinator.BeginShutdown());

        Assert.Equal(CaptureArbitration.ShuttingDown, coordinator.Decide(CaptureIntent.StartOverlay));
        Assert.Equal(CaptureArbitration.ShuttingDown, coordinator.Decide(CaptureIntent.StartQuick));
        Assert.Equal(CaptureArbitration.ShuttingDown, coordinator.Decide(CaptureIntent.Shutdown));
    }

    [Fact]
    public void Snapshot_GuidEmptyKeepsLegacyOwnerSemantics()
    {
        var (coordinator, _) = Create();

        Assert.True(coordinator.IsOwner(Guid.Empty));
        Assert.False(coordinator.IsCurrent(Guid.Empty));
        Assert.False(coordinator.Invalidate(Guid.Empty));
        Assert.True(coordinator.Snapshot(Guid.Empty).IsOwner);
        Assert.False(coordinator.Snapshot(Guid.Empty).IsCurrent);
    }

    [Fact]
    public async Task ReserveAndQuickEntryRace_NeverLeavesSlotAndReservationTogether()
    {
        for (var round = 0; round < 32; round++)
        {
            var (coordinator, _) = CreateConcurrent();
            var quickId = Guid.NewGuid();
            var reserveSucceeded = false;
            var quickBegan = false;
            using var barrier = new Barrier(2);

            var reserveTask = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    reserveSucceeded = coordinator.TryReserveOverlay(out _, out _);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            var quickTask = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    quickBegan = coordinator.Begin(quickId, CaptureSessionKind.Quick) is not null;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            await Task.WhenAll(reserveTask, quickTask);

            Assert.True(reserveSucceeded);
            Assert.NotEqual(Guid.Empty, coordinator.OverlayReservationId);
            Assert.False(coordinator.Snapshot().Active);
            Assert.Equal(CaptureSessionState.Idle, coordinator.State);
            if (quickBegan)
            {
                // Quick 若先拿到槽，预留必须在同一同步边界内让它失效并收尾；最近 owner 保留但已不活动。
                Assert.Equal(quickId, coordinator.OwnerSessionId);
                Assert.False(coordinator.IsCurrent(quickId));
            }
            else
            {
                Assert.Equal(Guid.Empty, coordinator.OwnerSessionId);
            }
            Assert.True(coordinator.IsBusy);
        }
    }

    [Fact]
    public void End_OverlaySuccess_KeepsRecentIdentityAndMetadata()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out _));
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Overlay, reservationId));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Selecting, "test-prepared"));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Capturing, "test-attempt"));
        Assert.True(coordinator.TryMarkSucceeded(sessionId, CaptureOrigin.Window, CaptureEngine.Wgc));
        _ = Latch(coordinator, sessionId);

        var snapshot = coordinator.End(sessionId, "confirm-success");

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Active);
        Assert.Equal(CaptureSessionState.Completed, snapshot.State);
        Assert.False(coordinator.IsBusy);
        Assert.Equal(CaptureSessionState.Idle, coordinator.State);
        Assert.True(coordinator.IsCurrent(sessionId));
        Assert.True(coordinator.IsOwner(sessionId));
        Assert.False(coordinator.IsCommitLatched);
        Assert.Equal(CaptureArbitration.Granted, coordinator.Decide(CaptureIntent.StartQuick));

        var recent = coordinator.Snapshot(sessionId);
        Assert.False(recent.Active);
        Assert.False(recent.IsBusy);
        Assert.Equal(CaptureSessionState.Idle, recent.State);
        Assert.Equal(CaptureOrigin.Window, recent.Origin);
        Assert.Equal(CaptureEngine.Wgc, recent.Engine);
        Assert.Equal(CaptureFailure.None, recent.Failure);
        Assert.True(recent.IsCurrent);
        Assert.True(recent.IsOwner);

        Assert.True(coordinator.Invalidate(sessionId));
        Assert.False(coordinator.IsCurrent(sessionId));
        Assert.True(coordinator.IsOwner(sessionId));
        Assert.Equal(CaptureFailure.None, coordinator.Snapshot(sessionId).Failure);
        Assert.Equal(CaptureOrigin.Window, coordinator.Snapshot(sessionId).Origin);
        Assert.Equal(CaptureSessionState.Idle, coordinator.Snapshot(sessionId).State);
    }

    [Fact]
    public void End_QuickSuccess_ClearsCurrentButKeepsRecentOwnerAndMetadata()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));

        var snapshot = coordinator.End(sessionId, "quick-success");

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Active);
        Assert.Equal(CaptureSessionState.Completed, snapshot.State);
        Assert.Equal(CaptureSessionState.Idle, coordinator.State);
        Assert.False(coordinator.IsBusy);
        Assert.False(coordinator.IsCurrent(sessionId));
        Assert.True(coordinator.IsOwner(sessionId));
        Assert.Equal(sessionId, coordinator.OwnerSessionId);
        Assert.Equal(CaptureSessionKind.Quick, coordinator.OwnerKind);
        var recent = coordinator.Snapshot(sessionId);
        Assert.False(recent.Active);
        Assert.Equal(CaptureSessionState.Idle, recent.State);
        Assert.Equal(CaptureFailure.None, recent.Failure);
        Assert.Equal(CaptureArbitration.Granted, coordinator.Decide(CaptureIntent.StartQuick));
    }

    [Fact]
    public void End_CompletedOverlay_NextGenerationReplacesIdentityAndOldCallsStayStale()
    {
        var (coordinator, _) = Create();
        var overlayId = Guid.NewGuid();
        Assert.True(coordinator.TryReserveOverlay(out var oldReservation, out _));
        Assert.NotNull(coordinator.Begin(overlayId, CaptureSessionKind.Overlay, oldReservation));
        Assert.True(coordinator.TryTransition(overlayId, CaptureSessionState.Selecting, "test-prepared"));
        Assert.True(coordinator.TryTransition(overlayId, CaptureSessionState.Capturing, "test-attempt"));
        Assert.True(coordinator.TryMarkSucceeded(overlayId, CaptureOrigin.Normal, CaptureEngine.BitBlt));
        _ = Latch(coordinator, overlayId);
        Assert.NotNull(coordinator.End(overlayId, "confirm-success"));
        var firstGeneration = coordinator.SlotGeneration;

        var quickId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(quickId, CaptureSessionKind.Quick));

        Assert.Equal(firstGeneration + 1, coordinator.SlotGeneration);
        Assert.Equal(quickId, coordinator.OwnerSessionId);
        Assert.Equal(quickId, coordinator.CurrentSessionId);
        Assert.False(coordinator.IsCurrent(overlayId));
        Assert.False(coordinator.IsOwner(overlayId));
        Assert.Equal(CaptureOrigin.None, coordinator.Snapshot(quickId).Origin);
        Assert.Equal(CaptureFailure.None, coordinator.Snapshot(quickId).Failure);
        Assert.True(coordinator.IsBusy);

        Assert.Null(coordinator.End(overlayId, "cancel"));
        Assert.Equal(quickId, coordinator.OwnerSessionId);
        Assert.Equal(CaptureSessionState.Preparing, coordinator.State);

        Assert.False(coordinator.ReleaseOverlayReservation(oldReservation));
        Assert.Equal(quickId, coordinator.OwnerSessionId);
        Assert.Equal(quickId, coordinator.CurrentSessionId);

        Assert.NotNull(coordinator.End(quickId, "quick-success"));
        Assert.True(coordinator.TryReserveOverlay(out var newReservation, out var preempted));
        Assert.Equal(Guid.Empty, preempted);
        Assert.NotEqual(oldReservation, newReservation);
        Assert.False(coordinator.ReleaseOverlayReservation(oldReservation));
        Assert.Equal(newReservation, coordinator.OverlayReservationId);

        var nextOverlay = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(nextOverlay, CaptureSessionKind.Overlay, newReservation));
        Assert.Equal(nextOverlay, coordinator.CurrentSessionId);
        Assert.Equal(nextOverlay, coordinator.OwnerSessionId);
        Assert.False(coordinator.IsOwner(overlayId));
        Assert.False(coordinator.IsOwner(quickId));
        Assert.True(coordinator.IsBusy);
    }

    [Fact]
    public void AfterEnd_LateMutationsAreRejectedAndSecondEndDoesNotRewriteResult()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out _));
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Overlay, reservationId));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Selecting, "test-prepared"));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Capturing, "test-attempt"));
        Assert.True(coordinator.TryMarkSucceeded(sessionId, CaptureOrigin.Window, CaptureEngine.Wgc));
        _ = Latch(coordinator, sessionId);
        Assert.NotNull(coordinator.End(sessionId, "confirm-success"));
        var logCount = log.Count;

        Assert.False(coordinator.TryMarkSucceeded(sessionId, CaptureOrigin.Normal, CaptureEngine.BitBlt));
        Assert.False(coordinator.TryMarkFailed(sessionId, CaptureFailure.Timeout, "late"));
        Assert.False(coordinator.TryTransition(sessionId, CaptureSessionState.Selecting, "late"));
        Assert.Equal(CaptureArbitration.Denied, coordinator.TryLatchCommit(sessionId, out var lateLease));
        Assert.True(lateLease.IsEmpty);
        Assert.False(coordinator.TryNoteCommitFailure(sessionId, CaptureFailure.ClipboardFailed));

        var recent = coordinator.Snapshot(sessionId);
        Assert.Equal(CaptureFailure.None, recent.Failure);
        Assert.Null(recent.Detail);
        Assert.Equal(CaptureOrigin.Window, recent.Origin);
        Assert.Equal(CaptureEngine.Wgc, recent.Engine);
        Assert.False(recent.Active);

        Assert.Null(coordinator.End(sessionId, "cancel"));

        var appended = log.Skip(logCount).ToArray();
        Assert.Contains(appended, line => line.EndsWith("finish ignored (not capturing, reason=cancel)", StringComparison.Ordinal));
        Assert.DoesNotContain(appended, line => line.Contains(" finished reason=", StringComparison.Ordinal));
        Assert.DoesNotContain(appended, line => line.Contains(" (terminate:", StringComparison.Ordinal));
        Assert.DoesNotContain(appended, line => line.Contains(" result state=", StringComparison.Ordinal));
        Assert.Single(log, line => line.Contains(" finished reason=confirm-success", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidatedSession_LateMarksCannotOverwriteCanceled()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        Assert.True(coordinator.Invalidate(sessionId));
        Assert.False(coordinator.IsCurrent(sessionId));

        Assert.False(coordinator.TryMarkSucceeded(sessionId, CaptureOrigin.Window, CaptureEngine.Wgc));
        Assert.False(coordinator.TryMarkFailed(sessionId, CaptureFailure.Timeout, "late"));
        Assert.False(coordinator.TryTransition(sessionId, CaptureSessionState.Selecting, "late"));

        var snapshot = coordinator.Snapshot(sessionId);
        Assert.Equal(CaptureFailure.Canceled, snapshot.Failure);
        Assert.Equal(CaptureSessionState.Preparing, snapshot.State);
        Assert.Equal(CaptureOrigin.None, snapshot.Origin);

        var end = coordinator.End(sessionId, "cancel");
        Assert.NotNull(end);
        Assert.Equal(CaptureSessionState.Canceled, end!.State);
        Assert.Equal(CaptureFailure.Canceled, end.Failure);
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public void Reserve_PreemptsQuickInCapturing_Atomically()
    {
        var (coordinator, _) = Create();
        var quickId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(quickId, CaptureSessionKind.Quick));
        Assert.True(coordinator.TryTransition(quickId, CaptureSessionState.Selecting, "test-prepared"));
        Assert.True(coordinator.TryTransition(quickId, CaptureSessionState.Capturing, "test-attempt"));

        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out var preemptedQuickId));

        Assert.Equal(quickId, preemptedQuickId);
        Assert.Equal(quickId, coordinator.OwnerSessionId);
        Assert.False(coordinator.Snapshot().Active);
        Assert.False(coordinator.IsCurrent(quickId));
        Assert.Equal(CaptureSessionState.Idle, coordinator.State);
        Assert.Equal(CaptureFailure.Canceled, coordinator.Snapshot(quickId).Failure);
        Assert.Equal(reservationId, coordinator.OverlayReservationId);
        Assert.Null(coordinator.End(quickId, "cancel"));
    }

    [Fact]
    public void Reserve_PreemptsMarkSucceededUnlatchedCommittingQuick()
    {
        var (coordinator, _) = Create();
        var quickId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(quickId, CaptureSessionKind.Quick));
        Assert.True(coordinator.TryTransition(quickId, CaptureSessionState.Selecting, "test-prepared"));
        Assert.True(coordinator.TryTransition(quickId, CaptureSessionState.Capturing, "test-attempt"));
        Assert.True(coordinator.TryMarkSucceeded(quickId, CaptureOrigin.Window, CaptureEngine.Wgc));
        Assert.Equal(CaptureSessionState.Committing, coordinator.State);

        Assert.True(coordinator.TryReserveOverlay(out _, out var preemptedQuickId));

        Assert.Equal(quickId, preemptedQuickId);
        Assert.False(coordinator.Snapshot().Active);
        Assert.False(coordinator.IsCommitLatched);
        Assert.False(coordinator.IsCurrent(quickId));
        Assert.Equal(CaptureFailure.Canceled, coordinator.Snapshot(quickId).Failure);
        Assert.Null(coordinator.End(quickId, "cancel"));
    }

    [Fact]
    public void Reserve_AfterQuickEnd_DoesNotTreatRecentOwnerAsActive()
    {
        var (coordinator, _) = Create();
        var quickId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(quickId, CaptureSessionKind.Quick));
        Assert.NotNull(coordinator.End(quickId, "quick-success"));
        Assert.True(coordinator.IsOwner(quickId));
        Assert.False(coordinator.IsBusy);

        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out var preemptedQuickId));

        Assert.Equal(Guid.Empty, preemptedQuickId);
        Assert.Equal(quickId, coordinator.OwnerSessionId);
        Assert.Equal(reservationId, coordinator.OverlayReservationId);
        Assert.False(coordinator.Snapshot().Active);
    }

    [Fact]
    public void Shutdown_AfterEndedSession_SealsWithoutReclassifyingMetadata()
    {
        var (coordinator, _) = Create();
        var sessionId = Guid.NewGuid();
        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out _));
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Overlay, reservationId));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Selecting, "test-prepared"));
        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Capturing, "test-attempt"));
        Assert.True(coordinator.TryMarkSucceeded(sessionId, CaptureOrigin.Normal, CaptureEngine.BitBlt));
        _ = Latch(coordinator, sessionId);
        Assert.NotNull(coordinator.End(sessionId, "confirm-success"));
        Assert.True(coordinator.IsCurrent(sessionId));

        Assert.True(coordinator.BeginShutdown());

        Assert.True(coordinator.IsShuttingDown);
        Assert.False(coordinator.IsCurrent(sessionId));
        Assert.True(coordinator.IsOwner(sessionId));
        var snapshot = coordinator.Snapshot(sessionId);
        Assert.Equal(CaptureFailure.None, snapshot.Failure);
        Assert.Equal(CaptureOrigin.Normal, snapshot.Origin);
        Assert.Equal(CaptureEngine.BitBlt, snapshot.Engine);
        Assert.Equal(CaptureSessionState.Idle, snapshot.State);
        Assert.False(snapshot.Active);
        Assert.Null(coordinator.Begin(Guid.NewGuid(), CaptureSessionKind.Quick));
        Assert.False(coordinator.TryReserveOverlay(out _, out _));
    }

    [Fact]
    public void Shutdown_OnEmptySlot_SealsAndRejectsEntry()
    {
        var (coordinator, log) = Create();

        Assert.True(coordinator.BeginShutdown());
        Assert.True(coordinator.IsShuttingDown);
        Assert.Equal(CaptureSessionState.Idle, coordinator.State);
        Assert.False(coordinator.IsBusy);
        Assert.Equal(CaptureArbitration.ShuttingDown, coordinator.Decide(CaptureIntent.StartQuick));

        Assert.Null(coordinator.Begin(Guid.NewGuid(), CaptureSessionKind.Quick));
        Assert.False(coordinator.TryReserveOverlay(out _, out _));
        Assert.False(coordinator.TryMarkSucceeded(Guid.NewGuid(), CaptureOrigin.Window, CaptureEngine.Wgc));
        Assert.False(coordinator.TryMarkFailed(Guid.NewGuid(), CaptureFailure.Timeout, "late"));
        Assert.True(coordinator.IsShuttingDown);
        Assert.Contains(log, line => line == "Capture: shutdown sealed (owner=<none>, latched=False, reservation=<none>, invalidated=<none>)");
    }

    [Fact]
    public void Decide_StartOverlay_MatchesBeginReservationRules()
    {
        var (coordinator, _) = Create();
        Assert.Equal(CaptureArbitration.ReservationRequired, coordinator.Decide(CaptureIntent.StartOverlay));

        Assert.True(coordinator.TryReserveOverlay(out var reservationId, out _));

        Assert.Equal(CaptureArbitration.ReservationRequired, coordinator.Decide(CaptureIntent.StartOverlay));
        Assert.Equal(CaptureArbitration.ReservationRequired, coordinator.Decide(CaptureIntent.StartOverlay, Guid.Empty, Guid.Empty));
        Assert.Equal(CaptureArbitration.ReservationConflict, coordinator.Decide(CaptureIntent.StartOverlay, Guid.Empty, Guid.NewGuid()));
        Assert.Equal(CaptureArbitration.Granted, coordinator.Decide(CaptureIntent.StartOverlay, Guid.Empty, reservationId));

        Assert.Null(coordinator.Begin(Guid.NewGuid(), CaptureSessionKind.Overlay));
        Assert.Null(coordinator.Begin(Guid.NewGuid(), CaptureSessionKind.Overlay, Guid.NewGuid()));
        Assert.NotNull(coordinator.Begin(Guid.NewGuid(), CaptureSessionKind.Overlay, reservationId));
    }

    [Fact]
    public void MarkSucceeded_IllegalPreparedState_RejectsWithoutMetadata()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));

        Assert.False(coordinator.TryMarkSucceeded(sessionId, CaptureOrigin.Window, CaptureEngine.Wgc));

        var prepared = coordinator.Snapshot(sessionId);
        Assert.Equal(CaptureSessionState.Preparing, prepared.State);
        Assert.Equal(CaptureOrigin.None, prepared.Origin);
        Assert.Equal(CaptureEngine.None, prepared.Engine);
        Assert.Equal(CaptureFailure.None, prepared.Failure);
        Assert.Null(prepared.Detail);
        Assert.DoesNotContain(log, line => line.Contains(" result state=", StringComparison.Ordinal));
        Assert.Contains(log, line => line.EndsWith("attempt-success rejected (state=Preparing)", StringComparison.Ordinal));

        Assert.False(coordinator.TryMarkFailed(sessionId, CaptureFailure.Timeout, "prepared"));
        Assert.Equal(CaptureFailure.None, coordinator.Snapshot(sessionId).Failure);
        Assert.Equal(CaptureSessionState.Preparing, coordinator.State);

        Assert.True(coordinator.TryTransition(sessionId, CaptureSessionState.Selecting, "test-prepared"));
        Assert.False(coordinator.TryMarkSucceeded(sessionId, CaptureOrigin.Window, CaptureEngine.Wgc));
        Assert.Equal(CaptureOrigin.None, coordinator.Snapshot(sessionId).Origin);
        Assert.Equal(CaptureSessionState.Selecting, coordinator.State);
    }

    [Fact]
    public void Latch_AfterShutdownOnLatchedSession_StaysAlreadyLatchedWithoutNewPermit()
    {
        var (coordinator, log) = Create();
        var sessionId = Guid.NewGuid();
        Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
        DriveToCommitting(coordinator, sessionId);
        var first = Latch(coordinator, sessionId);
        Assert.True(coordinator.BeginShutdown());

        Assert.Equal(CaptureArbitration.AlreadyLatched, coordinator.TryLatchCommit(sessionId, out var second));

        Assert.Equal(first, second);
        Assert.True(coordinator.IsCurrent(sessionId));
        Assert.True(coordinator.IsCommitLatched);
        Assert.Single(log, line => line.Contains("commit latched (kind=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommitAndPreemptRace_OnlyOneWinsAndLatchedQuickIsNotPreempted()
    {
        for (var round = 0; round < 32; round++)
        {
            var (coordinator, _) = CreateConcurrent();
            var quickId = Guid.NewGuid();
            Assert.NotNull(coordinator.Begin(quickId, CaptureSessionKind.Quick));
            DriveToCommitting(coordinator, quickId);
            var latchGranted = false;
            var reserveSucceeded = false;
            using var barrier = new Barrier(2);

            var latchTask = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    latchGranted = coordinator.TryLatchCommit(quickId, out _) == CaptureArbitration.Granted;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            var reserveTask = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    reserveSucceeded = coordinator.TryReserveOverlay(out _, out _);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            await Task.WhenAll(latchTask, reserveTask);

            Assert.True(latchGranted ^ reserveSucceeded);
            if (latchGranted)
            {
                Assert.True(coordinator.IsCommitLatched);
                Assert.True(coordinator.IsCurrent(quickId));
                Assert.Equal(Guid.Empty, coordinator.OverlayReservationId);
                var snapshot = coordinator.End(quickId, "quick-success");
                Assert.NotNull(snapshot);
                Assert.Equal(CaptureSessionState.Completed, snapshot!.State);
                Assert.False(coordinator.IsBusy);
            }
            else
            {
                Assert.False(coordinator.IsCommitLatched);
                Assert.False(coordinator.IsCurrent(quickId));
                Assert.False(coordinator.Snapshot().Active);
                Assert.Equal(CaptureSessionState.Idle, coordinator.State);
                Assert.Equal(CaptureFailure.Canceled, coordinator.Snapshot(quickId).Failure);
                Assert.NotEqual(Guid.Empty, coordinator.OverlayReservationId);
                Assert.Null(coordinator.End(quickId, "cancel"));
                Assert.True(coordinator.IsBusy);
            }
        }
    }

    [Fact]
    public async Task ShutdownAndCommitRace_LatchedSessionSurvivesUntilEnd()
    {
        for (var round = 0; round < 32; round++)
        {
            var (coordinator, _) = CreateConcurrent();
            var sessionId = Guid.NewGuid();
            Assert.NotNull(coordinator.Begin(sessionId, CaptureSessionKind.Quick));
            DriveToCommitting(coordinator, sessionId);
            var latchDecision = CaptureArbitration.None;
            var sealedNow = false;
            using var barrier = new Barrier(2);

            var latchTask = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    latchDecision = coordinator.TryLatchCommit(sessionId, out _);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            var shutdownTask = Task.Factory.StartNew(
                () =>
                {
                    barrier.SignalAndWait();
                    sealedNow = coordinator.BeginShutdown();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            await Task.WhenAll(latchTask, shutdownTask);

            Assert.True(sealedNow);
            Assert.True(coordinator.IsShuttingDown);
            if (latchDecision == CaptureArbitration.Granted)
            {
                Assert.True(coordinator.IsCommitLatched);
                Assert.True(coordinator.IsCurrent(sessionId));
                Assert.True(coordinator.IsBusy);
                var snapshot = coordinator.End(sessionId, "confirm-success");
                Assert.NotNull(snapshot);
                Assert.Equal(CaptureSessionState.Completed, snapshot!.State);
                Assert.Equal(CaptureFailure.None, snapshot.Failure);
            }
            else
            {
                Assert.Equal(CaptureArbitration.Denied, latchDecision);
                Assert.False(coordinator.IsCommitLatched);
                Assert.False(coordinator.IsCurrent(sessionId));
                var snapshot = coordinator.End(sessionId, "cancel");
                Assert.NotNull(snapshot);
                Assert.Equal(CaptureSessionState.Canceled, snapshot!.State);
                Assert.Equal(CaptureFailure.Canceled, snapshot.Failure);
            }
            Assert.False(coordinator.IsBusy);
            Assert.True(coordinator.IsShuttingDown);
        }
    }
}
