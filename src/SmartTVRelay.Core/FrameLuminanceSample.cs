namespace SmartTVRelay.Core;

/// <summary>One frame's average luminance, normalized to [0, 1] (0 = fully black, 1 = fully white).</summary>
/// <param name="AverageLuminance">
/// Full-frame average luminance, normalized to [0, 1]. When letterboxing/pillarboxing bars are
/// present, this value reflects the whole frame INCLUDING the bars and can read as "black" even
/// though the actual picture content is not.
/// </param>
/// <param name="CapturedAt">Wall-clock time the frame was captured.</param>
/// <param name="ContentAreaLuminance">
/// Optional average luminance of just the active picture/content area, excluding any
/// letterbox/pillarbox bars, normalized to [0, 1]. Callers that can identify the content region
/// (e.g. via bar detection) should supply this so the detector can tell "the bars are black but
/// the show is not" apart from "the whole picture, content included, is black". Defaults to
/// <c>null</c>, meaning no bar-aware signal is available; in that case the detector falls back to
/// <see cref="AverageLuminance"/>, which is exactly the pre-existing (#59) behavior.
/// </param>
public sealed record FrameLuminanceSample(
    double AverageLuminance,
    DateTimeOffset CapturedAt,
    double? ContentAreaLuminance = null);
