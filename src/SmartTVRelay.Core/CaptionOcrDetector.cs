namespace SmartTVRelay.Core;

using Observation.Core;
using SmartTVRelay.Core.Ingest;

/// <summary>
/// Emits commercial-likelihood evidence from timestamped caption/OCR text, using a bounded,
/// deterministic phrase rule set. Tier-4 evidence per docs/detector-backlog.md: weaker
/// standalone than the visual/audio detectors, so it never claims a final replacement decision
/// -- only <see cref="BroadcastState.Commercial"/> evidence with a correspondingly modest
/// confidence for downstream fusion/policy to weigh.
/// </summary>
public sealed class CaptionOcrDetector
{
    /// <summary>
    /// Built-in commercial-indicative phrase rule set, used when the caller doesn't supply one.
    /// Deliberately specific, direct-response-advertising phrasing (calls to action, legal
    /// boilerplate, sales urgency) rather than generic words -- chosen so a single match is
    /// already meaningful evidence rather than something that would also fire on ordinary
    /// dialogue (see <see cref="DefaultMinimumMatchCount"/> for why that lets the threshold be 1).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultCommercialPhrases = new[]
    {
        "call now",
        "call this number",
        "limited time offer",
        "terms and conditions apply",
        "operators are standing by",
        "visit our website",
        "act now",
        "order now",
        "supplies are limited",
        "while supplies last",
        "money back guarantee",
        "as seen on tv",
        "click here to order",
        "shipping and handling",
    };

    /// <summary>
    /// Default minimum number of distinct matched phrases required within a single caption
    /// sample before an observation is emitted. Set to 1: the default phrase list is composed
    /// of strong, specific commercial-indicative language that essentially never appears in
    /// ordinary program dialogue, and caption cues are typically short (one sentence or clause),
    /// so requiring a second co-occurring match within the same cue would silently suppress most
    /// genuine positives rather than improve precision. Callers with a broader/noisier custom
    /// phrase list can raise this via the constructor.
    /// </summary>
    public const int DefaultMinimumMatchCount = 1;

    private const string SourceKind = "caption-ocr";

    // Confidence scales modestly with corroborating match count but is capped well below the
    // visual/audio detectors' and far below explicit-marker's: caption/OCR is tier 4 (weaker
    // standalone signal per docs/detector-backlog.md), so even a strong phrase match should
    // never outweigh cheaper deterministic evidence in fusion on its own.
    private const double BaseConfidence = 0.5;
    private const double PerAdditionalMatchConfidence = 0.1;
    private const double MaxConfidence = 0.75;

    private readonly IReadOnlyList<string> phrases;
    private readonly int minimumMatchCount;

    public CaptionOcrDetector(IReadOnlyList<string>? commercialPhrases = null, int minimumMatchCount = DefaultMinimumMatchCount)
    {
        if (minimumMatchCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumMatchCount), minimumMatchCount, "Minimum match count must be at least 1.");
        }

        if (commercialPhrases is not null)
        {
            foreach (var phrase in commercialPhrases)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(phrase, nameof(commercialPhrases));
            }
        }

        phrases = commercialPhrases is { Count: > 0 } ? commercialPhrases : DefaultCommercialPhrases;
        this.minimumMatchCount = minimumMatchCount;
    }

    /// <summary>
    /// Scans each caption sample's text for configured commercial-indicative phrases
    /// (case-insensitive substring match) and emits one observation per sample that meets the
    /// minimum match count. An empty or sparse sample list, or samples with no matches,
    /// correctly produces no observations -- this detector never fabricates evidence from an
    /// absence of data (per AGENTS.md's prime safety rule).
    /// </summary>
    public IReadOnlyList<CaptionCommercialObservation> Detect(string sourceId, IReadOnlyList<CaptionSample> samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(samples);

        var scope = ObservationScope.Of(sourceId);
        var results = new List<CaptionCommercialObservation>();

        foreach (var sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.Text))
            {
                continue;
            }

            var matched = FindMatchedPhrases(sample.Text);
            if (matched.Count < minimumMatchCount)
            {
                continue;
            }

            var confidence = Math.Min(MaxConfidence, BaseConfidence + (PerAdditionalMatchConfidence * (matched.Count - 1)));

            var observation = new Observation<BroadcastState>(
                Guid.NewGuid(),
                scope,
                BroadcastObservations.Subject,
                BroadcastObservations.Predicate,
                BroadcastState.Commercial,
                sourceId,
                SourceKind,
                confidence,
                sample.StartTime);

            results.Add(new CaptionCommercialObservation(observation, matched));
        }

        return results;
    }

    private IReadOnlyList<string> FindMatchedPhrases(string text)
    {
        List<string>? matched = null;

        foreach (var phrase in phrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                (matched ??= new List<string>()).Add(phrase);
            }
        }

        return matched ?? (IReadOnlyList<string>)Array.Empty<string>();
    }
}
