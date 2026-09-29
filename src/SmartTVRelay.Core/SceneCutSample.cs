namespace SmartTVRelay.Core;

/// <summary>One pre-computed scene-cut (shot-boundary) event. The cut itself is detected
/// upstream (e.g. by a shot-boundary heuristic) -- this detector only reasons about cadence.</summary>
public sealed record SceneCutSample(DateTimeOffset CapturedAt);
