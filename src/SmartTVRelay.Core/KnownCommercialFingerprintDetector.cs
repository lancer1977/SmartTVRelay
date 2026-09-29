namespace SmartTVRelay.Core;

using Observation.Core;

/// <summary>
/// Recognizes previously known commercials from pre-computed audio fingerprints (tier 2 evidence per
/// docs/detector-backlog.md). Looks each sample's fingerprint up in an <see cref="IKnownCommercialFingerprintStore"/>
/// and classifies the closest match into one of three outcomes:
/// <list type="bullet">
/// <item>known match (distance ratio &lt;= <see cref="DefaultKnownMatchMaxDistanceRatio"/>): high confidence, emits <see cref="BroadcastState.Commercial"/>.</item>
/// <item>near miss (distance ratio between the known-match and near-miss thresholds): lower confidence, still emits <see cref="BroadcastState.Commercial"/> -- see the near-miss threshold note below.</item>
/// <item>unknown (distance ratio above <see cref="DefaultNearMissMaxDistanceRatio"/>, or no comparable entry): no observation, matching how <see cref="BlackFrameDetector"/> emits nothing for non-qualifying samples.</item>
/// </list>
/// Near-miss design decision: this detector still emits a (lower-confidence) observation for a near miss
/// rather than staying silent, because a weak-but-real fingerprint match is still useful corroborating
/// evidence for fusion to combine with other detectors -- the prime safety rule in AGENTS.md ("when
/// evidence is unknown, disputed, stale, conflicting, or below policy threshold, preserve the original
/// broadcast") is enforced downstream by policy/fusion using the emitted confidence, not by this detector
/// suppressing borderline evidence outright.
/// </summary>
public sealed class KnownCommercialFingerprintDetector
{
    /// <summary>Distance ratios at or below this are classified as a known match.</summary>
    public const double DefaultKnownMatchMaxDistanceRatio = 0.10;

    /// <summary>Distance ratios at or below this (but above the known-match threshold) are classified as a near miss. Above this is unknown.</summary>
    public const double DefaultNearMissMaxDistanceRatio = 0.35;

    private const string SourceKind = "commercial-fingerprint";
    private const double KnownMatchConfidence = 0.9;
    private const double NearMissConfidence = 0.4;

    private readonly IKnownCommercialFingerprintStore store;
    private readonly double knownMatchMaxDistanceRatio;
    private readonly double nearMissMaxDistanceRatio;

    public KnownCommercialFingerprintDetector(
        IKnownCommercialFingerprintStore store,
        double knownMatchMaxDistanceRatio = DefaultKnownMatchMaxDistanceRatio,
        double nearMissMaxDistanceRatio = DefaultNearMissMaxDistanceRatio)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (!double.IsFinite(knownMatchMaxDistanceRatio) || knownMatchMaxDistanceRatio < 0 || knownMatchMaxDistanceRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(knownMatchMaxDistanceRatio));
        }

        if (!double.IsFinite(nearMissMaxDistanceRatio) || nearMissMaxDistanceRatio < knownMatchMaxDistanceRatio || nearMissMaxDistanceRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(nearMissMaxDistanceRatio));
        }

        this.store = store;
        this.knownMatchMaxDistanceRatio = knownMatchMaxDistanceRatio;
        this.nearMissMaxDistanceRatio = nearMissMaxDistanceRatio;
    }

    public IReadOnlyList<Observation<BroadcastState>> Detect(string sourceId, IReadOnlyList<AudioFingerprintSample> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(samples);

        var scope = ObservationScope.Of(sourceId);
        var observations = new List<Observation<BroadcastState>>();

        foreach (var sample in samples)
        {
            if (sample.Hash is null || sample.Hash.Count == 0)
            {
                // A degenerate/empty fingerprint carries no evidence -- skip it rather than treat it as a
                // store lookup error, consistent with other detectors simply not emitting for a
                // non-qualifying sample.
                continue;
            }

            var match = store.FindClosestMatch(sample.Hash);
            if (match is null)
            {
                continue;
            }

            double? confidence = match.DistanceRatio switch
            {
                var ratio when ratio <= knownMatchMaxDistanceRatio => KnownMatchConfidence,
                var ratio when ratio <= nearMissMaxDistanceRatio => NearMissConfidence,
                _ => null,
            };

            if (confidence is null)
            {
                continue;
            }

            observations.Add(new Observation<BroadcastState>(
                Guid.NewGuid(),
                scope,
                BroadcastObservations.Subject,
                BroadcastObservations.Predicate,
                BroadcastState.Commercial,
                sourceId,
                SourceKind,
                confidence.Value,
                sample.CapturedAt));
        }

        return observations;
    }
}
