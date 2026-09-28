namespace SmartTVRelay.Core;

using Observation.Core;

public sealed class BlackFrameDetector
{
    public const double DefaultLuminanceThreshold = 0.05;
    private const string SourceKind = "black-frame";
    private const double TransitionConfidence = 0.6;

    private readonly double luminanceThreshold;

    public BlackFrameDetector(double luminanceThreshold = DefaultLuminanceThreshold)
    {
        if (!double.IsFinite(luminanceThreshold) || luminanceThreshold < 0 || luminanceThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(luminanceThreshold));
        }

        this.luminanceThreshold = luminanceThreshold;
    }

    public IReadOnlyList<Observation<BroadcastState>> Detect(string sourceId, IReadOnlyList<FrameLuminanceSample> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(samples);

        var scope = ObservationScope.Of(sourceId);
        var observations = new List<Observation<BroadcastState>>();

        foreach (var sample in samples)
        {
            if (sample.AverageLuminance <= luminanceThreshold)
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
