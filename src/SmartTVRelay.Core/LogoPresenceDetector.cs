namespace SmartTVRelay.Core;

using Observation.Core;

/// <summary>
/// Observes presence/absence of a configured station/network logo (bug) from pre-computed
/// template-match scores. Networks conventionally pull their bug during commercial breaks and
/// restore it for programming, so this detector treats a clearly PRESENT logo as evidence leaning
/// toward <see cref="BroadcastState.Program"/> and a clearly ABSENT logo as evidence leaning toward
/// <see cref="BroadcastState.Commercial"/>.
/// </summary>
/// <remarks>
/// This is an observer only: per AGENTS.md's layering rule it never calls into
/// <c>BroadcastStateFusion</c> or <c>BroadcastReplacementPolicy</c> and never decides replacement
/// eligibility -- it only emits confidence-bearing <see cref="Observation{T}"/> evidence.
/// </remarks>
public sealed class LogoPresenceDetector
{
    /// <summary>
    /// Default match-score threshold above which a logo is considered present (before applying the
    /// ambiguity margin). Chosen so that template-match noise near 0.5 (a coin-flip match) does not,
    /// by itself, register as a confident presence signal.
    /// </summary>
    public const double DefaultPresenceThreshold = 0.65;

    /// <summary>
    /// Default half-width of the ambiguity band straddling <see cref="DefaultPresenceThreshold"/>.
    /// Match scores inside the band are treated as occluded/noisy and produce no observation.
    /// </summary>
    public const double DefaultAmbiguityMargin = 0.15;

    private const string SourceKind = "logo-presence";

    /// <summary>
    /// The minimum confidence emitted for any qualifying observation -- assigned right at the edge
    /// of the ambiguity band, where the detector is only barely more sure than not.
    /// </summary>
    private const double MinEmittedConfidence = 0.5;

    /// <summary>
    /// The maximum confidence emitted, reserved for a match score at the extreme end of its range
    /// (1.0 for presence, 0.0 for absence). Capped below 1.0 because this is still a single cheap
    /// visual signal, not a certainty -- fusion/policy still decide final replacement eligibility.
    /// </summary>
    private const double MaxEmittedConfidence = 0.95;

    private readonly double upperBound;
    private readonly double lowerBound;

    public LogoPresenceDetector(
        string logoReferenceId,
        double presenceThreshold = DefaultPresenceThreshold,
        double ambiguityMargin = DefaultAmbiguityMargin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logoReferenceId);

        if (!double.IsFinite(presenceThreshold) || presenceThreshold < 0 || presenceThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(presenceThreshold));
        }

        if (!double.IsFinite(ambiguityMargin) || ambiguityMargin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ambiguityMargin));
        }

        LogoReferenceId = logoReferenceId;
        PresenceThreshold = presenceThreshold;
        AmbiguityMargin = ambiguityMargin;

        upperBound = Math.Clamp(presenceThreshold + ambiguityMargin, 0.0, 1.0);
        lowerBound = Math.Clamp(presenceThreshold - ambiguityMargin, 0.0, 1.0);
    }

    /// <summary>
    /// The opaque configured ROI/template/reference identifier this detector matches against (for
    /// example a region descriptor or logo-template id). Stored as part of the detector's identity;
    /// this detector does not perform image matching itself -- that happens upstream.
    /// </summary>
    public string LogoReferenceId { get; }

    /// <summary>The configured match-score threshold at the center of the ambiguity band.</summary>
    public double PresenceThreshold { get; }

    /// <summary>The configured half-width of the ambiguity band around <see cref="PresenceThreshold"/>.</summary>
    public double AmbiguityMargin { get; }

    public IReadOnlyList<Observation<BroadcastState>> Detect(string sourceId, IReadOnlyList<LogoMatchSample> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(samples);

        var scope = ObservationScope.Of(sourceId);
        var observations = new List<Observation<BroadcastState>>();

        foreach (var sample in samples)
        {
            if (sample.MatchScore >= upperBound)
            {
                // Logo clearly present -> lean Program. Confidence grows from MinEmittedConfidence
                // right at the band edge up to MaxEmittedConfidence at a perfect match.
                var confidence = ScaleConfidence(sample.MatchScore - upperBound, 1.0 - upperBound);
                observations.Add(new Observation<BroadcastState>(
                    Guid.NewGuid(),
                    scope,
                    BroadcastObservations.Subject,
                    BroadcastObservations.Predicate,
                    BroadcastState.Program,
                    sourceId,
                    SourceKind,
                    confidence,
                    sample.CapturedAt));
            }
            else if (sample.MatchScore <= lowerBound)
            {
                // Logo clearly absent -> lean Commercial (networks conventionally pull their bug
                // during breaks). Confidence grows the further the score falls below the band edge.
                var confidence = ScaleConfidence(lowerBound - sample.MatchScore, lowerBound);
                observations.Add(new Observation<BroadcastState>(
                    Guid.NewGuid(),
                    scope,
                    BroadcastObservations.Subject,
                    BroadcastObservations.Predicate,
                    BroadcastState.Commercial,
                    sourceId,
                    SourceKind,
                    confidence,
                    sample.CapturedAt));
            }

            // Otherwise the match score falls inside the ambiguity band (occluded/noisy input):
            // emit nothing. Per AGENTS.md's prime safety rule, disputed/uncertain evidence must not
            // push toward a decision -- abstaining here is stronger than emitting a low-confidence
            // observation, since a pile of weak-but-present observations could still tip a fusion
            // margin the wrong way. No evidence is the safest evidence in this band.
        }

        return observations;
    }

    private static double ScaleConfidence(double numerator, double denominator)
    {
        if (denominator <= 0)
        {
            return MaxEmittedConfidence;
        }

        var ratio = Math.Clamp(numerator / denominator, 0.0, 1.0);
        return MinEmittedConfidence + (ratio * (MaxEmittedConfidence - MinEmittedConfidence));
    }
}
