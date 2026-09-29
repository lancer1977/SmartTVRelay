namespace SmartTVRelay.Core.Fixtures;

/// <summary>A correction that could not be applied, and why.</summary>
public sealed record CorrectionConflict(LabelCorrection Correction, string Reason);

/// <summary>
/// Result of replaying a correction log against a baseline <see cref="ExpectedSegment"/> timeline.
/// <see cref="ResultingTimeline"/> reflects every correction that applied cleanly; every correction
/// that didn't is reported in <see cref="Conflicts"/> instead of being partially applied or throwing.
/// </summary>
public sealed record CorrectionApplyResult(
    IReadOnlyList<ExpectedSegment> ResultingTimeline,
    IReadOnlyList<CorrectionConflict> Conflicts);

/// <summary>
/// Replays a <see cref="LabelCorrection"/> log against a baseline labeled timeline (#25). Pure and
/// deterministic: corrections are applied strictly in list order, with no wall-clock or randomness
/// involved, so the same baseline and the same correction list always produce the same result. Never
/// touches raw media or emissions -- only the <see cref="ExpectedSegment"/> timeline.
/// </summary>
public static class LabelCorrectionApplier
{
    public static CorrectionApplyResult Apply(IReadOnlyList<ExpectedSegment> baseline, IReadOnlyList<LabelCorrection> corrections)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(corrections);

        var current = baseline.OrderBy(s => s.Start).ToList();
        var conflicts = new List<CorrectionConflict>();
        var appliedIds = new HashSet<Guid>();

        foreach (var correction in corrections)
        {
            // Idempotent replay: the same correction Id seen again (e.g. an at-least-once
            // correction log re-delivering an entry) is a silent no-op, not a second application
            // and not a conflict -- "already applied" is a success state, not a failure one.
            if (!appliedIds.Add(correction.Id))
            {
                continue;
            }

            switch (correction.Operation)
            {
                case CorrectionOperation.Add:
                    ApplyAdd(current, correction, conflicts);
                    break;
                case CorrectionOperation.Remove:
                    ApplyRemove(current, correction, conflicts);
                    break;
                case CorrectionOperation.Change:
                    ApplyChange(current, correction, conflicts);
                    break;
            }
        }

        return new CorrectionApplyResult(current, conflicts);
    }

    private static void ApplyAdd(List<ExpectedSegment> current, LabelCorrection correction, List<CorrectionConflict> conflicts)
    {
        var segment = correction.Replacement!;

        if (OverlapsAny(current, segment))
        {
            conflicts.Add(new CorrectionConflict(correction, $"Add segment [{segment.Start}, {segment.End}) overlaps an existing segment."));
            return;
        }

        Insert(current, segment);
    }

    private static void ApplyRemove(List<ExpectedSegment> current, LabelCorrection correction, List<CorrectionConflict> conflicts)
    {
        var target = correction.Target!;
        var index = current.IndexOf(target);

        if (index < 0)
        {
            conflicts.Add(new CorrectionConflict(correction, $"Remove target [{target.Start}, {target.End}) is not present in the current timeline."));
            return;
        }

        current.RemoveAt(index);
    }

    private static void ApplyChange(List<ExpectedSegment> current, LabelCorrection correction, List<CorrectionConflict> conflicts)
    {
        var target = correction.Target!;
        var replacement = correction.Replacement!;
        var index = current.IndexOf(target);

        if (index < 0)
        {
            conflicts.Add(new CorrectionConflict(correction, $"Change target [{target.Start}, {target.End}) is not present in the current timeline."));
            return;
        }

        // Check the replacement against every OTHER segment, not the target itself, and don't
        // mutate `current` until we know the change is valid -- a Change that fails must leave
        // the original target in place, never remove-then-fail-to-add.
        if (current.Where((_, i) => i != index).Any(other => Overlaps(other, replacement)))
        {
            conflicts.Add(new CorrectionConflict(correction, $"Change replacement [{replacement.Start}, {replacement.End}) would overlap another segment."));
            return;
        }

        current.RemoveAt(index);
        Insert(current, replacement);
    }

    private static void Insert(List<ExpectedSegment> current, ExpectedSegment segment)
    {
        var insertAt = current.FindIndex(s => s.Start > segment.Start);
        if (insertAt < 0)
        {
            current.Add(segment);
        }
        else
        {
            current.Insert(insertAt, segment);
        }
    }

    private static bool OverlapsAny(IReadOnlyList<ExpectedSegment> segments, ExpectedSegment candidate) =>
        segments.Any(existing => Overlaps(existing, candidate));

    private static bool Overlaps(ExpectedSegment a, ExpectedSegment b) =>
        a.Start < b.End && b.Start < a.End;
}
