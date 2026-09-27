namespace SmartTVRelay.Core.Fixtures;

public sealed record SegmentEvaluation(
    ExpectedSegment Segment,
    ReplacementPolicyResult PolicyResult,
    SegmentOutcome Outcome);
