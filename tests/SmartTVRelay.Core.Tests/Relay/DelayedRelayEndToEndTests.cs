namespace SmartTVRelay.Core.Tests.Relay;

using System.Linq;
using Observation.Core;
using SmartTVRelay.Core;
using SmartTVRelay.Core.Catalog;
using SmartTVRelay.Core.Ingest;
using SmartTVRelay.Core.Relay;
using SmartTVRelay.Core.Scheduling;
using SmartTVRelay.Core.Telemetry;
using Xunit;

/// <summary>
/// End-to-end fixture test for #51: proves Program -&gt; Commercial -&gt; Program through the full
/// delayed relay chain using deterministic, synthetic evidence -- no live hardware/network, no
/// wall-clock sleeps. Integration test only, per the issue's agent boundary: it wires together
/// already-merged production types (#4's fusion/policy, #44/#45's delayed output, #47's scheduler,
/// #48's state machine, #49's fault coordinator, #50's telemetry) rather than adding new production
/// code.
/// </summary>
public class DelayedRelayEndToEndTests
{
    private static readonly ObservationScope Scope = ObservationScope.Of("e2e-source");
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan SegmentDuration = TimeSpan.FromSeconds(5);

    private static string GetFixturePath(string filename) => Path.Combine(AppContext.BaseDirectory, "synthetic", filename);

    private static FusionResult<BroadcastState> Agreed(BroadcastState value, double confidence) =>
        new(
            Scope, BroadcastObservations.Subject, BroadcastObservations.Predicate,
            value, confidence, FusionStatus.Agreed, [Guid.NewGuid()],
            [new FusionCandidate<BroadcastState>(value, confidence, [Guid.NewGuid()])], []);

    private static FusionResult<BroadcastState> Disputed() =>
        new(
            Scope, BroadcastObservations.Subject, BroadcastObservations.Predicate,
            BroadcastState.Unknown, 0.5, FusionStatus.Disputed, [Guid.NewGuid(), Guid.NewGuid()],
            [
                new FusionCandidate<BroadcastState>(BroadcastState.Program, 0.5, [Guid.NewGuid()]),
                new FusionCandidate<BroadcastState>(BroadcastState.Commercial, 0.5, [Guid.NewGuid()]),
            ],
            []);

    /// <summary>
    /// The full timeline: baseline program, a disputed/ambiguous case that must NOT enter
    /// replacement, high-confidence commercial that debounces into replacement, a fault that must
    /// force fail-open even against concurrently-authorizing evidence, explicit recovery, and a
    /// return to program that must complete within the state machine's documented fast-exit bound.
    /// Every expected RelayState is asserted against the actual recorded trace -- the "output
    /// timeline machine-verified" acceptance criterion -- rather than eyeballed.
    /// </summary>
    [Fact]
    public void FullTimeline_ProgramCommercialProgram_MatchesExpectedStateSequenceExactly()
    {
        var stateMachine = new RelaySwitchingStateMachine(confirmationsRequiredToEnterReplacement: 2);
        var coordinator = new RelayFaultCoordinator(stateMachine, maxDecisionAge: TimeSpan.FromSeconds(30));
        var policy = new BroadcastReplacementPolicy(confidenceThreshold: 0.75);
        var recorder = new SubstitutionTelemetryRecorder();

        var catalog = new[]
        {
            new ReplacementClipEntry(
                "ad-1", "/media/ads/ad-1.ts", TimeSpan.FromSeconds(5),
                new CodecCompatibilitySummary("ts", "h264", "aac"), [], true),
        };
        var scheduler = new ReplacementScheduler();

        var t = Epoch;
        RelayState Segment(FusionResult<BroadcastState> fusion, bool scheduleReplacement = false)
        {
            var policyResult = policy.Evaluate(fusion);
            var segmentStart = t;
            var segmentEnd = t + SegmentDuration;

            ReplacementSchedule? schedule = null;
            if (scheduleReplacement && policyResult.Decision == ReplacementDecision.AuthorizeReplacement)
            {
                schedule = scheduler.Schedule(SegmentDuration, catalog);
            }

            var relayState = coordinator.Advance(policyResult.Decision, segmentEnd);
            recorder.RecordDecision(segmentStart, segmentEnd, policyResult, relayState, policyThreshold: 0.75, selectedSchedule: schedule);

            t = segmentEnd;
            return relayState;
        }

        // 1. Baseline: high-confidence Program -> stays Original.
        var s1 = Segment(Agreed(BroadcastState.Program, 0.95));
        Assert.Equal(RelayState.Original, s1);

        // 2. Disputed/ambiguous evidence -> policy fails open (PreserveOriginal) -> stays Original.
        // Acceptance: "ambiguous/disputed case stays on original."
        var s2 = Segment(Disputed());
        Assert.Equal(RelayState.Original, s2);

        // 3. High-confidence Commercial, 1st confirmation -> EnteringReplacement, not yet committed.
        var s3 = Segment(Agreed(BroadcastState.Commercial, 0.9));
        Assert.Equal(RelayState.EnteringReplacement, s3);

        // 4. High-confidence Commercial, 2nd confirmation -> commits to Replacement, with a
        // schedule selected. Acceptance: "high-confidence commercial enters replacement."
        var s4 = Segment(Agreed(BroadcastState.Commercial, 0.9), scheduleReplacement: true);
        Assert.Equal(RelayState.Replacement, s4);

        // 5. A relay output fault occurs while evidence would otherwise still authorize
        // replacement -- the fault must force fail-open regardless. Acceptance: "replacement
        // failure demonstrates fail-open."
        var faultAt = t;
        coordinator.ObserveOutputStatus(RelayOutputStatus.Running, faultAt); // pump was already running
        var s5State = coordinator.ObserveOutputStatus(RelayOutputStatus.Faulted, faultAt);
        Assert.Equal(RelayState.FailOpen, s5State);
        var faultDiagnostics = coordinator.LastFault!;
        var faultEvent = recorder.RecordFault(faultDiagnostics, s5State);
        // Evidence at this tick would authorize replacement, but the active fault must win: the
        // decision is recorded as not executed even though policy still says Authorize.
        var faultedFusion = Agreed(BroadcastState.Commercial, 0.9);
        var faultedPolicyResult = policy.Evaluate(faultedFusion);
        Assert.Equal(ReplacementDecision.AuthorizeReplacement, faultedPolicyResult.Decision);
        var s5 = Segment(faultedFusion);
        Assert.Equal(RelayState.FailOpen, s5);

        // 6. Explicit, deterministic recovery.
        var s6 = coordinator.Recover(t);
        Assert.Equal(RelayState.Original, s6);
        var recoveryEvent = recorder.RecordRecovery(t, s6, correlationId: faultEvent.CorrelationId);
        Assert.Equal(faultEvent.CorrelationId, recoveryEvent.CorrelationId);

        // 7. Back to normal operation: high-confidence Commercial re-authorizes and re-enters
        // replacement (2 fresh confirmations, since recovery reset entry progress via Original).
        var s7 = Segment(Agreed(BroadcastState.Commercial, 0.9));
        Assert.Equal(RelayState.EnteringReplacement, s7);
        var s8 = Segment(Agreed(BroadcastState.Commercial, 0.9));
        Assert.Equal(RelayState.Replacement, s8);

        // 8. Return to program: first non-authorize tick starts the return, second completes it.
        // Acceptance: "return to program within defined tolerance" -- the state machine's
        // documented fast-exit bound is at most two ticks from Replacement, verified here rather
        // than assumed.
        var returnStart = t;
        var s9 = Segment(Agreed(BroadcastState.Program, 0.95));
        Assert.Equal(RelayState.ReturningToOriginal, s9);
        var s10 = Segment(Agreed(BroadcastState.Program, 0.95));
        Assert.Equal(RelayState.Original, s10);
        var returnDuration = t - returnStart;
        Assert.True(returnDuration <= SegmentDuration * 2, $"Return to Original took {returnDuration}, expected at most {SegmentDuration * 2}.");

        // -- Output timeline machine-verified: the full recorded decision trace is asserted
        // against the expected sequence in one pass, not spot-checked. --
        var actualStates = recorder.DecisionEvents.Select(e => e.RelayState).ToArray();
        RelayState[] expectedStates =
        [
            RelayState.Original,             // 1
            RelayState.Original,             // 2 (disputed)
            RelayState.EnteringReplacement,  // 3
            RelayState.Replacement,          // 4
            RelayState.FailOpen,             // 5 (fault wins over concurrent authorize)
            RelayState.EnteringReplacement,  // 7
            RelayState.Replacement,          // 8
            RelayState.ReturningToOriginal,  // 9
            RelayState.Original,             // 10
        ];
        Assert.Equal(expectedStates, actualStates);

        // Denied vs executed is distinguishable across the whole trace: segment 5's decision was
        // authorized by policy but not executed because of the active fault.
        var executedFlags = recorder.DecisionEvents.Select(e => e.Executed).ToArray();
        bool[] expectedExecuted = [false, false, false, true, false, false, true, false, false];
        Assert.Equal(expectedExecuted, executedFlags);

        // The scheduled replacement clip from segment 4 is captured in that event's telemetry.
        var replacementEvent = recorder.DecisionEvents.ElementAt(3);
        Assert.NotNull(replacementEvent.SelectedScheduleSummary);
        Assert.Contains("Fit", replacementEvent.SelectedScheduleSummary);

        // Exactly one fault event and one recovery event were recorded.
        Assert.Single(recorder.FaultEvents, e => e.Kind == RelayFaultTelemetryKind.Fault);
        Assert.Single(recorder.FaultEvents, e => e.Kind == RelayFaultTelemetryKind.Recovery);
    }

    /// <summary>
    /// Grounds the "delayed relay" half of the end-to-end claim: the delayed original output
    /// (#44/#45) independently delivers the full source stream, unaffected by whatever switching
    /// decisions the state machine above it is making. Reuses the same recorded fixture and
    /// pattern already proven in #45's own tests.
    /// </summary>
    [Fact]
    public async Task DelayedOriginalOutput_DeliversFullSourceStream_IndependentOfSwitchingDecisions()
    {
        var filePath = GetFixturePath("black-with-tone.ts");
        var expectedBytes = new FileInfo(filePath).Length;
        var source = new RecordedFileMediaSource(
            "e2e-source", filePath, chunkSizeBytes: 4096, chunkInterval: TimeSpan.FromMilliseconds(10));
        await using var output = new ContinuousRelayOutput(
            source, delay: TimeSpan.FromMilliseconds(30), maxBufferedDuration: TimeSpan.FromSeconds(10),
            maxBufferedBytes: 2_000_000, pollInterval: TimeSpan.FromMilliseconds(5));
        output.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        long totalBytes = 0;
        await foreach (var chunk in output.Subscribe(cts.Token))
        {
            totalBytes += chunk.Data.Length;
        }

        Assert.Equal(RelayOutputStatus.Completed, output.Status);
        Assert.Equal(expectedBytes, totalBytes);
    }
}
