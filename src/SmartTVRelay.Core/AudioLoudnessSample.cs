namespace SmartTVRelay.Core;

/// <summary>
/// One point-in-time audio loudness measurement.
/// </summary>
/// <param name="LoudnessDeltaDb">The change in loudness (dB) from the immediately preceding measurement window.</param>
/// <param name="CapturedAt">The time this measurement was captured.</param>
/// <param name="AbsoluteLevelDb">
/// The absolute loudness level (dBFS, typically zero or negative) at this measurement window, independent of the
/// preceding window. Defaults to 0.0 for backward compatibility with callers that only supply a delta — 0.0 is a
/// normal/loud reference level, not a near-silence one, so omitting this parameter can never accidentally trigger
/// silence/dead-air detection. Silence genuinely requires an absolute level (a large negative delta is an ambiguous
/// proxy: it could mean "went silent" or just "got quieter but is still audible"), so dead-air detection only
/// activates for callers that populate this field with a real measurement.
/// </param>
public sealed record AudioLoudnessSample(double LoudnessDeltaDb, DateTimeOffset CapturedAt, double AbsoluteLevelDb = 0.0);
