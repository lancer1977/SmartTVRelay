namespace SmartTVRelay.Core;

/// <summary>
/// One pre-computed audio fingerprint for a bounded window of the broadcast, captured at a point in time.
/// The fingerprint itself (<see cref="Hash"/>) is an already-computed hash vector -- this detector never
/// performs raw audio DSP; it only compares vectors already produced upstream by the capture pipeline.
/// </summary>
public sealed record AudioFingerprintSample(IReadOnlyList<int> Hash, DateTimeOffset CapturedAt);
