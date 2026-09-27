namespace SmartTVRelay.Core.Fixtures;

public sealed record BroadcastFixture(
    string Name,
    string Scope,
    IReadOnlyList<DetectorEmission> Emissions,
    IReadOnlyList<ExpectedSegment> ExpectedTimeline);
