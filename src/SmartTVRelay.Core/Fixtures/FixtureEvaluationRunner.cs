namespace SmartTVRelay.Core.Fixtures;

/// <summary>
/// Bridges a <see cref="BroadcastFixture"/> to a <see cref="ScoringReport"/> (#27) by running it
/// through the existing <see cref="BroadcastFixtureHarness"/> (#22) -- which already knows how to
/// turn a fixture's emissions into one policy decision per expected segment -- and reinterpreting
/// each decision's <see cref="ReplacementPolicyResult.EffectiveState"/> as the predicted state for
/// that segment's whole time range, then scoring with <see cref="BroadcastScoringEngine"/> (#24).
/// Pure composition of existing pieces -- no new fusion/policy/scoring logic -- per #27's "CI
/// integration only" agent boundary.
/// </summary>
public static class FixtureEvaluationRunner
{
    public static ScoringReport Evaluate(BroadcastFixture fixture, BroadcastFixtureHarness? harness = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        harness ??= new BroadcastFixtureHarness();
        var report = harness.Run(fixture);

        var predicted = report.Segments
            .Select(s => new PredictedSegment(s.Segment.Start, s.Segment.End, s.PolicyResult.EffectiveState))
            .ToList();

        return BroadcastScoringEngine.Score(fixture.ExpectedTimeline, predicted);
    }
}
