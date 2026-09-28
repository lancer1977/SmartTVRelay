namespace SmartTVRelay.Core.Tests.Fixtures;

using SmartTVRelay.Core;
using SmartTVRelay.Core.Fixtures;
using Xunit;

public class BroadcastFixtureValidatorTests
{
    private static DetectorEmission ValidEmission() =>
        new(Guid.NewGuid(), TimeSpan.Zero, BroadcastState.Program, "detector-1", "heuristic", 0.9);

    private static ExpectedSegment Segment(int startSeconds, int endSeconds, BroadcastState state) =>
        new(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds), state);

    [Fact]
    public void Validate_AcceptsWellFormedSyntheticFixture()
    {
        var fixture = new BroadcastFixture(
            "valid",
            "scope",
            new[] { ValidEmission() },
            new[] { Segment(0, 30, BroadcastState.Program) });

        var errors = BroadcastFixtureValidator.Validate(fixture);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_AcceptsWellFormedRecordedFixtureWithNoEmissions()
    {
        var fixture = new BroadcastFixture(
            "valid-recorded",
            "scope",
            Array.Empty<DetectorEmission>(),
            new[] { Segment(0, 30, BroadcastState.Program) },
            MediaPath: "captures/sample.ts");

        var errors = BroadcastFixtureValidator.Validate(fixture);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_RejectsSyntheticFixtureWithNoEmissions()
    {
        var fixture = new BroadcastFixture(
            "invalid",
            "scope",
            Array.Empty<DetectorEmission>(),
            new[] { Segment(0, 30, BroadcastState.Program) });

        var errors = BroadcastFixtureValidator.Validate(fixture);

        Assert.Contains(errors, e => e.Contains("must supply at least one emission"));
    }

    [Fact]
    public void Validate_RejectsOverlappingSegments()
    {
        var fixture = new BroadcastFixture(
            "invalid",
            "scope",
            new[] { ValidEmission() },
            new[]
            {
                Segment(0, 30, BroadcastState.Program),
                Segment(20, 40, BroadcastState.Commercial),
            });

        var errors = BroadcastFixtureValidator.Validate(fixture);

        Assert.Contains(errors, e => e.Contains("overlaps"));
    }

    [Fact]
    public void Validate_RejectsSegmentWithEndAtOrBeforeStart()
    {
        var fixture = new BroadcastFixture(
            "invalid",
            "scope",
            new[] { ValidEmission() },
            new[] { Segment(30, 30, BroadcastState.Program) });

        var errors = BroadcastFixtureValidator.Validate(fixture);

        Assert.Contains(errors, e => e.Contains("End <= Start"));
    }

    [Fact]
    public void Validate_RejectsEmptyTimeline()
    {
        var fixture = new BroadcastFixture(
            "invalid",
            "scope",
            new[] { ValidEmission() },
            Array.Empty<ExpectedSegment>());

        var errors = BroadcastFixtureValidator.Validate(fixture);

        Assert.Contains(errors, e => e.Contains("must not be empty"));
    }

    [Fact]
    public void Validate_RejectsMismatchedSchemaVersion()
    {
        var fixture = new BroadcastFixture(
            "invalid",
            "scope",
            new[] { ValidEmission() },
            new[] { Segment(0, 30, BroadcastState.Program) },
            SchemaVersion: 1);

        var errors = BroadcastFixtureValidator.Validate(fixture);

        Assert.Contains(errors, e => e.Contains("Unsupported schema version"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Validate_RejectsMissingName(string? name)
    {
        var fixture = new BroadcastFixture(
            name!,
            "scope",
            new[] { ValidEmission() },
            new[] { Segment(0, 30, BroadcastState.Program) });

        var errors = BroadcastFixtureValidator.Validate(fixture);

        Assert.Contains(errors, e => e.Contains("Name must not be null or whitespace"));
    }

    [Fact]
    public void ValidateOrThrow_ThrowsOnInvalidFixture()
    {
        var fixture = new BroadcastFixture(
            "invalid",
            "scope",
            Array.Empty<DetectorEmission>(),
            Array.Empty<ExpectedSegment>());

        Assert.Throws<InvalidDataException>(() => BroadcastFixtureValidator.ValidateOrThrow(fixture));
    }

    [Fact]
    public void ValidateOrThrow_DoesNotThrowOnValidFixture()
    {
        var fixture = new BroadcastFixture(
            "valid",
            "scope",
            new[] { ValidEmission() },
            new[] { Segment(0, 30, BroadcastState.Program) });

        var exception = Record.Exception(() => BroadcastFixtureValidator.ValidateOrThrow(fixture));

        Assert.Null(exception);
    }
}
