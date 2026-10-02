namespace SmartTVRelay.Core;

using Observation.Core;

/// <summary>
/// Detects two distinct audio-loudness signals from a sequence of measurement samples:
/// <list type="bullet">
/// <item>
/// <description>
/// A loudness-<see cref="BroadcastState.Transition"/> signal: a sharp, isolated loudness delta that holds at its
/// new level (a "sustained" jump), which is the shape of a real commercial-boundary loudness change. A loud
/// music swell or an action-sequence sting also produces large deltas, but those levels do not hold — they
/// swing back within a short window — so they are suppressed rather than reported as a transition.
/// </description>
/// </item>
/// <item>
/// <description>
/// A dead-air signal, reported as <see cref="BroadcastState.Unknown"/> (deliberately not
/// <see cref="BroadcastState.Commercial"/> — see remarks below): a sustained run of near-silent absolute
/// loudness levels.
/// </description>
/// </item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why dead-air maps to <see cref="BroadcastState.Unknown"/>, not <see cref="BroadcastState.Commercial"/>:</b>
/// dead air happens for reasons unrelated to commercials too (format changes, technical faults, a deliberate
/// dramatic pause). Per this repo's prime safety rule, evidence that is merely consistent with — but does not
/// establish — a commercial break must not assert one; asserting <see cref="BroadcastState.Unknown"/> keeps
/// downstream fusion/policy free to preserve the original broadcast rather than act on an unproven guess.
/// </para>
/// <para>
/// <b>Absolute-level dependency:</b> silence detection and the sustained-hold guard both key off
/// <see cref="AudioLoudnessSample.AbsoluteLevelDb"/>, which defaults to 0.0 (a normal/loud level) when a caller
/// only supplies a delta. That default can never cross the (negative) silence threshold and never differs
/// between samples, so both new behaviors are a no-op for callers that do not populate absolute level — this is
/// what keeps every pre-existing call site and test byte-for-byte compatible.
/// </para>
/// </remarks>
public sealed class AudioLoudnessDetector
{
    public const double DefaultDeltaThresholdDb = 6.0;

    /// <summary>
    /// Absolute loudness level (dBFS) at or below which a sample is considered near-silent. -45 dBFS sits well
    /// below typical broadcast program/commercial loudness (roughly -24 to -16 dBFS) and well above the analog
    /// noise floor, so it flags genuine dead air without tripping on ordinary quiet dialogue or pauses.
    /// </summary>
    public const double DefaultSilenceThresholdDb = -45.0;

    /// <summary>
    /// Minimum contiguous duration a run of near-silent samples must span before it is reported as dead air.
    /// Filters out a single quiet sample (a brief pause, a metering glitch) from being mistaken for dead air.
    /// </summary>
    public const double DefaultSilenceMinDurationMilliseconds = 250.0;

    /// <summary>
    /// How close (dB) a candidate jump's absolute level must stay to itself over
    /// <see cref="DefaultSustainedDurationMilliseconds"/> to count as "held" rather than a transient swing.
    /// </summary>
    public const double DefaultSustainedToleranceDb = 3.0;

    /// <summary>
    /// Window after a candidate jump over which the new level must stay within
    /// <see cref="DefaultSustainedToleranceDb"/> for the jump to be reported as a transition. Loud music/action
    /// passages characteristically swing back within a fraction of a second; a real commercial-boundary loudness
    /// change holds at its new level for substantially longer.
    /// </summary>
    public const double DefaultSustainedDurationMilliseconds = 500.0;

    private const string SourceKind = "audio-loudness";
    private const double TransitionConfidence = 0.55;

    /// <summary>
    /// Confidence for the dead-air signal. Deliberately lower than <see cref="TransitionConfidence"/>: silence
    /// alone is weaker, more ambiguous evidence of a commercial boundary than a loudness transition is.
    /// </summary>
    private const double SilenceConfidence = 0.45;

    private readonly double deltaThresholdDb;
    private readonly double silenceThresholdDb;
    private readonly double silenceMinDurationMilliseconds;
    private readonly double sustainedToleranceDb;
    private readonly double sustainedDurationMilliseconds;

    public AudioLoudnessDetector(
        double deltaThresholdDb = DefaultDeltaThresholdDb,
        double silenceThresholdDb = DefaultSilenceThresholdDb,
        double silenceMinDurationMilliseconds = DefaultSilenceMinDurationMilliseconds,
        double sustainedToleranceDb = DefaultSustainedToleranceDb,
        double sustainedDurationMilliseconds = DefaultSustainedDurationMilliseconds)
    {
        if (!double.IsFinite(deltaThresholdDb) || deltaThresholdDb < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaThresholdDb));
        }

        if (!double.IsFinite(silenceThresholdDb))
        {
            throw new ArgumentOutOfRangeException(nameof(silenceThresholdDb));
        }

        if (!double.IsFinite(silenceMinDurationMilliseconds) || silenceMinDurationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(silenceMinDurationMilliseconds));
        }

        if (!double.IsFinite(sustainedToleranceDb) || sustainedToleranceDb < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sustainedToleranceDb));
        }

        if (!double.IsFinite(sustainedDurationMilliseconds) || sustainedDurationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sustainedDurationMilliseconds));
        }

        this.deltaThresholdDb = deltaThresholdDb;
        this.silenceThresholdDb = silenceThresholdDb;
        this.silenceMinDurationMilliseconds = silenceMinDurationMilliseconds;
        this.sustainedToleranceDb = sustainedToleranceDb;
        this.sustainedDurationMilliseconds = sustainedDurationMilliseconds;
    }

    public IReadOnlyList<Observation<BroadcastState>> Detect(string sourceId, IReadOnlyList<AudioLoudnessSample> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(samples);

        var scope = ObservationScope.Of(sourceId);
        var observations = new List<Observation<BroadcastState>>();

        DateTimeOffset? silenceRunStartedAt = null;
        bool silenceReportedForRun = false;

        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];

            if (sample.AbsoluteLevelDb <= silenceThresholdDb)
            {
                silenceRunStartedAt ??= sample.CapturedAt;

                if (!silenceReportedForRun &&
                    (sample.CapturedAt - silenceRunStartedAt.Value).TotalMilliseconds >= silenceMinDurationMilliseconds)
                {
                    observations.Add(new Observation<BroadcastState>(
                        Guid.NewGuid(),
                        scope,
                        BroadcastObservations.Subject,
                        BroadcastObservations.Predicate,
                        BroadcastState.Unknown,
                        sourceId,
                        SourceKind,
                        SilenceConfidence,
                        sample.CapturedAt));

                    silenceReportedForRun = true;
                }
            }
            else
            {
                silenceRunStartedAt = null;
                silenceReportedForRun = false;
            }

            if (Math.Abs(sample.LoudnessDeltaDb) >= deltaThresholdDb && IsSustainedJump(samples, i))
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

    /// <summary>
    /// A candidate jump is treated as a real transition only if the absolute level it jumps to holds within
    /// <see cref="sustainedToleranceDb"/> for <see cref="sustainedDurationMilliseconds"/> afterward. This is what
    /// tells a genuine commercial-boundary loudness change apart from a loud music swell or action-sequence hit,
    /// which are large but transient: they swing back before the window elapses.
    /// </summary>
    /// <remarks>
    /// If the sample list ends before the window elapses, there is no future data available to disprove the hold,
    /// so the jump is treated as sustained. This is a known, honest limitation: within a single batch, the very
    /// last candidate jump near the end of the list cannot be fully evaluated and is reported optimistically.
    /// </remarks>
    private bool IsSustainedJump(IReadOnlyList<AudioLoudnessSample> samples, int index)
    {
        var reference = samples[index];

        for (int j = index + 1; j < samples.Count; j++)
        {
            var elapsedMilliseconds = (samples[j].CapturedAt - reference.CapturedAt).TotalMilliseconds;
            if (elapsedMilliseconds > sustainedDurationMilliseconds)
            {
                break;
            }

            if (Math.Abs(samples[j].AbsoluteLevelDb - reference.AbsoluteLevelDb) > sustainedToleranceDb)
            {
                return false;
            }
        }

        return true;
    }
}
