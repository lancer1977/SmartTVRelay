namespace SmartTVRelay.Core;

/// <summary>One frame's average luminance, normalized to [0, 1] (0 = fully black, 1 = fully white).</summary>
public sealed record FrameLuminanceSample(double AverageLuminance, DateTimeOffset CapturedAt);
