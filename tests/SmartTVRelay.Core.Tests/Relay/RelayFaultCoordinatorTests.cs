namespace SmartTVRelay.Core.Tests.Relay;

using SmartTVRelay.Core;
using SmartTVRelay.Core.Catalog;
using SmartTVRelay.Core.Relay;
using SmartTVRelay.Core.Scheduling;
using Xunit;

public class RelayFaultCoordinatorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_NullStateMachine_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RelayFaultCoordinator(null!));
    }

    [Fact]
    public void Constructor_NonPositiveMaxDecisionAge_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RelayFaultCoordinator(new RelaySwitchingStateMachine(), TimeSpan.Zero));
    }

    // -- Case: replacement media missing/corrupt --

    [Fact]
    public void ObserveSchedule_NoMedia_FaultsWithReplacementMediaUnavailable()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());
        var schedule = new ReplacementSchedule(SchedulingOutcome.NoMedia, [], TimeSpan.Zero, TimeSpan.FromSeconds(30));

        var result = coordinator.ObserveSchedule(schedule, Epoch);

        Assert.Equal(RelayState.FailOpen, result);
        Assert.Equal(RelayFaultReason.ReplacementMediaUnavailable, coordinator.LastFault!.Reason);
    }

    // -- Case: scheduler failure --

    [Fact]
    public void ObserveSchedule_NoFit_FaultsWithSchedulerFailure()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());
        var candidate = new ReplacementClipEntry(
            "clip.ts", "clip.ts", TimeSpan.FromSeconds(5), new CodecCompatibilitySummary("ts", "h264", "aac"), [], true);
        var schedule = new ReplacementSchedule(SchedulingOutcome.NoFit, [candidate], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

        var result = coordinator.ObserveSchedule(schedule, Epoch);

        Assert.Equal(RelayState.FailOpen, result);
        Assert.Equal(RelayFaultReason.SchedulerFailure, coordinator.LastFault!.Reason);
    }

    [Fact]
    public void ObserveSchedule_Fit_DoesNotFault()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());
        var candidate = new ReplacementClipEntry(
            "clip.ts", "clip.ts", TimeSpan.FromSeconds(30), new CodecCompatibilitySummary("ts", "h264", "aac"), [], true);
        var schedule = new ReplacementSchedule(SchedulingOutcome.Fit, [candidate], TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        var result = coordinator.ObserveSchedule(schedule, Epoch);

        Assert.Equal(RelayState.Original, result);
        Assert.Null(coordinator.LastFault);
    }

    // -- Case: stale detector/policy data --

    [Fact]
    public void CheckDecisionFreshness_NoDecisionEverAndPastMaxAge_Faults()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine(), TimeSpan.FromSeconds(30));

        var result = coordinator.CheckDecisionFreshness(Epoch);

        Assert.Equal(RelayState.FailOpen, result);
        Assert.Equal(RelayFaultReason.StaleDecisionData, coordinator.LastFault!.Reason);
    }

    [Fact]
    public void CheckDecisionFreshness_RecentDecision_DoesNotFault()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine(), TimeSpan.FromSeconds(30));
        coordinator.Advance(ReplacementDecision.PreserveOriginal, Epoch);

        var result = coordinator.CheckDecisionFreshness(Epoch + TimeSpan.FromSeconds(10));

        Assert.Equal(RelayState.Original, result);
        Assert.Null(coordinator.LastFault);
    }

    [Fact]
    public void CheckDecisionFreshness_DecisionOlderThanMaxAge_Faults()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine(), TimeSpan.FromSeconds(30));
        coordinator.Advance(ReplacementDecision.PreserveOriginal, Epoch);

        var result = coordinator.CheckDecisionFreshness(Epoch + TimeSpan.FromSeconds(31));

        Assert.Equal(RelayState.FailOpen, result);
        Assert.Equal(RelayFaultReason.StaleDecisionData, coordinator.LastFault!.Reason);
    }

    // -- Case: output failure --

    [Fact]
    public void ObserveOutputStatus_Faulted_FaultsWithOutputFailure()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());
        coordinator.ObserveOutputStatus(RelayOutputStatus.Running, Epoch);

        var result = coordinator.ObserveOutputStatus(RelayOutputStatus.Faulted, Epoch);

        Assert.Equal(RelayState.FailOpen, result);
        Assert.Equal(RelayFaultReason.OutputFailure, coordinator.LastFault!.Reason);
    }

    [Fact]
    public void ObserveOutputStatus_ExpectedTransitionSequence_DoesNotFault()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());

        coordinator.ObserveOutputStatus(RelayOutputStatus.Running, Epoch);
        var result = coordinator.ObserveOutputStatus(RelayOutputStatus.Completed, Epoch);

        Assert.Equal(RelayState.Original, result);
        Assert.Null(coordinator.LastFault);
    }

    // -- Case: unexpected state transition --

    [Fact]
    public void ObserveOutputStatus_TransitionOutOfTerminalState_FaultsWithUnexpectedStateTransition()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());
        coordinator.ObserveOutputStatus(RelayOutputStatus.Running, Epoch);
        coordinator.ObserveOutputStatus(RelayOutputStatus.Completed, Epoch);

        var result = coordinator.ObserveOutputStatus(RelayOutputStatus.Running, Epoch);

        Assert.Equal(RelayState.FailOpen, result);
        Assert.Equal(RelayFaultReason.UnexpectedStateTransition, coordinator.LastFault!.Reason);
    }

    [Fact]
    public void ObserveOutputStatus_SkippingRunning_FaultsWithUnexpectedStateTransition()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());

        var result = coordinator.ObserveOutputStatus(RelayOutputStatus.Completed, Epoch);

        Assert.Equal(RelayState.FailOpen, result);
        Assert.Equal(RelayFaultReason.UnexpectedStateTransition, coordinator.LastFault!.Reason);
    }

    [Fact]
    public void ObserveOutputStatus_RepeatedSameStatus_IsNotUnexpected()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());

        coordinator.ObserveOutputStatus(RelayOutputStatus.Idle, Epoch);
        var result = coordinator.ObserveOutputStatus(RelayOutputStatus.Idle, Epoch);

        Assert.Equal(RelayState.Original, result);
        Assert.Null(coordinator.LastFault);
    }

    // -- Case: source reconnect (disconnect faults, reconnect recovers deterministically) --

    [Fact]
    public void ReportSourceDisconnected_Faults()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());

        var result = coordinator.ReportSourceDisconnected(Epoch);

        Assert.Equal(RelayState.FailOpen, result);
        Assert.Equal(RelayFaultReason.SourceDisconnected, coordinator.LastFault!.Reason);
    }

    [Fact]
    public void Recover_AfterSourceDisconnect_DeterministicallyReturnsToOriginal()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());
        coordinator.ReportSourceDisconnected(Epoch);

        var result = coordinator.Recover(Epoch + TimeSpan.FromSeconds(5));

        Assert.Equal(RelayState.Original, result);
        Assert.Null(coordinator.LastFault);
    }

    // -- Recovery semantics shared across all cases --

    [Fact]
    public void Recover_ResetsDecisionFreshnessWindow_SoItDoesNotImmediatelyRefault()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine(), TimeSpan.FromSeconds(30));
        coordinator.CheckDecisionFreshness(Epoch); // faults: no decision ever observed
        var recoveredAt = Epoch + TimeSpan.FromSeconds(100);

        coordinator.Recover(recoveredAt);
        var result = coordinator.CheckDecisionFreshness(recoveredAt);

        Assert.Equal(RelayState.Original, result);
        Assert.Null(coordinator.LastFault);
    }

    [Fact]
    public void Recover_WhenNotFaulted_IsNoOp()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());

        var result = coordinator.Recover(Epoch);

        Assert.Equal(RelayState.Original, result);
        Assert.Null(coordinator.LastFault);
    }

    // -- Failures never increase replacement authority --

    [Fact]
    public void WhileFaulted_AdvanceTowardReplacementHasNoEffect()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());
        coordinator.ReportSourceDisconnected(Epoch);

        var result = coordinator.Advance(ReplacementDecision.AuthorizeReplacement, Epoch);

        Assert.Equal(RelayState.FailOpen, result);
    }

    [Fact]
    public void LastFault_ReflectsMostRecentFaultOnly()
    {
        var coordinator = new RelayFaultCoordinator(new RelaySwitchingStateMachine());
        coordinator.ReportSourceDisconnected(Epoch);
        coordinator.Recover(Epoch);

        coordinator.ObserveSchedule(
            new ReplacementSchedule(SchedulingOutcome.NoMedia, [], TimeSpan.Zero, TimeSpan.FromSeconds(30)),
            Epoch);

        Assert.Equal(RelayFaultReason.ReplacementMediaUnavailable, coordinator.LastFault!.Reason);
    }
}
