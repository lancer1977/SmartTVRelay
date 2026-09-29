namespace SmartTVRelay.Core.Vlm;

/// <summary>
/// Structured result of one local-VLM classification attempt (#43). Never represents a thrown
/// exception -- model/timeout/parse failures are reported via <see cref="TimedOut"/>/<see cref="Error"/>
/// with <see cref="PredictedState"/> forced to <see cref="BroadcastState.Unknown"/> and
/// <see cref="Confidence"/> forced to zero, so a failure can never itself carry authority into a
/// downstream decision.
/// </summary>
public sealed record LocalVlmClassification(
    BroadcastState PredictedState,
    double Confidence,
    TimeSpan Latency,
    bool TimedOut,
    string? Error)
{
    /// <summary>Constructs a failed classification: Unknown state, zero confidence, the given error, not a timeout.</summary>
    public static LocalVlmClassification Failed(TimeSpan latency, string error) =>
        new(BroadcastState.Unknown, 0.0, latency, TimedOut: false, error);

    /// <summary>Constructs a timed-out classification: Unknown state, zero confidence, no error text.</summary>
    public static LocalVlmClassification TimedOutResult(TimeSpan latency) =>
        new(BroadcastState.Unknown, 0.0, latency, TimedOut: true, Error: null);
}
