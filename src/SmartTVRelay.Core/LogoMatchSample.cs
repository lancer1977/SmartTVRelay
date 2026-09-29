namespace SmartTVRelay.Core;

/// <summary>
/// A pre-computed template-match score for a station/network logo ROI, for one point in time.
/// Image matching itself happens upstream of this detector; this sample only carries the result.
/// </summary>
/// <param name="MatchScore">
/// The upstream template-match confidence for the configured logo region, in [0, 1]. Higher values
/// mean the configured logo more strongly matches what was captured at <paramref name="CapturedAt"/>.
/// </param>
/// <param name="CapturedAt">The timestamp the underlying frame was captured.</param>
public sealed record LogoMatchSample(double MatchScore, DateTimeOffset CapturedAt);
