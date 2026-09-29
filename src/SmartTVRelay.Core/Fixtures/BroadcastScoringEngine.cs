namespace SmartTVRelay.Core.Fixtures;

using System.Linq;

/// <summary>
/// Scores a predicted broadcast-state timeline against labeled ground truth (#24). Pure
/// computation over the two input timelines -- no scoring/ingest side effects, no detector logic,
/// no randomness -- so the same inputs always produce the same <see cref="ScoringReport"/>.
/// </summary>
public static class BroadcastScoringEngine
{
    public static ScoringReport Score(IReadOnlyList<ExpectedSegment> expected, IReadOnlyList<PredictedSegment> predicted)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(predicted);

        var confusionCounts = new Dictionary<(BroadcastState Expected, BroadcastState Predicted), int>();

        if (expected.Count == 0)
        {
            return new ScoringReport(null, null, Array.Empty<SegmentBoundaryError>(), TimeSpan.Zero, 0, TimeSpan.Zero, confusionCounts);
        }

        // Ground truth defines the span being scored: a gap in predicted coverage within that span
        // is meaningful (no decision was made -- scored as BroadcastState.Unknown predicted), but a
        // stretch outside the expected timeline entirely has no ground truth to score against at
        // all, so it's excluded rather than guessed at.
        var timelineStart = expected.Min(s => s.Start);
        var timelineEnd = expected.Max(s => s.End);

        var boundaries = new SortedSet<TimeSpan> { timelineStart, timelineEnd };
        foreach (var segment in expected)
        {
            AddIfWithin(boundaries, segment.Start, timelineStart, timelineEnd);
            AddIfWithin(boundaries, segment.End, timelineStart, timelineEnd);
        }

        foreach (var segment in predicted)
        {
            AddIfWithin(boundaries, segment.Start, timelineStart, timelineEnd);
            AddIfWithin(boundaries, segment.End, timelineStart, timelineEnd);
        }

        var ordered = boundaries.ToList();

        var commercialPredictedDuration = TimeSpan.Zero;
        var commercialTruePositiveDuration = TimeSpan.Zero;
        var commercialExpectedDuration = TimeSpan.Zero;
        var missedCommercialDuration = TimeSpan.Zero;
        var falseReplacementDuration = TimeSpan.Zero;
        var falseReplacementCount = 0;
        var inFalseReplacementRun = false;

        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var start = ordered[i];
            var end = ordered[i + 1];
            if (start >= end)
            {
                continue; // Duplicate boundary collapsed to a zero-length interval; nothing to score.
            }

            var expectedState = FindExpectedStateAt(expected, start);
            if (expectedState is null)
            {
                // A gap in ground-truth coverage within [timelineStart, timelineEnd) -- can happen
                // if the fixture's expected segments aren't fully contiguous. Nothing to score, and
                // it breaks a false-replacement run in progress (there's no ground truth to call it
                // a "false" anything against).
                inFalseReplacementRun = false;
                continue;
            }

            var predictedState = FindPredictedStateAt(predicted, start) ?? BroadcastState.Unknown;
            var duration = end - start;

            var key = (expectedState.Value, predictedState);
            confusionCounts[key] = confusionCounts.GetValueOrDefault(key) + 1;

            if (predictedState == BroadcastState.Commercial)
            {
                commercialPredictedDuration += duration;
                if (expectedState == BroadcastState.Commercial)
                {
                    commercialTruePositiveDuration += duration;
                }
            }

            if (expectedState == BroadcastState.Commercial)
            {
                commercialExpectedDuration += duration;
                if (predictedState != BroadcastState.Commercial)
                {
                    missedCommercialDuration += duration;
                }
            }

            // "False replacement": the prediction authorized replacing content that truth says was
            // not a commercial -- AGENTS.md's most severe failure mode. Adjacent false-replacement
            // intervals (however many atomic boundaries split them) are merged into one run, so this
            // count reflects distinct incidents, not an artifact of how finely the timeline happened
            // to be sliced.
            var isFalseReplacement = predictedState == BroadcastState.Commercial && expectedState != BroadcastState.Commercial;
            if (isFalseReplacement)
            {
                falseReplacementDuration += duration;
                if (!inFalseReplacementRun)
                {
                    falseReplacementCount++;
                    inFalseReplacementRun = true;
                }
            }
            else
            {
                inFalseReplacementRun = false;
            }
        }

        // Duration-weighted, not segment-count-weighted: this domain is inherently about
        // continuous time, and a count-based precision/recall would treat a segment that's 99%
        // right the same as one that's completely wrong, losing exactly the granularity that
        // BoundaryErrors exists to capture separately.
        double? precision = commercialPredictedDuration > TimeSpan.Zero
            ? commercialTruePositiveDuration.TotalSeconds / commercialPredictedDuration.TotalSeconds
            : null;
        double? recall = commercialExpectedDuration > TimeSpan.Zero
            ? commercialTruePositiveDuration.TotalSeconds / commercialExpectedDuration.TotalSeconds
            : null;

        var boundaryErrors = ComputeBoundaryErrors(expected, predicted);

        return new ScoringReport(precision, recall, boundaryErrors, missedCommercialDuration, falseReplacementCount, falseReplacementDuration, confusionCounts);
    }

    private static IReadOnlyList<SegmentBoundaryError> ComputeBoundaryErrors(IReadOnlyList<ExpectedSegment> expected, IReadOnlyList<PredictedSegment> predicted)
    {
        var errors = new List<SegmentBoundaryError>();

        foreach (var segment in expected)
        {
            PredictedSegment? best = null;
            var bestOverlap = TimeSpan.Zero;

            foreach (var candidate in predicted)
            {
                if (candidate.PredictedState != segment.ExpectedState)
                {
                    continue;
                }

                var overlapStart = candidate.Start > segment.Start ? candidate.Start : segment.Start;
                var overlapEnd = candidate.End < segment.End ? candidate.End : segment.End;
                if (overlapEnd <= overlapStart)
                {
                    continue; // No actual time overlap with this expected segment.
                }

                var overlap = overlapEnd - overlapStart;
                if (best is null || overlap > bestOverlap)
                {
                    best = candidate;
                    bestOverlap = overlap;
                }
            }

            if (best is not null)
            {
                errors.Add(new SegmentBoundaryError(segment, best, best.Start - segment.Start, best.End - segment.End));
            }
        }

        return errors;
    }

    private static void AddIfWithin(SortedSet<TimeSpan> boundaries, TimeSpan value, TimeSpan lowerExclusive, TimeSpan upperExclusive)
    {
        if (value > lowerExclusive && value < upperExclusive)
        {
            boundaries.Add(value);
        }
    }

    private static BroadcastState? FindExpectedStateAt(IReadOnlyList<ExpectedSegment> segments, TimeSpan at)
    {
        foreach (var segment in segments)
        {
            if (at >= segment.Start && at < segment.End)
            {
                return segment.ExpectedState;
            }
        }

        return null;
    }

    private static BroadcastState? FindPredictedStateAt(IReadOnlyList<PredictedSegment> segments, TimeSpan at)
    {
        foreach (var segment in segments)
        {
            if (at >= segment.Start && at < segment.End)
            {
                return segment.PredictedState;
            }
        }

        return null;
    }
}
