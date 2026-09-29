namespace SmartTVRelay.Core.Fixtures;

/// <summary>
/// One segment of a predicted broadcast-state timeline, as produced by whatever consumes fusion +
/// policy output over time (e.g. a future streaming harness). Kept distinct from
/// <see cref="ExpectedSegment"/> -- which always represents ground truth -- so <see
/// cref="BroadcastScoringEngine"/> can score any predicted timeline against any expected timeline
/// without depending on how the prediction was produced (#24's own agent boundary: scoring only,
/// no ingest/detector/harness changes).
/// </summary>
public sealed record PredictedSegment(TimeSpan Start, TimeSpan End, BroadcastState PredictedState);
