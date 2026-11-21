using System.Linq;
using PrtScr.Core;
using Xunit;

namespace PrtScr.Core.Tests;

public sealed class SmokeRunStateTests
{
    [Fact]
    public void PartialChecks_DoNotComplete()
    {
        var state = new SmokeRunState();
        Assert.Null(state.TryRecordCheck(SmokeProtocol.XamlInitializedCheck));
        Assert.Null(state.TryRecordCheck(SmokeProtocol.MainWindowLoadedCheck));
    }

    [Fact]
    public void AllThreeChecks_CompleteWithCanonicalOrder()
    {
        var state = new SmokeRunState();
        state.TryRecordCheck(SmokeProtocol.XamlInitializedCheck);
        state.TryRecordCheck(SmokeProtocol.MainWindowLoadedCheck);
        var outcome = state.TryRecordCheck(SmokeProtocol.DispatcherRoundtripCheck);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Success);
        Assert.Null(outcome.Error);
        Assert.Equal(SmokeProtocol.SuccessChecks, outcome.Checks);
    }

    [Fact]
    public void UnknownOrDuplicateChecks_AreIgnored()
    {
        var state = new SmokeRunState();
        Assert.Null(state.TryRecordCheck("not-a-check"));
        Assert.Null(state.TryRecordCheck(SmokeProtocol.XamlInitializedCheck));
        Assert.Null(state.TryRecordCheck(SmokeProtocol.XamlInitializedCheck));
    }

    [Fact]
    public void Failure_IsTerminal_AndLateSuccessCannotOverride()
    {
        var state = new SmokeRunState();
        var failure = state.TryFail("timeout-waiting-for-main-window-roundtrip");
        Assert.NotNull(failure);
        Assert.False(failure!.Success);
        Assert.Equal("timeout-waiting-for-main-window-roundtrip", failure.Error);
        Assert.Empty(failure.Checks);

        Assert.Null(state.TryRecordCheck(SmokeProtocol.XamlInitializedCheck));
        Assert.Null(state.TryRecordCheck(SmokeProtocol.MainWindowLoadedCheck));
        Assert.Null(state.TryRecordCheck(SmokeProtocol.DispatcherRoundtripCheck));
        Assert.Null(state.TryFail("second-failure"));
    }

    [Fact]
    public void Success_IsTerminal_AndFailureCannotOverride()
    {
        var state = new SmokeRunState();
        state.TryRecordCheck(SmokeProtocol.XamlInitializedCheck);
        state.TryRecordCheck(SmokeProtocol.MainWindowLoadedCheck);
        var success = state.TryRecordCheck(SmokeProtocol.DispatcherRoundtripCheck);
        Assert.True(success!.Success);

        Assert.Null(state.TryFail("late-failure"));
        Assert.Null(state.TryRecordCheck(SmokeProtocol.DispatcherRoundtripCheck));
    }

    [Fact]
    public void EmptyFailureReason_IsNormalized()
    {
        var state = new SmokeRunState();
        var failure = state.TryFail("  ");
        Assert.Equal("unspecified", failure!.Error);
    }
}
