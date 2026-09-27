namespace SmartTVRelay.Core.Fixtures;

public sealed record DetectorEmission(
    Guid Id,
    TimeSpan At,
    BroadcastState Value,
    string SourceId,
    string SourceKind,
    double Confidence);
