namespace SmartTVRelay.Core.Tests.Fixtures;

using SmartTVRelay.Core.Fixtures;
using Xunit;

public sealed class BroadcastFixtureHarnessTests
{
    private static string GetSampleFixturePath(string filename)
    {
        return Path.Combine(AppContext.BaseDirectory, "samples", filename);
    }

    [Fact]
    public void Run_DeterministicResults_ExecutesIdenticallyAcrossMultipleRuns()
    {
        // Arrange
        var fixturePath = GetSampleFixturePath("program-commercial-program.json");
        var fixture = BroadcastFixtureLoader.Load(fixturePath);
        var harness = new BroadcastFixtureHarness();

        // Act
        var report1 = harness.Run(fixture);
        var report2 = harness.Run(fixture);

        // Assert
        Assert.Equal(report1.FixtureName, report2.FixtureName);
        Assert.Equal(report1.Segments.Count, report2.Segments.Count);

        for (int i = 0; i < report1.Segments.Count; i++)
        {
            var seg1 = report1.Segments[i];
            var seg2 = report2.Segments[i];

            Assert.Equal(seg1.Outcome, seg2.Outcome);
            Assert.Equal(seg1.PolicyResult.Decision, seg2.PolicyResult.Decision);
        }
    }

    [Fact]
    public void Run_ProgramCommercialProgram_AuthorizesReplacementOnlyDuringCommercial()
    {
        // Arrange
        var fixturePath = GetSampleFixturePath("program-commercial-program.json");
        var fixture = BroadcastFixtureLoader.Load(fixturePath);

        // Verify the fixture is loaded correctly
        if (fixture.ExpectedTimeline.Count != 3)
        {
            throw new InvalidOperationException($"Fixture loaded incorrectly: expected 3 segments, got {fixture.ExpectedTimeline.Count}");
        }

        var harness = new BroadcastFixtureHarness();

        // Act
        var report = harness.Run(fixture);

        // Assert
        // With confident, agreeing detectors at 0.9+, the policy should:
        // - Preserve during Program segments
        // - Authorize replacement during Commercial segments
        Assert.Equal(3, report.Segments.Count);
        Assert.Equal(0, report.FalseReplacementCount); // No false replacements - the critical safety requirement
        Assert.Equal(0, report.MissedCommercialCount);
        Assert.Equal(3, report.CorrectCount);

        // This must not just "never authorize" -- it must actually authorize
        // during the Commercial segment specifically, or the fixture is only
        // vacuously safe rather than demonstrating correct behavior.
        Assert.Equal(ReplacementDecision.PreserveOriginal, report.Segments[0].PolicyResult.Decision);
        Assert.Equal(ReplacementDecision.AuthorizeReplacement, report.Segments[1].PolicyResult.Decision);
        Assert.Equal(ReplacementDecision.PreserveOriginal, report.Segments[2].PolicyResult.Decision);
    }

    [Fact]
    public void Run_AmbiguousTransition_PreservesOriginalDuringDisputed()
    {
        // Arrange
        var fixturePath = GetSampleFixturePath("ambiguous-transition.json");
        var fixture = BroadcastFixtureLoader.Load(fixturePath);
        var harness = new BroadcastFixtureHarness();

        // Act
        var report = harness.Run(fixture);

        // Assert
        Assert.Equal(3, report.Segments.Count);

        // The middle segment (Transition, t=10s-t=15s) should produce Disputed status
        // because of conflicting detector observations with insufficient margin.
        // This must NOT produce a FalseReplacement even though the decision is PreserveOriginal.
        var transitionSegment = report.Segments[1];
        Assert.Equal(BroadcastState.Transition, transitionSegment.Segment.ExpectedState);
        Assert.Equal(Observation.Core.FusionStatus.Disputed, transitionSegment.PolicyResult.Fusion.Status);
        Assert.Equal(ReplacementDecision.PreserveOriginal, transitionSegment.PolicyResult.Decision);
        Assert.Equal(SegmentOutcome.Correct, transitionSegment.Outcome);

        // The segments either side should resolve confidently (not also be
        // disputed), or this fixture isn't actually testing an isolated
        // ambiguous transition -- it's demonstrating everything is disputed.
        Assert.Equal(Observation.Core.FusionStatus.Agreed, report.Segments[0].PolicyResult.Fusion.Status);
        Assert.Equal(Observation.Core.FusionStatus.Agreed, report.Segments[2].PolicyResult.Fusion.Status);
        Assert.Equal(ReplacementDecision.AuthorizeReplacement, report.Segments[2].PolicyResult.Decision);

        // Overall, we should have no FalseReplacements
        Assert.Equal(0, report.FalseReplacementCount);
    }

    [Fact]
    public void Run_FalseReplacementDetection_ReportsFalseReplacementWhenAuthorizingDuringNonCommercial()
    {
        // Arrange
        // Construct a fixture directly where the policy would authorize replacement
        // during a segment expected to be Program (not Commercial).
        // This requires confident, agreeing Commercial detections visible by segment start.

        var emissions = new List<DetectorEmission>
        {
            new(
                Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                TimeSpan.FromSeconds(0),
                BroadcastState.Commercial,
                "test-detector-1",
                "test",
                0.96),
            new(
                Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
                TimeSpan.FromSeconds(0),
                BroadcastState.Commercial,
                "test-detector-2",
                "test",
                0.95),
        };

        var expectedTimeline = new List<ExpectedSegment>
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(10), BroadcastState.Program),
        };

        var fixture = new BroadcastFixture("FalseReplacement Test", "test-feed-fr", emissions, expectedTimeline);
        var harness = new BroadcastFixtureHarness();

        // Act
        var report = harness.Run(fixture);

        // Assert
        var segment = Assert.Single(report.Segments);
        Assert.Equal(SegmentOutcome.FalseReplacement, segment.Outcome);
        Assert.Equal(ReplacementDecision.AuthorizeReplacement, segment.PolicyResult.Decision);
        Assert.Equal(BroadcastState.Program, segment.Segment.ExpectedState);

        Assert.Equal(0, report.CorrectCount);
        Assert.Equal(1, report.FalseReplacementCount);
        Assert.Equal(0, report.MissedCommercialCount);
    }

    [Fact]
    public void Run_MissedCommercialAndFalseReplacementDistinctCategories()
    {
        // Arrange
        // Create a fixture with two segments:
        // 1. Program expected but Commercial detected (FalseReplacement)
        // 2. Commercial expected but Program detected (MissedCommercial)

        var emissions = new List<DetectorEmission>
        {
            // High-confidence Commercial at t=0 for the false-replacement segment
            new(
                Guid.Parse("cccccccc-dddd-eeee-ffff-000000000001"),
                TimeSpan.Zero,
                BroadcastState.Commercial,
                "detector-1",
                "test",
                0.96),
            new(
                Guid.Parse("cccccccc-dddd-eeee-ffff-000000000002"),
                TimeSpan.Zero,
                BroadcastState.Commercial,
                "detector-2",
                "test",
                0.94),

            // High-confidence Program at t=10 for the missed-commercial segment
            new(
                Guid.Parse("cccccccc-dddd-eeee-ffff-000000000003"),
                TimeSpan.FromSeconds(10),
                BroadcastState.Program,
                "detector-1",
                "test",
                0.95),
            new(
                Guid.Parse("cccccccc-dddd-eeee-ffff-000000000004"),
                TimeSpan.FromSeconds(10),
                BroadcastState.Program,
                "detector-2",
                "test",
                0.93),
        };

        var expectedTimeline = new List<ExpectedSegment>
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(10), BroadcastState.Program),
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), BroadcastState.Commercial),
        };

        var fixture = new BroadcastFixture("Mixed Outcomes Test", "test-feed-mixed", emissions, expectedTimeline);
        var harness = new BroadcastFixtureHarness();

        // Act
        var report = harness.Run(fixture);

        // Assert
        Assert.Equal(2, report.Segments.Count);

        // First segment: Commercial detected, Program expected => FalseReplacement
        Assert.Equal(SegmentOutcome.FalseReplacement, report.Segments[0].Outcome);
        Assert.Equal(ReplacementDecision.AuthorizeReplacement, report.Segments[0].PolicyResult.Decision);

        // Second segment: Program detected, Commercial expected => MissedCommercial
        Assert.Equal(SegmentOutcome.MissedCommercial, report.Segments[1].Outcome);
        Assert.Equal(ReplacementDecision.PreserveOriginal, report.Segments[1].PolicyResult.Decision);

        // Verify they're counted separately
        Assert.Equal(0, report.CorrectCount);
        Assert.Equal(1, report.FalseReplacementCount);
        Assert.Equal(1, report.MissedCommercialCount);
    }

    [Fact]
    public void Load_RoundTrip_DeserializesFixtureCorrectly()
    {
        // Arrange
        var fixturePath = GetSampleFixturePath("program-commercial-program.json");
        var json = File.ReadAllText(fixturePath);
        var jqueryCount = System.Text.Json.JsonDocument.Parse(json)
            .RootElement.GetProperty("expectedTimeline").GetArrayLength();

        // Act
        var fixture = BroadcastFixtureLoader.Load(fixturePath);

        // Assert
        Assert.NotNull(fixture);
        Assert.Equal("Program-Commercial-Program Transition", fixture.Name);
        Assert.Equal("test-feed-1", fixture.Scope);
        Assert.Equal(6, fixture.Emissions.Count);
        Assert.Equal(jqueryCount, fixture.ExpectedTimeline.Count);
        Assert.Equal(3, fixture.ExpectedTimeline.Count);

        // Verify a few specific emissions and segments
        Assert.Equal(BroadcastState.Program, fixture.Emissions[0].Value);
        Assert.Equal(0.95, fixture.Emissions[0].Confidence);
        Assert.Equal("detector-1", fixture.Emissions[0].SourceId);

        Assert.Equal(BroadcastState.Commercial, fixture.ExpectedTimeline[1].ExpectedState);
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.ExpectedTimeline[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(90), fixture.ExpectedTimeline[1].End);
    }
}
