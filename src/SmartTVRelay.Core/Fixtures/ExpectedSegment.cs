namespace SmartTVRelay.Core.Fixtures;

public sealed record ExpectedSegment(
    TimeSpan Start,
    TimeSpan End,
    BroadcastState ExpectedState);
