namespace SmartTVRelay.Core.Tests.Telemetry;

using System.Linq;
using Observation.Core;
using SmartTVRelay.Core;
using SmartTVRelay.Core.Catalog;
using SmartTVRelay.Core.Relay;
using SmartTVRelay.Core.Scheduling;
using SmartTVRelay.Core.Telemetry;
using Xunit;

public class SubstitutionTelemetryRecorderTests
{
    private static readonly ObservationScope Scope = ObservationScope.Of("test-source");
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static FusionResult<BroadcastState> AgreedCommercial(params Guid[] evidenceIds)
    {
        var ids = evidenceIds.Length == 0 ? [Guid.NewGuid()] : evidenceIds;
        return new FusionResult<BroadcastState>(
            Scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Commercial,
            0.9,
            FusionStatus.Agreed,
            ids,
            [new FusionCandidate<BroadcastState>(BroadcastState.Commercial, 0.9, ids)],
            []);
    }

    [Fact]
    public void RecordDecision_AuthorizeAndCommitted_ExecutedIsTrue()
    {
        var recorder = new SubstitutionTelemetryRecorder();
        var fusion = AgreedCommercial();
        var policyResult = new ReplacementPolicyResult(ReplacementDecision.AuthorizeReplacement, BroadcastState.Commercial, fusion);

        var evt = recorder.RecordDecision(Epoch, Epoch + TimeSpan.FromSeconds(5), policyResult, RelayState.Replacement, 0.75);

        Assert.True(evt.Executed);
        Assert.Equal(BroadcastState.Commercial, evt.FusedState);
        Assert.Equal(0.9, evt.FusedConfidence);
        Assert.Equal(0.75, evt.PolicyThreshold);
        Assert.Single(evt.ContributingEvidence);
    }

    [Fact]
    public void RecordDecision_AuthorizedButStillEnteringReplacement_ExecutedIsFalse()
    {
        var recorder = new SubstitutionTelemetryRecorder();
        var fusion = AgreedCommercial();
        var policyResult = new ReplacementPolicyResult(ReplacementDecision.AuthorizeReplacement, BroadcastState.Commercial, fusion);

        var evt = recorder.RecordDecision(Epoch, Epoch + TimeSpan.FromSeconds(5), policyResult, RelayState.EnteringReplacement, 0.75);

        Assert.False(evt.Executed);
        Assert.Equal(ReplacementDecision.AuthorizeReplacement, evt.PolicyDecision);
    }

    [Fact]
    public void RecordDecision_PreserveOriginal_ExecutedIsFalse()
    {
        var recorder = new SubstitutionTelemetryRecorder();
        var fusion = new FusionResult<BroadcastState>(
            Scope, BroadcastObservations.Subject, BroadcastObservations.Predicate,
            BroadcastState.Program, 0.95, FusionStatus.Agreed, [Guid.NewGuid()],
            [new FusionCandidate<BroadcastState>(BroadcastState.Program, 0.95, [Guid.NewGuid()])], []);
        var policyResult = new ReplacementPolicyResult(ReplacementDecision.PreserveOriginal, BroadcastState.Program, fusion);

        var evt = recorder.RecordDecision(Epoch, Epoch + TimeSpan.FromSeconds(5), policyResult, RelayState.Original, 0.75);

        Assert.False(evt.Executed);
    }

    [Fact]
    public void RecordDecision_WithSchedule_SummarizesCountAndDurationsOnly()
    {
        var recorder = new SubstitutionTelemetryRecorder();
        var fusion = AgreedCommercial();
        var policyResult = new ReplacementPolicyResult(ReplacementDecision.AuthorizeReplacement, BroadcastState.Commercial, fusion);
        var clip = new ReplacementClipEntry(
            "/media/ads/clip1.ts", "/media/ads/clip1.ts", TimeSpan.FromSeconds(30),
            new CodecCompatibilitySummary("ts", "h264", "aac"), [], true);
        var schedule = new ReplacementSchedule(SchedulingOutcome.Fit, [clip], TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        var evt = recorder.RecordDecision(Epoch, Epoch + TimeSpan.FromSeconds(30), policyResult, RelayState.Replacement, 0.75, schedule);

        Assert.NotNull(evt.SelectedScheduleSummary);
        Assert.DoesNotContain("/media/ads/clip1.ts", evt.SelectedScheduleSummary);
        Assert.Contains("1 clip(s)", evt.SelectedScheduleSummary);
        Assert.Contains("Fit", evt.SelectedScheduleSummary);
    }

    [Fact]
    public void RecordDecision_WithoutSchedule_SummaryIsNull()
    {
        var recorder = new SubstitutionTelemetryRecorder();
        var fusion = AgreedCommercial();
        var policyResult = new ReplacementPolicyResult(ReplacementDecision.PreserveOriginal, BroadcastState.Program, fusion);

        var evt = recorder.RecordDecision(Epoch, Epoch + TimeSpan.FromSeconds(5), policyResult, RelayState.Original, 0.75);

        Assert.Null(evt.SelectedScheduleSummary);
    }

    [Fact]
    public void RecordDecision_NoCorrelationIdProvided_GeneratesDistinctIdsPerCall()
    {
        var recorder = new SubstitutionTelemetryRecorder();
        var fusion = AgreedCommercial();
        var policyResult = new ReplacementPolicyResult(ReplacementDecision.PreserveOriginal, BroadcastState.Program, fusion);

        var first = recorder.RecordDecision(Epoch, Epoch + TimeSpan.FromSeconds(5), policyResult, RelayState.Original, 0.75);
        var second = recorder.RecordDecision(Epoch, Epoch + TimeSpan.FromSeconds(5), policyResult, RelayState.Original, 0.75);

        Assert.NotEqual(first.CorrelationId, second.CorrelationId);
    }

    [Fact]
    public void RecordDecision_ProvidedCorrelationId_IsUsedVerbatim()
    {
        var recorder = new SubstitutionTelemetryRecorder();
        var fusion = AgreedCommercial();
        var policyResult = new ReplacementPolicyResult(ReplacementDecision.PreserveOriginal, BroadcastState.Program, fusion);
        var id = Guid.NewGuid();

        var evt = recorder.RecordDecision(Epoch, Epoch + TimeSpan.FromSeconds(5), policyResult, RelayState.Original, 0.75, correlationId: id);

        Assert.Equal(id, evt.CorrelationId);
    }

    [Fact]
    public void RecordFault_AppendsFaultEventWithReasonAndMessage()
    {
        var recorder = new SubstitutionTelemetryRecorder();
        var diagnostics = new RelayFaultDiagnostics(RelayFaultReason.OutputFailure, Epoch, "pump faulted", []);

        var evt = recorder.RecordFault(diagnostics, RelayState.FailOpen);

        Assert.Equal(RelayFaultTelemetryKind.Fault, evt.Kind);
        Assert.Equal(RelayFaultReason.OutputFailure, evt.Reason);
        Assert.Equal("pump faulted", evt.Message);
        Assert.Equal(RelayState.FailOpen, evt.RelayState);
    }

    [Fact]
    public void RecordRecovery_AppendsRecoveryEventWithNullReason()
    {
        var recorder = new SubstitutionTelemetryRecorder();

        var evt = recorder.RecordRecovery(Epoch, RelayState.Original);

        Assert.Equal(RelayFaultTelemetryKind.Recovery, evt.Kind);
        Assert.Null(evt.Reason);
        Assert.Null(evt.Message);
    }

    [Fact]
    public void Events_PreservesRecordingOrderAcrossDecisionAndFaultEvents()
    {
        var recorder = new SubstitutionTelemetryRecorder();
        var fusion = AgreedCommercial();
        var policyResult = new ReplacementPolicyResult(ReplacementDecision.PreserveOriginal, BroadcastState.Program, fusion);
        var diagnostics = new RelayFaultDiagnostics(RelayFaultReason.SourceDisconnected, Epoch, "disconnected", []);

        recorder.RecordDecision(Epoch, Epoch + TimeSpan.FromSeconds(5), policyResult, RelayState.Original, 0.75);
        recorder.RecordFault(diagnostics, RelayState.FailOpen);
        recorder.RecordRecovery(Epoch + TimeSpan.FromSeconds(10), RelayState.Original);

        Assert.Collection(
            recorder.Events,
            e => Assert.IsType<SubstitutionDecisionEvent>(e),
            e => Assert.IsType<RelayFaultTelemetryEvent>(e),
            e => Assert.IsType<RelayFaultTelemetryEvent>(e));
    }

    // -- Acceptance criterion: a fixture-style scenario verifies the trace chain end to end,
    // including that a denied/faulted decision is distinguishable from an executed one, and that
    // a correlation id threads a decision through to the fault it caused and its later recovery.

    [Fact]
    public void FixtureTraceChain_DecisionFaultAndRecoverySequence_CorrelatesAndDistinguishesOutcomes()
    {
        var recorder = new SubstitutionTelemetryRecorder();

        // Segment 1: clean authorize, already committed -- executed.
        var seg1Fusion = AgreedCommercial();
        var seg1Policy = new ReplacementPolicyResult(ReplacementDecision.AuthorizeReplacement, BroadcastState.Commercial, seg1Fusion);
        var seg1 = recorder.RecordDecision(Epoch, Epoch + TimeSpan.FromSeconds(5), seg1Policy, RelayState.Replacement, 0.75);

        // An output fault occurs immediately after segment 1 -- correlated to that same decision,
        // so a consumer can trace "this decision's replacement was interrupted by this fault."
        var faultAt = Epoch + TimeSpan.FromSeconds(6);
        var diagnostics = new RelayFaultDiagnostics(RelayFaultReason.OutputFailure, faultAt, "pump faulted", []);
        var faultEvt = recorder.RecordFault(diagnostics, RelayState.FailOpen, correlationId: seg1.CorrelationId);

        // Segment 2: policy would still authorize (evidence still says Commercial), but the relay
        // is FailOpen -- so despite an Authorize policy decision, nothing executes. This is the
        // "denied vs executed" and "failures never increase authority" acceptance criteria made
        // concrete in the trace.
        var seg2Fusion = AgreedCommercial();
        var seg2Policy = new ReplacementPolicyResult(ReplacementDecision.AuthorizeReplacement, BroadcastState.Commercial, seg2Fusion);
        var seg2 = recorder.RecordDecision(
            Epoch + TimeSpan.FromSeconds(6), Epoch + TimeSpan.FromSeconds(11), seg2Policy, RelayState.FailOpen, 0.75);

        // Recovery, correlated to the same fault lifecycle.
        var recoverAt = Epoch + TimeSpan.FromSeconds(15);
        var recoveryEvt = recorder.RecordRecovery(recoverAt, RelayState.Original, correlationId: faultEvt.CorrelationId);

        // Segment 3: back to normal, authorize commits again.
        var seg3Fusion = AgreedCommercial();
        var seg3Policy = new ReplacementPolicyResult(ReplacementDecision.AuthorizeReplacement, BroadcastState.Commercial, seg3Fusion);
        var seg3 = recorder.RecordDecision(
            Epoch + TimeSpan.FromSeconds(15), Epoch + TimeSpan.FromSeconds(20), seg3Policy, RelayState.Replacement, 0.75);

        // The trace is complete and in chronological order.
        Assert.Equal(5, recorder.Events.Count);
        Assert.True(recorder.Events.Zip(recorder.Events.Skip(1)).All(p => p.First.OccurredAt <= p.Second.OccurredAt));

        // Executed vs denied is distinguishable directly from the recorded events.
        Assert.True(seg1.Executed);
        Assert.False(seg2.Executed); // authorized by policy, but denied by the active fault
        Assert.True(seg3.Executed);

        // The fault and its later recovery share one correlation id -- the trace chain a consumer
        // follows to answer "what happened to the fault started at segment 1?"
        Assert.Equal(faultEvt.CorrelationId, recoveryEvt.CorrelationId);
        Assert.Equal(seg1.CorrelationId, faultEvt.CorrelationId);

        // Each decision still has its own distinct correlation id.
        Assert.NotEqual(seg1.CorrelationId, seg2.CorrelationId);
        Assert.NotEqual(seg2.CorrelationId, seg3.CorrelationId);
    }
}
