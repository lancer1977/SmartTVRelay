namespace SmartTVRelay.Core.Fixtures;

/// <summary>Current schema version for <see cref="BroadcastFixture"/>. Bump when the shape changes in a
/// way that breaks existing fixture files; <see cref="BroadcastFixtureValidator"/> rejects a mismatch.</summary>
public static class BroadcastFixtureSchema
{
    public const int CurrentVersion = 2;
}

/// <summary>A replayable broadcast evaluation fixture (#22). One schema covers both fixture styles:
/// a <b>synthetic</b> fixture supplies <paramref name="Emissions"/> directly (hand-authored or
/// generated detector output, no real media involved); a <b>recorded</b> fixture instead sets
/// <paramref name="MediaPath"/> to a real captured file and leaves <paramref name="Emissions"/> empty,
/// since real detectors compute emissions from the referenced media at evaluation time (once an ingest
/// adapter -- #29/#30 -- reads it). In both cases <paramref name="ExpectedTimeline"/> is the labeled
/// ground truth the evaluation platform (#7) scores against.</summary>
public sealed record BroadcastFixture(
    string Name,
    string Scope,
    IReadOnlyList<DetectorEmission> Emissions,
    IReadOnlyList<ExpectedSegment> ExpectedTimeline,
    int SchemaVersion = BroadcastFixtureSchema.CurrentVersion,
    string? MediaPath = null);
