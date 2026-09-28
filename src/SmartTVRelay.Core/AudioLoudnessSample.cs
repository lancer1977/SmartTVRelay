namespace SmartTVRelay.Core;

/// <summary>The change in loudness (dB) from the immediately preceding measurement window, for one point in time.</summary>
public sealed record AudioLoudnessSample(double LoudnessDeltaDb, DateTimeOffset CapturedAt);
