namespace PrtScr.Core.Tests;

public sealed class CaptureRulesTests
{
    [Theory]
    [InlineData(CaptureSessionState.Idle, CaptureSessionState.Preparing)]
    [InlineData(CaptureSessionState.Preparing, CaptureSessionState.Selecting)]
    [InlineData(CaptureSessionState.Preparing, CaptureSessionState.Canceled)]
    [InlineData(CaptureSessionState.Preparing, CaptureSessionState.Failed)]
    [InlineData(CaptureSessionState.Selecting, CaptureSessionState.Capturing)]
    [InlineData(CaptureSessionState.Selecting, CaptureSessionState.Canceled)]
    [InlineData(CaptureSessionState.Capturing, CaptureSessionState.Committing)]
    [InlineData(CaptureSessionState.Capturing, CaptureSessionState.Selecting)]
    [InlineData(CaptureSessionState.Capturing, CaptureSessionState.Canceled)]
    [InlineData(CaptureSessionState.Capturing, CaptureSessionState.Failed)]
    [InlineData(CaptureSessionState.Committing, CaptureSessionState.Completed)]
    [InlineData(CaptureSessionState.Committing, CaptureSessionState.Selecting)]
    [InlineData(CaptureSessionState.Committing, CaptureSessionState.Failed)]
    public void LegalTransitions_AreAllowed(CaptureSessionState from, CaptureSessionState to)
    {
        Assert.True(CaptureRules.CanTransition(from, to));
    }

    [Theory]
    [InlineData(CaptureSessionState.Idle, CaptureSessionState.Idle)]
    [InlineData(CaptureSessionState.Idle, CaptureSessionState.Selecting)]
    [InlineData(CaptureSessionState.Idle, CaptureSessionState.Completed)]
    [InlineData(CaptureSessionState.Preparing, CaptureSessionState.Idle)]
    [InlineData(CaptureSessionState.Preparing, CaptureSessionState.Capturing)]
    [InlineData(CaptureSessionState.Preparing, CaptureSessionState.Committing)]
    [InlineData(CaptureSessionState.Preparing, CaptureSessionState.Completed)]
    [InlineData(CaptureSessionState.Selecting, CaptureSessionState.Idle)]
    [InlineData(CaptureSessionState.Selecting, CaptureSessionState.Preparing)]
    [InlineData(CaptureSessionState.Selecting, CaptureSessionState.Committing)]
    [InlineData(CaptureSessionState.Selecting, CaptureSessionState.Completed)]
    [InlineData(CaptureSessionState.Selecting, CaptureSessionState.Failed)]
    [InlineData(CaptureSessionState.Capturing, CaptureSessionState.Idle)]
    [InlineData(CaptureSessionState.Capturing, CaptureSessionState.Preparing)]
    [InlineData(CaptureSessionState.Capturing, CaptureSessionState.Completed)]
    [InlineData(CaptureSessionState.Committing, CaptureSessionState.Idle)]
    [InlineData(CaptureSessionState.Committing, CaptureSessionState.Preparing)]
    [InlineData(CaptureSessionState.Committing, CaptureSessionState.Capturing)]
    [InlineData(CaptureSessionState.Committing, CaptureSessionState.Canceled)]
    [InlineData(CaptureSessionState.Completed, CaptureSessionState.Selecting)]
    [InlineData(CaptureSessionState.Canceled, CaptureSessionState.Preparing)]
    [InlineData(CaptureSessionState.Failed, CaptureSessionState.Selecting)]
    public void IllegalTransitions_AreRejected(CaptureSessionState from, CaptureSessionState to)
    {
        Assert.False(CaptureRules.CanTransition(from, to));
    }

    [Fact]
    public void TerminalStates_HaveNoOutgoingTransitions()
    {
        CaptureSessionState[] terminals =
        [
            CaptureSessionState.Completed,
            CaptureSessionState.Canceled,
            CaptureSessionState.Failed,
        ];

        foreach (var terminal in terminals)
        {
            Assert.True(CaptureRules.IsTerminal(terminal));
            foreach (var target in Enum.GetValues<CaptureSessionState>())
                Assert.False(CaptureRules.CanTransition(terminal, target), $"{terminal} 不应能迁移到 {target}");
        }
    }

    [Theory]
    [InlineData(CaptureSessionState.Preparing, true)]
    [InlineData(CaptureSessionState.Selecting, true)]
    [InlineData(CaptureSessionState.Capturing, true)]
    [InlineData(CaptureSessionState.Committing, true)]
    [InlineData(CaptureSessionState.Idle, false)]
    [InlineData(CaptureSessionState.Completed, false)]
    [InlineData(CaptureSessionState.Canceled, false)]
    [InlineData(CaptureSessionState.Failed, false)]
    public void IsActive_OnlyNonIdleNonTerminal(CaptureSessionState state, bool expected)
    {
        Assert.Equal(expected, CaptureRules.IsActive(state));
    }

    [Theory]
    [InlineData(CaptureEndReason.StartFailed, CaptureTerminateDecision.Allowed)]
    [InlineData(CaptureEndReason.Cancel, CaptureTerminateDecision.Invalid)]
    [InlineData(CaptureEndReason.ConfirmSuccess, CaptureTerminateDecision.Invalid)]
    [InlineData(CaptureEndReason.Ocr, CaptureTerminateDecision.Invalid)]
    [InlineData(CaptureEndReason.Save, CaptureTerminateDecision.Invalid)]
    [InlineData(CaptureEndReason.None, CaptureTerminateDecision.Invalid)]
    public void TerminateDecision_IdleOnlyAcceptsStartFailed(
        CaptureEndReason reason, CaptureTerminateDecision expected)
    {
        Assert.Equal(expected, CaptureRules.TerminateDecision(CaptureSessionState.Idle, reason));
    }

    [Theory]
    [InlineData(CaptureSessionState.Completed)]
    [InlineData(CaptureSessionState.Canceled)]
    [InlineData(CaptureSessionState.Failed)]
    public void TerminateDecision_TerminalStateIsIdempotentDuplicate(CaptureSessionState state)
    {
        foreach (var reason in Enum.GetValues<CaptureEndReason>())
            Assert.Equal(CaptureTerminateDecision.Duplicate, CaptureRules.TerminateDecision(state, reason));
    }

    [Theory]
    [InlineData(CaptureSessionState.Preparing, CaptureEndReason.Cancel)]
    [InlineData(CaptureSessionState.Selecting, CaptureEndReason.Cancel)]
    [InlineData(CaptureSessionState.Capturing, CaptureEndReason.ConfirmSuccess)]
    [InlineData(CaptureSessionState.Committing, CaptureEndReason.ConfirmEnd)]
    [InlineData(CaptureSessionState.Capturing, CaptureEndReason.StartFailed)]
    public void TerminateDecision_ActiveStatesAllowAnyReason(
        CaptureSessionState state, CaptureEndReason reason)
    {
        Assert.Equal(CaptureTerminateDecision.Allowed, CaptureRules.TerminateDecision(state, reason));
    }

    [Theory]
    [InlineData(CaptureEndReason.None, "")]
    [InlineData(CaptureEndReason.Cancel, "cancel")]
    [InlineData(CaptureEndReason.ConfirmSuccess, "confirm-success")]
    [InlineData(CaptureEndReason.ConfirmEnd, "confirm-end")]
    [InlineData(CaptureEndReason.Ocr, "ocr")]
    [InlineData(CaptureEndReason.Save, "save")]
    [InlineData(CaptureEndReason.StartFailed, "start-failed")]
    public void EndReasonText_MatchesLogContract(CaptureEndReason reason, string expected)
    {
        Assert.Equal(expected, CaptureRules.EndReasonText(reason));
    }

    [Theory]
    [InlineData(CaptureOrigin.Window, HistoryCaptureMode.Window)]
    [InlineData(CaptureOrigin.Normal, HistoryCaptureMode.Normal)]
    [InlineData(CaptureOrigin.Screen, HistoryCaptureMode.Normal)]
    [InlineData(CaptureOrigin.None, HistoryCaptureMode.Normal)]
    public void ToHistoryMode_MapsOnlyWindowToWindow(CaptureOrigin origin, HistoryCaptureMode expected)
    {
        Assert.Equal(expected, CaptureRules.ToHistoryMode(origin));
    }

    [Theory]
    [InlineData(CaptureOrigin.Window, false)]
    [InlineData(CaptureOrigin.Normal, false)]
    [InlineData(CaptureOrigin.Screen, true)]
    [InlineData(CaptureOrigin.None, false)]
    public void IsDowngrade_OnlyTrueForScreenOrigin(CaptureOrigin origin, bool expected)
    {
        Assert.Equal(expected, CaptureRules.IsDowngrade(origin));
    }

    [Theory]
    [InlineData(true, CaptureOrigin.Screen)]
    [InlineData(false, CaptureOrigin.Normal)]
    public void ResolveScreenOrigin_DistinguishesDowngradeFromPlainCrop(bool windowAttempted, CaptureOrigin expected)
    {
        Assert.Equal(expected, CaptureRules.ResolveScreenOrigin(windowAttempted));
    }

    [Theory]
    [InlineData(CaptureFailure.Canceled, true)]
    [InlineData(CaptureFailure.Timeout, false)]
    [InlineData(CaptureFailure.Busy, false)]
    [InlineData(CaptureFailure.None, false)]
    public void CountsAsCancel_OnlyCanceledCounts(CaptureFailure failure, bool expected)
    {
        Assert.Equal(expected, CaptureRules.CountsAsCancel(failure));
    }

    [Theory]
    [InlineData(CaptureFailure.Busy, true)]
    [InlineData(CaptureFailure.Timeout, true)]
    [InlineData(CaptureFailure.Canceled, false)]
    [InlineData(CaptureFailure.EncodeFailed, false)]
    [InlineData(CaptureFailure.None, false)]
    public void IsRecoverable_OnlyBusyAndTimeout(CaptureFailure failure, bool expected)
    {
        Assert.Equal(expected, CaptureRules.IsRecoverable(failure));
    }

    [Theory]
    [InlineData(CaptureFailure.None)]
    [InlineData(CaptureFailure.Canceled)]
    public void NoticeFor_QuietFailuresReturnNull(CaptureFailure failure)
    {
        Assert.Null(CaptureRules.NoticeFor(failure));
    }

    [Theory]
    [InlineData(CaptureFailure.Busy, "该窗口上一张截图还在处理中，请稍后重试")]
    [InlineData(CaptureFailure.Timeout, "窗口响应超时，本次未截图；请稍后重试")]
    [InlineData(CaptureFailure.ClipboardFailed, "复制失败：剪贴板被占用，请稍后重试")]
    [InlineData(CaptureFailure.HistoryFailed, "历史保存失败，请检查磁盘空间")]
    [InlineData(CaptureFailure.EncodeFailed, "图片编码失败，请重试")]
    public void NoticeFor_KnownFailuresUseExactText(CaptureFailure failure, string expected)
    {
        Assert.Equal(expected, CaptureRules.NoticeFor(failure));
    }

    [Fact]
    public void NoticeFor_InternalWithoutDetail_UsesGenericText()
    {
        Assert.Equal("截图失败，请重试", CaptureRules.NoticeFor(CaptureFailure.Internal));
    }

    [Fact]
    public void NoticeFor_InternalWithDetail_AppendsDetail()
    {
        Assert.Equal("截图失败：磁盘已满", CaptureRules.NoticeFor(CaptureFailure.Internal, 0, "磁盘已满"));
    }

    [Theory]
    [InlineData(CaptureScenario.ScreenSelection)]
    [InlineData(CaptureScenario.WindowWhole)]
    [InlineData(CaptureScenario.WindowRegion)]
    [InlineData(CaptureScenario.PrintWindow)]
    [InlineData(CaptureScenario.PrintWindowTimeoutWgc)]
    [InlineData(CaptureScenario.PrintWindowTimeoutFailed)]
    [InlineData(CaptureScenario.Busy)]
    [InlineData(CaptureScenario.UserCancel)]
    [InlineData(CaptureScenario.EncodeFailed)]
    [InlineData(CaptureScenario.ClipboardFailed)]
    [InlineData(CaptureScenario.HistoryFailed)]
    [InlineData(CaptureScenario.OcrOrSave)]
    public void MatrixRow_MatchesDesignContract(CaptureScenario scenario)
    {
        var expected = scenario switch
        {
            CaptureScenario.ScreenSelection => new CaptureExpectation(
                CaptureSessionState.Completed, CaptureFailure.None, false, true, true, false),
            CaptureScenario.WindowWhole => new CaptureExpectation(
                CaptureSessionState.Completed, CaptureFailure.None, false, true, true, false),
            CaptureScenario.WindowRegion => new CaptureExpectation(
                CaptureSessionState.Completed, CaptureFailure.None, false, true, true, false),
            CaptureScenario.PrintWindow => new CaptureExpectation(
                CaptureSessionState.Completed, CaptureFailure.None, false, true, true, false),
            CaptureScenario.PrintWindowTimeoutWgc => new CaptureExpectation(
                CaptureSessionState.Completed, CaptureFailure.None, false, true, true, true, PrintWindowTimedOut: true),
            CaptureScenario.PrintWindowTimeoutFailed => new CaptureExpectation(
                CaptureSessionState.Failed, CaptureFailure.Timeout, true, false, false, true, PrintWindowTimedOut: true),
            CaptureScenario.Busy => new CaptureExpectation(
                CaptureSessionState.Selecting, CaptureFailure.Busy, true, false, false, true),
            CaptureScenario.UserCancel => new CaptureExpectation(
                CaptureSessionState.Canceled, CaptureFailure.Canceled, false, false, false, false),
            CaptureScenario.EncodeFailed => new CaptureExpectation(
                CaptureSessionState.Failed, CaptureFailure.EncodeFailed, true, false, false, true),
            CaptureScenario.ClipboardFailed => new CaptureExpectation(
                CaptureSessionState.Completed, CaptureFailure.ClipboardFailed, false, true, false, true),
            CaptureScenario.HistoryFailed => new CaptureExpectation(
                CaptureSessionState.Completed, CaptureFailure.HistoryFailed, false, false, true, true),
            CaptureScenario.OcrOrSave => new CaptureExpectation(
                CaptureSessionState.Completed, CaptureFailure.None, false, null, null, false),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Equal(expected, CaptureRules.ExpectationFor(scenario));
    }

    [Fact]
    public void Matrix_CoversAllTwelveScenarios()
    {
        var scenarios = Enum.GetValues<CaptureScenario>();

        Assert.Equal(12, scenarios.Length);
        Assert.Equal(12, scenarios.Distinct().Count());
        Assert.All(scenarios, scenario => Assert.True(Enum.IsDefined(scenario)));
    }

    [Fact]
    public void Matrix_OnlyCancelRowIsCanceledAndNeverWrites()
    {
        foreach (var scenario in Enum.GetValues<CaptureScenario>())
        {
            var expectation = CaptureRules.ExpectationFor(scenario);
            if (expectation.Failure == CaptureFailure.Canceled)
            {
                Assert.Equal(CaptureSessionState.Canceled, expectation.State);
                Assert.False(expectation.WritesHistory ?? false);
                Assert.False(expectation.WritesClipboard ?? false);
                Assert.False(expectation.ExpectsNotice);
            }
        }
    }

    [Fact]
    public void Matrix_RecoverableRowsRestoreOverlayWithoutWriting()
    {
        foreach (var scenario in Enum.GetValues<CaptureScenario>())
        {
            var expectation = CaptureRules.ExpectationFor(scenario);
            if (!CaptureRules.IsRecoverable(expectation.Failure))
                continue;

            Assert.True(expectation.RestoresOverlay);
            Assert.False(expectation.WritesHistory ?? false);
            Assert.False(expectation.WritesClipboard ?? false);
        }
    }
}
