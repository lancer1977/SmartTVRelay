namespace SmartTVRelay.Core.Fixtures;

/// <summary>
/// Signed timing error between one <see cref="ExpectedSegment"/> and the <see cref="PredictedSegment"/>
/// matched to it (the same-state predicted segment with the greatest time overlap). Positive
/// <see cref="StartError"/> means the prediction started late; negative means it started early.
/// Positive <see cref="EndError"/> means the prediction ended late (over-extended); negative means
/// it ended early (cut short). A segment with no matching prediction at all has no entry here --
/// that is a full miss, reflected in <see cref="ScoringReport.MissedCommercialDuration"/> and
/// <see cref="ScoringReport.ConfusionCounts"/> instead.
/// </summary>
public sealed record SegmentBoundaryError(
    ExpectedSegment Expected,
    PredictedSegment Predicted,
    TimeSpan StartError,
    TimeSpan EndError);

/// <summary>
/// Result of scoring a predicted broadcast-state timeline against labeled ground truth (#24).
/// Every field is a pure function of the two input timelines -- <see
/// cref="BroadcastScoringEngine.Score"/> has no randomness and no external state, so the same
/// inputs always produce the same report.
/// </summary>
/// <param name="CommercialPrecision">Of all time predicted as Commercial, the duration-weighted
/// fraction that was truly Commercial. <see langword="null"/> when nothing was ever predicted as
/// Commercial (undefined, not zero -- a policy that never authorizes replacement has no false
/// positives to be penalized for, but reporting 0 here would misleadingly suggest it does).</param>
/// <param name="CommercialRecall">Of all time truly Commercial, the duration-weighted fraction that
/// was predicted as Commercial. <see langword="null"/> when the expected timeline contains no
/// Commercial time at all.</param>
/// <param name="BoundaryErrors">Start/end timing error for each expected segment that has a
/// matching same-state predicted segment.</param>
/// <param name="MissedCommercialDuration">Total duration where truth was Commercial but the
/// prediction was not -- content that should have been replaced but wasn't.</param>
/// <param name="FalseReplacementCount">Number of distinct contiguous runs where the prediction was
/// Commercial but truth was not -- real programming that would have been incorrectly replaced.
/// Reported as its own top-level field (not buried inside <see cref="ConfusionCounts"/>) because
/// AGENTS.md treats this as the single most severe failure mode: "false replacement of real
/// programming is a more severe failure than failing to suppress a commercial."</param>
/// <param name="FalseReplacementDuration">Total duration across all false-replacement runs.</param>
/// <param name="ConfusionCounts">Count of atomic timeline intervals for each (expected, predicted)
/// state pair. An interval with no covering predicted segment counts as
/// <see cref="BroadcastState.Unknown"/> predicted -- "no decision was made here" is itself a
/// meaningful outcome to track, distinct from an active wrong prediction.</param>
public sealed record ScoringReport(
    double? CommercialPrecision,
    double? CommercialRecall,
    IReadOnlyList<SegmentBoundaryError> BoundaryErrors,
    TimeSpan MissedCommercialDuration,
    int FalseReplacementCount,
    TimeSpan FalseReplacementDuration,
    IReadOnlyDictionary<(BroadcastState Expected, BroadcastState Predicted), int> ConfusionCounts);
