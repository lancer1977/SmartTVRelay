namespace SmartTVRelay.Core.Tests.Fixtures;

using SmartTVRelay.Core;
using SmartTVRelay.Core.Fixtures;
using Xunit;

public class SyntheticFixtureGeneratorTests
{
    [Theory]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateCleanBreak))]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateShortBreak))]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateAmbiguousTransition))]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateFalsePositiveTrap))]
    public void Generate_SameSeedTwice_ProducesIdenticalFixtureMetadata(string method)
    {
        var first = Invoke(method, seed: 42);
        var second = Invoke(method, seed: 42);

        Assert.Equal(first.Name, second.Name);
        Assert.Equal(first.Scope, second.Scope);
        Assert.Equal(first.SchemaVersion, second.SchemaVersion);
        Assert.Equal(first.MediaPath, second.MediaPath);
        Assert.Equal(first.ExpectedTimeline, second.ExpectedTimeline);

        // Record equality on DetectorEmission compares Id/At/Value/SourceId/SourceKind/Confidence
        // field-by-field; xUnit's Assert.Equal on two IReadOnlyList<T> does an element-wise
        // sequence comparison, so this proves every emission -- including its deterministically
        // generated Guid and jittered confidence -- is byte-identical across the two runs, not
        // just "the same length" or "didn't throw."
        Assert.Equal(first.Emissions, second.Emissions);
    }

    [Theory]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateCleanBreak))]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateShortBreak))]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateAmbiguousTransition))]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateFalsePositiveTrap))]
    public void Generate_DifferentSeeds_ProducesDifferentEmissionIds(string method)
    {
        var first = Invoke(method, seed: 1);
        var second = Invoke(method, seed: 2);

        // Sanity check the seed is actually wired through the generator, not silently ignored --
        // if it were ignored, this would fail alongside the determinism test above being
        // vacuously true for the wrong reason.
        Assert.NotEqual(first.Emissions.Select(e => e.Id), second.Emissions.Select(e => e.Id));
    }

    [Theory]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateCleanBreak))]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateShortBreak))]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateAmbiguousTransition))]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateFalsePositiveTrap))]
    public void Generate_EachScenario_ValidatesAgainstFixtureSchema(string method)
    {
        var fixture = Invoke(method, seed: 7);

        var errors = BroadcastFixtureValidator.Validate(fixture);

        Assert.Empty(errors);
    }

    [Fact]
    public void GenerateCleanBreak_ExpectedTimelineHasNoTransitionSegment()
    {
        var fixture = SyntheticFixtureGenerator.GenerateCleanBreak(seed: 7);

        Assert.DoesNotContain(fixture.ExpectedTimeline, s => s.ExpectedState == BroadcastState.Transition);
        Assert.Equal(BroadcastState.Program, fixture.ExpectedTimeline[0].ExpectedState);
        Assert.Equal(BroadcastState.Commercial, fixture.ExpectedTimeline[1].ExpectedState);
        Assert.Equal(BroadcastState.Program, fixture.ExpectedTimeline[2].ExpectedState);
    }

    [Fact]
    public void GenerateShortBreak_CommercialSegmentIsShorterThanCleanBreaks()
    {
        var shortBreak = SyntheticFixtureGenerator.GenerateShortBreak(seed: 7);
        var cleanBreak = SyntheticFixtureGenerator.GenerateCleanBreak(seed: 7);

        var shortCommercial = shortBreak.ExpectedTimeline.Single(s => s.ExpectedState == BroadcastState.Commercial);
        var cleanCommercial = cleanBreak.ExpectedTimeline.Single(s => s.ExpectedState == BroadcastState.Commercial);

        Assert.True((shortCommercial.End - shortCommercial.Start) < (cleanCommercial.End - cleanCommercial.Start));
        Assert.DoesNotContain(shortBreak.ExpectedTimeline, s => s.ExpectedState == BroadcastState.Transition);
    }

    [Fact]
    public void GenerateAmbiguousTransition_ExpectedTimelineHasExactlyOneTransitionSegmentBetweenProgramAndCommercial()
    {
        var fixture = SyntheticFixtureGenerator.GenerateAmbiguousTransition(seed: 7);

        var transitionSegments = fixture.ExpectedTimeline.Where(s => s.ExpectedState == BroadcastState.Transition).ToList();
        Assert.Single(transitionSegments);

        Assert.Equal(BroadcastState.Program, fixture.ExpectedTimeline[0].ExpectedState);
        Assert.Equal(BroadcastState.Transition, fixture.ExpectedTimeline[1].ExpectedState);
        Assert.Equal(BroadcastState.Commercial, fixture.ExpectedTimeline[2].ExpectedState);

        // The transition segment must actually bridge the two, with no gap or overlap.
        Assert.Equal(fixture.ExpectedTimeline[0].End, transitionSegments[0].Start);
        Assert.Equal(transitionSegments[0].End, fixture.ExpectedTimeline[2].Start);
    }

    [Fact]
    public void GenerateAmbiguousTransition_HasDisagreeingEmissionsAtTheTransitionBoundary()
    {
        var fixture = SyntheticFixtureGenerator.GenerateAmbiguousTransition(seed: 7);
        var transitionStart = fixture.ExpectedTimeline.Single(s => s.ExpectedState == BroadcastState.Transition).Start;

        var atBoundary = fixture.Emissions.Where(e => e.At == transitionStart).ToList();

        Assert.True(atBoundary.Count >= 2, "Expected at least two emissions at the transition boundary.");
        Assert.Contains(atBoundary, e => e.Value == BroadcastState.Program);
        Assert.Contains(atBoundary, e => e.Value == BroadcastState.Commercial);
    }

    [Fact]
    public void GenerateFalsePositiveTrap_ExpectedTimelineIsProgramThroughoutDespiteADisagreeingEmission()
    {
        var fixture = SyntheticFixtureGenerator.GenerateFalsePositiveTrap(seed: 7);

        // The whole point of this scenario: exactly one segment, entirely Program, even though a
        // detector reported a Commercial-like blip mid-stream.
        var segment = Assert.Single(fixture.ExpectedTimeline);
        Assert.Equal(BroadcastState.Program, segment.ExpectedState);

        Assert.Contains(fixture.Emissions, e => e.Value == BroadcastState.Commercial);
        Assert.DoesNotContain(fixture.ExpectedTimeline, s => s.ExpectedState is BroadcastState.Commercial or BroadcastState.Transition);
    }

    [Fact]
    public void GenerateFalsePositiveTrap_SpuriousBlipHasLowConfidence()
    {
        var fixture = SyntheticFixtureGenerator.GenerateFalsePositiveTrap(seed: 7);

        var spuriousBlip = fixture.Emissions.Single(e => e.Value == BroadcastState.Commercial);

        // A confident false alarm would be a much harder (and different) test case than this
        // scenario is meant to cover -- the trap here is specifically a *low-confidence* dissent
        // that a naive "trust the newest emission" policy could still wrongly act on.
        Assert.True(spuriousBlip.Confidence < 0.6);
    }

    [Fact]
    public void GenerateAll_ReturnsAllFourScenariosDerivedFromOneSeed()
    {
        var fixtures = SyntheticFixtureGenerator.GenerateAll(seed: 100);

        Assert.Equal(4, fixtures.Count);
        Assert.All(fixtures, f => Assert.Empty(BroadcastFixtureValidator.Validate(f)));

        // Every scenario must actually be represented, not e.g. the same one generated four times.
        var names = fixtures.Select(f => f.Name).ToHashSet();
        Assert.Equal(4, names.Count);
    }

    [Fact]
    public void GenerateAll_SameSeedTwice_ProducesIdenticalFixtureSets()
    {
        var first = SyntheticFixtureGenerator.GenerateAll(seed: 55);
        var second = SyntheticFixtureGenerator.GenerateAll(seed: 55);

        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Emissions, second[i].Emissions);
            Assert.Equal(first[i].ExpectedTimeline, second[i].ExpectedTimeline);
        }
    }

    private static BroadcastFixture Invoke(string method, int seed) => method switch
    {
        nameof(SyntheticFixtureGenerator.GenerateCleanBreak) => SyntheticFixtureGenerator.GenerateCleanBreak(seed),
        nameof(SyntheticFixtureGenerator.GenerateShortBreak) => SyntheticFixtureGenerator.GenerateShortBreak(seed),
        nameof(SyntheticFixtureGenerator.GenerateAmbiguousTransition) => SyntheticFixtureGenerator.GenerateAmbiguousTransition(seed),
        nameof(SyntheticFixtureGenerator.GenerateFalsePositiveTrap) => SyntheticFixtureGenerator.GenerateFalsePositiveTrap(seed),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown generator method."),
    };
}
