namespace SmartTVRelay.Core.Vlm;

using Observation.Core;

/// <summary>
/// Turns already-computed local-VLM classifications (#43) into <see cref="Observation{T}"/> evidence.
/// This observer never calls the model itself -- see <see cref="ILocalVlmClient"/>/<see cref="OllamaLocalVlmClient"/>
/// for the isolated adapter that produces a <see cref="LocalVlmClassification"/> -- matching this
/// repo's existing detector pattern of consuming pre-computed samples (compare
/// <c>LogoPresenceDetector</c>).
/// </summary>
/// <remarks>
/// Per AGENTS.md's evidence priority, a local VLM is the last-resort/optional fallback: this
/// detector never emits anything for a timed-out, errored, <see cref="BroadcastState.Unknown"/>, or
/// low-confidence classification, so a model failure -- or the model simply being unsure -- can
/// never carry authority into fusion. This is a stronger abstention rule than the ambiguity-band
/// approach other detectors use, appropriate for a signal this expensive and this much less
/// deterministic than the cheaper evidence sources ahead of it.
/// </remarks>
public sealed class LocalVlmCommercialObserver
{
    public const string SourceKind = "local-vlm";
    public const double DefaultMinConfidence = 0.6;

    private readonly double _minConfidence;

    public LocalVlmCommercialObserver(double minConfidence = DefaultMinConfidence)
    {
        if (!double.IsFinite(minConfidence) || minConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minConfidence));
        }

        _minConfidence = minConfidence;
    }

    public double MinConfidence => _minConfidence;

    public IReadOnlyList<Observation<BroadcastState>> Detect(string sourceId, IReadOnlyList<LocalVlmSample> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(samples);

        var scope = ObservationScope.Of(sourceId);
        var observations = new List<Observation<BroadcastState>>();

        foreach (var sample in samples)
        {
            var classification = sample.Classification;

            if (classification.TimedOut || classification.Error is not null)
            {
                continue; // A model failure grants no authority.
            }

            if (classification.PredictedState == BroadcastState.Unknown)
            {
                continue; // The model itself abstained.
            }

            if (classification.Confidence < _minConfidence)
            {
                continue; // Too unsure to contribute evidence.
            }

            observations.Add(new Observation<BroadcastState>(
                Guid.NewGuid(),
                scope,
                BroadcastObservations.Subject,
                BroadcastObservations.Predicate,
                classification.PredictedState,
                sourceId,
                SourceKind,
                classification.Confidence,
                sample.CapturedAt));
        }

        return observations;
    }
}
