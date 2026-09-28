namespace SmartTVRelay.Core;

using Observation.Core;

public sealed class AudioLoudnessDetector
{
    public const double DefaultDeltaThresholdDb = 6.0;
    private const string SourceKind = "audio-loudness";
    private const double TransitionConfidence = 0.55;

    private readonly double deltaThresholdDb;

    public AudioLoudnessDetector(double deltaThresholdDb = DefaultDeltaThresholdDb)
    {
        if (!double.IsFinite(deltaThresholdDb) || deltaThresholdDb < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaThresholdDb));
        }

        this.deltaThresholdDb = deltaThresholdDb;
    }

    public IReadOnlyList<Observation<BroadcastState>> Detect(string sourceId, IReadOnlyList<AudioLoudnessSample> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(samples);

        var scope = ObservationScope.Of(sourceId);
        var observations = new List<Observation<BroadcastState>>();

        foreach (var sample in samples)
        {
            if (Math.Abs(sample.LoudnessDeltaDb) >= deltaThresholdDb)
            {
                observations.Add(new Observation<BroadcastState>(
                    Guid.NewGuid(),
                    scope,
                    BroadcastObservations.Subject,
                    BroadcastObservations.Predicate,
                    BroadcastState.Transition,
                    sourceId,
                    SourceKind,
                    TransitionConfidence,
                    sample.CapturedAt));
            }
        }

        return observations;
    }
}
