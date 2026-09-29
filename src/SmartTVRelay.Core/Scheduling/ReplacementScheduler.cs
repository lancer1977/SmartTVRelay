using SmartTVRelay.Core.Catalog;

namespace SmartTVRelay.Core.Scheduling;

/// <summary>How a scheduling attempt resolved.</summary>
public enum SchedulingOutcome
{
    /// <summary>One or more clips were selected whose total duration is within tolerance of the target.</summary>
    Fit,

    /// <summary>Candidates existed, but no combination stays within <c>target + tolerance</c> --
    /// even the smallest enabled candidate alone is too long.</summary>
    NoFit,

    /// <summary>No enabled candidates were supplied at all.</summary>
    NoMedia,
}

/// <summary>
/// The result of a scheduling attempt: which clips (if any) were chosen, their combined duration,
/// and why. An empty <see cref="SelectedClips"/> list always pairs with <see cref="NoFit"/> or
/// <see cref="NoMedia"/> -- never <see cref="Fit"/> with nothing selected.
/// </summary>
public sealed record ReplacementSchedule(
    SchedulingOutcome Outcome,
    IReadOnlyList<ReplacementClipEntry> SelectedClips,
    TimeSpan TotalDuration,
    TimeSpan TargetDuration);

/// <summary>
/// Selects replacement clips whose combined duration approximates a target break duration, without
/// controlling the relay itself -- scheduling only, per the issue's agent boundary. This class does
/// not decide *when* to switch or *whether* replacement is authorized; that is
/// <c>BroadcastReplacementPolicy</c>'s responsibility elsewhere in this codebase.
/// </summary>
public sealed class ReplacementScheduler
{
    private static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Selects clips from <paramref name="candidates"/> (only those with <c>Enabled == true</c> are
    /// considered) whose combined duration stays within <c>targetDuration + tolerance</c>, greedily
    /// preferring longer clips first to minimize the clip count, and continuing to add shorter
    /// clips until the total lands within <c>[targetDuration - tolerance, targetDuration + tolerance]</c>
    /// or candidates run out.
    /// </summary>
    /// <param name="targetDuration">The estimated break duration to fill.</param>
    /// <param name="candidates">Candidate clips, typically a <see cref="CatalogScanResult.Entries"/>.</param>
    /// <param name="tolerance">
    /// How much the total may fall short of or exceed <paramref name="targetDuration"/> and still
    /// count as a fit. Defaults to 1 second when omitted.
    /// </param>
    /// <param name="seed">
    /// Optional seed controlling tie-break order among clips of otherwise-equal standing (see
    /// remarks). Omitting it is equivalent to passing 0 -- scheduling is always deterministic for a
    /// given seed, never wall-clock- or Guid-based.
    /// </param>
    /// <remarks>
    /// Without a seed, candidates of equal duration would always be chosen in the same catalog
    /// order, so the same handful of clips would be favored on every call. To spread wear across an
    /// equally-suitable catalog while remaining fully reproducible for a given seed, candidates are
    /// first shuffled with a <see cref="Random"/> seeded from <paramref name="seed"/> (default 0),
    /// then stably sorted by duration descending -- so the shuffle only affects ordering among
    /// clips the duration sort would otherwise consider ties.
    /// </remarks>
    public ReplacementSchedule Schedule(
        TimeSpan targetDuration,
        IReadOnlyList<ReplacementClipEntry> candidates,
        TimeSpan? tolerance = null,
        int? seed = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var effectiveTolerance = tolerance ?? DefaultTolerance;
        var enabled = candidates.Where(c => c.Enabled).ToList();

        if (enabled.Count == 0)
        {
            return new ReplacementSchedule(SchedulingOutcome.NoMedia, [], TimeSpan.Zero, targetDuration);
        }

        var maxAllowed = targetDuration + effectiveTolerance;
        var minAcceptable = targetDuration - effectiveTolerance;

        var rng = new Random(seed ?? 0);
        var ordered = enabled
            .OrderBy(_ => rng.Next())
            .OrderByDescending(c => c.Duration)
            .ToList();

        var selected = new List<ReplacementClipEntry>();
        var total = TimeSpan.Zero;

        foreach (var clip in ordered)
        {
            if (total + clip.Duration > maxAllowed)
            {
                continue;
            }

            selected.Add(clip);
            total += clip.Duration;

            if (total >= minAcceptable)
            {
                break;
            }
        }

        if (selected.Count == 0)
        {
            return new ReplacementSchedule(SchedulingOutcome.NoFit, [], TimeSpan.Zero, targetDuration);
        }

        return new ReplacementSchedule(SchedulingOutcome.Fit, selected, total, targetDuration);
    }
}
