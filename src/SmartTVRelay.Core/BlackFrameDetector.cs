namespace SmartTVRelay.Core;

using Observation.Core;

/// <summary>
/// Detects black-frame runs that plausibly signal a broadcast transition (e.g. into/out of a
/// commercial break), from a sequence of per-frame luminance samples.
///
/// Hardening over the original (#59) single-frame threshold check:
/// <list type="bullet">
/// <item>
/// A configurable minimum run duration ("<see cref="MinimumTransitionDuration"/>") avoids treating
/// a momentary scene-cut flash or a brief dark-scene dip as a transition signal. A run of
/// qualifying frames only emits observations once its span (last frame's timestamp minus first
/// frame's timestamp) meets or exceeds this duration. The default is zero, which preserves the
/// exact #59 behavior of flagging every qualifying frame independently.
/// </item>
/// <item>
/// Letterboxing/pillarboxing robustness is handled via
/// <see cref="FrameLuminanceSample.ContentAreaLuminance"/>, NOT via duration. Persistent
/// letterbox bars are, by construction, a long-lived continuous condition, so a duration
/// threshold alone cannot distinguish "the bars are black for 10 minutes because it's
/// letterboxed" from "the whole picture is black for 10 minutes because a commercial rolled in
/// early" -- both are equally continuous. What differs is the CONTENT: letterboxing keeps the
/// interior picture at normal luminance while only the bars go black, whereas a real
/// black-frame transition takes the whole frame (bars and content together) black. Callers that
/// can identify the content region should populate <see cref="FrameLuminanceSample.ContentAreaLuminance"/>
/// with the interior-only average; when present, the detector evaluates that value instead of
/// the full-frame average, so a low full-frame average caused solely by bars no longer qualifies
/// as a black frame. When the field is omitted (null), the detector falls back to
/// <see cref="FrameLuminanceSample.AverageLuminance"/>, i.e. it cannot distinguish letterboxing
/// from a real transition in that case -- this is a real limit of this approach, not a solved
/// general case: it only works when the upstream sampler actually supplies a content-area
/// reading.
/// </item>
/// </list>
/// </summary>
public sealed class BlackFrameDetector
{
    public const double DefaultLuminanceThreshold = 0.05;
    private const string SourceKind = "black-frame";
    private const double TransitionConfidence = 0.6;

    private readonly double luminanceThreshold;
    private readonly TimeSpan minimumTransitionDuration;

    /// <param name="luminanceThreshold">
    /// Samples at or below this normalized luminance qualify as "black". Unchanged from #59.
    /// </param>
    /// <param name="minimumTransitionDuration">
    /// Minimum span a contiguous run of qualifying frames must cover (last qualifying frame's
    /// <see cref="FrameLuminanceSample.CapturedAt"/> minus the run's first) before it counts as a
    /// transition signal. Defaults to <see cref="TimeSpan.Zero"/>, which accepts any run
    /// (including a single frame) -- i.e. the original #59 behavior. Pass a positive duration to
    /// require the run to persist for at least that long, which filters out single-frame
    /// scene-cut flashes and brief dark-scene dips.
    /// </param>
    public BlackFrameDetector(double luminanceThreshold = DefaultLuminanceThreshold, TimeSpan? minimumTransitionDuration = null)
    {
        if (!double.IsFinite(luminanceThreshold) || luminanceThreshold < 0 || luminanceThreshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(luminanceThreshold));
        }

        if (minimumTransitionDuration is { } duration && duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumTransitionDuration));
        }

        this.luminanceThreshold = luminanceThreshold;
        this.minimumTransitionDuration = minimumTransitionDuration ?? TimeSpan.Zero;
    }

    public IReadOnlyList<Observation<BroadcastState>> Detect(string sourceId, IReadOnlyList<FrameLuminanceSample> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(samples);

        var scope = ObservationScope.Of(sourceId);
        var observations = new List<Observation<BroadcastState>>();

        var runStart = -1;
        for (var i = 0; i <= samples.Count; i++)
        {
            var qualifies = i < samples.Count && IsBlack(samples[i]);
            if (qualifies)
            {
                if (runStart < 0)
                {
                    runStart = i;
                }

                continue;
            }

            if (runStart >= 0)
            {
                EmitRunIfLongEnough(samples, runStart, i - 1, scope, sourceId, observations);
                runStart = -1;
            }
        }

        return observations;
    }

    /// <summary>
    /// A frame qualifies as "black" based on the content-area luminance when the caller supplied
    /// one (i.e. it can distinguish letterbox bars from picture content), otherwise on the
    /// full-frame average -- exactly the #59 check.
    /// </summary>
    private bool IsBlack(FrameLuminanceSample sample)
    {
        var effectiveLuminance = sample.ContentAreaLuminance ?? sample.AverageLuminance;
        return effectiveLuminance <= luminanceThreshold;
    }

    private void EmitRunIfLongEnough(
        IReadOnlyList<FrameLuminanceSample> samples,
        int runStart,
        int runEnd,
        ObservationScope scope,
        string sourceId,
        List<Observation<BroadcastState>> observations)
    {
        var runDuration = samples[runEnd].CapturedAt - samples[runStart].CapturedAt;
        if (runDuration < minimumTransitionDuration)
        {
            return;
        }

        for (var i = runStart; i <= runEnd; i++)
        {
            var sample = samples[i];
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
}
