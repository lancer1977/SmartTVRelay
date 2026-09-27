namespace SmartTVRelay.Core.Fixtures;

using System.Linq;
using Observation.Core;

public sealed class BroadcastFixtureHarness
{
    private static readonly TimeSpan DefaultEvidenceWindow = TimeSpan.FromSeconds(5);

    private readonly BroadcastStateFusion fusion;
    private readonly BroadcastReplacementPolicy policy;
    private readonly TimeSpan evidenceWindow;

    public BroadcastFixtureHarness(
        BroadcastStateFusion? fusion = null,
        BroadcastReplacementPolicy? policy = null,
        TimeSpan? evidenceWindow = null)
    {
        this.fusion = fusion ?? new BroadcastStateFusion();
        this.policy = policy ?? new BroadcastReplacementPolicy();
        this.evidenceWindow = evidenceWindow ?? DefaultEvidenceWindow;
    }

    public FixtureReport Run(BroadcastFixture fixture)
    {
        var scope = ObservationScope.Of(fixture.Scope);
        var evaluations = new List<SegmentEvaluation>();

        foreach (var segment in fixture.ExpectedTimeline)
        {
            // Only recent evidence is visible: at or before the segment start (no
            // look-ahead), and no older than `evidenceWindow`. Without a recency
            // bound, ObservationFusion's unweighted lifetime average never lets
            // later evidence override an earlier confident reading -- a state
            // that held confidently for a while would make it mathematically
            // impossible for fusion to ever resolve to a different state later,
            // however much new evidence arrives, which contradicts AGENTS.md's
            // own treatment of stale evidence as something that must not carry
            // authority into a current decision.
            var windowStart = segment.Start - evidenceWindow;
            var observations = fixture.Emissions
                .Where(e => e.At <= segment.Start && e.At > windowStart)
                .Select(e => ToObservation(scope, e))
                .ToArray();

            var results = fusion.Fuse(observations);
            var fusionResult = results.FirstOrDefault(r => r.Scope == scope)
                ?? new FusionResult<BroadcastState>(
                    scope,
                    BroadcastObservations.Subject,
                    BroadcastObservations.Predicate,
                    default,
                    0,
                    FusionStatus.Unknown,
                    Array.Empty<Guid>(),
                    Array.Empty<FusionCandidate<BroadcastState>>(),
                    Array.Empty<Guid>());

            var policyResult = policy.Evaluate(fusionResult);
            var outcome = DetermineOutcome(policyResult.Decision, segment.ExpectedState);
            evaluations.Add(new SegmentEvaluation(segment, policyResult, outcome));
        }

        return new FixtureReport(fixture.Name, evaluations);
    }

    private static SegmentOutcome DetermineOutcome(ReplacementDecision decision, BroadcastState expected)
    {
        if (decision == ReplacementDecision.AuthorizeReplacement && expected != BroadcastState.Commercial)
        {
            return SegmentOutcome.FalseReplacement;
        }

        if (decision == ReplacementDecision.PreserveOriginal && expected == BroadcastState.Commercial)
        {
            return SegmentOutcome.MissedCommercial;
        }

        return SegmentOutcome.Correct;
    }

    private static Observation<BroadcastState> ToObservation(ObservationScope scope, DetectorEmission emission)
        => new(
            emission.Id,
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            emission.Value,
            emission.SourceId,
            emission.SourceKind,
            emission.Confidence,
            DateTimeOffset.UnixEpoch + emission.At);
}
