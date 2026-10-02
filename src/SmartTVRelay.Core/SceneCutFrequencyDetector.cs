namespace SmartTVRelay.Core;

using Observation.Core;

/// <summary>
/// Emits evidence from scene-cut cadence over a bounded rolling window: when the count of
/// cuts falling within the trailing <see cref="window"/> exceeds <see cref="cutCountThreshold"/>,
/// something is changing unusually frequently. Cadence alone cannot tell Program from Commercial
/// (an action scene can also cut quickly), so -- matching <see cref="BlackFrameDetector"/> and
/// <see cref="AudioLoudnessDetector"/>'s precedent of emitting <see cref="BroadcastState.Transition"/>
/// for a raw physical signal that isn't itself state-specific -- this detector only ever emits
/// <see cref="BroadcastState.Transition"/>, leaving Program/Commercial classification to fusion.
/// </summary>
public sealed class SceneCutFrequencyDetector
{
    /// <summary>Default rolling window: 5 seconds.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(5);

    /// <summary>Default cut-count threshold: more than 8 cuts in the window (9+) trips it.</summary>
    public const int DefaultCutCountThreshold = 8;

    private const string SourceKind = "scene-cut";

    // Lower than BlackFrame (0.6) and AudioLoudness (0.55): the detector-backlog notes scene-cut
    // cadence is "noisier standalone -- higher false-positive rate" than those two tier-3 signals,
    // so even a threshold-crossing emission reflects less of the detector's own certainty.
    private const double TransitionConfidence = 0.45;

    private readonly TimeSpan window;
    private readonly int cutCountThreshold;

    public SceneCutFrequencyDetector(TimeSpan? window = null, int cutCountThreshold = DefaultCutCountThreshold)
    {
        var effectiveWindow = window ?? DefaultWindow;
        if (effectiveWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        if (cutCountThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(cutCountThreshold));
        }

        this.window = effectiveWindow;
        this.cutCountThreshold = cutCountThreshold;
    }

    /// <summary>
    /// Detects sustained scene-cut cadence. <paramref name="samples"/> must be in
    /// non-decreasing <see cref="SceneCutSample.CapturedAt"/> order (each sample is one
    /// already-detected cut event, pre-computed upstream).
    /// </summary>
    public IReadOnlyList<Observation<BroadcastState>> Detect(string sourceId, IReadOnlyList<SceneCutSample> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(samples);

        var scope = ObservationScope.Of(sourceId);
        var observations = new List<Observation<BroadcastState>>();

        var windowStart = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var current = samples[i];

            // Slide the window's leading edge forward past any cuts older than `window`.
            while (current.CapturedAt - samples[windowStart].CapturedAt > window)
            {
                windowStart++;
            }

            var cutCountInWindow = i - windowStart + 1;
            if (cutCountInWindow > cutCountThreshold)
            {
                // Emitted at the sample that pushed (or keeps) the rolling count over the
                // threshold, mirroring BlackFrameDetector/AudioLoudnessDetector's precedent of
                // one observation per qualifying sample rather than deduplicating a sustained run.
                observations.Add(new Observation<BroadcastState>(
                    Guid.NewGuid(),
                    scope,
                    BroadcastObservations.Subject,
                    BroadcastObservations.Predicate,
                    BroadcastState.Transition,
                    sourceId,
                    SourceKind,
                    TransitionConfidence,
                    current.CapturedAt));
            }
        }

        return observations;
    }
}
