namespace SmartTVRelay.Core.Tests.Fixtures;

using SmartTVRelay.Core;
using SmartTVRelay.Core.Fixtures;
using Xunit;

public class FixtureEvaluationRunnerTests
{
    [Theory]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateCleanBreak))]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateShortBreak))]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateAmbiguousTransition))]
    [InlineData(nameof(SyntheticFixtureGenerator.GenerateFalsePositiveTrap))]
    public void Evaluate_EverySyntheticScenario_RunsWithoutThrowingAndProducesAScoringReport(string method)
    {
        var fixture = Invoke(method, seed: 7);

        var report = FixtureEvaluationRunner.Evaluate(fixture);

        Assert.NotNull(report);
        Assert.True(report.BoundaryErrors.Count <= fixture.ExpectedTimeline.Count);
        Assert.True(report.ConfusionCounts.Values.Sum() > 0);
    }

    [Fact]
    public void Evaluate_CleanBreak_TwoAgreeingConfidentDetectors_AuthorizesReplacementCorrectly()
    {
        var fixture = SyntheticFixtureGenerator.GenerateCleanBreak(seed: 7);

        var report = FixtureEvaluationRunner.Evaluate(fixture);

        // Two agreeing, confident detectors on a clean break should require no fail-open behavior:
        // no missed commercial, no false replacement.
        Assert.Equal(0, report.FalseReplacementCount);
        Assert.Equal(TimeSpan.Zero, report.MissedCommercialDuration);
    }

    [Fact]
    public void Evaluate_FalsePositiveTrap_OneLowConfidenceDissentingDetector_DoesNotAuthorizeFalseReplacement()
    {
        var fixture = SyntheticFixtureGenerator.GenerateFalsePositiveTrap(seed: 7);

        var report = FixtureEvaluationRunner.Evaluate(fixture);

        // The whole point of this fixture: a single low-confidence dissenting detector must not be
        // enough to authorize replacing real programming.
        Assert.Equal(0, report.FalseReplacementCount);
    }

    [Fact]
    public void Evaluate_SameFixtureTwice_ProducesIdenticalReport()
    {
        var fixture = SyntheticFixtureGenerator.GenerateCleanBreak(seed: 7);

        var first = FixtureEvaluationRunner.Evaluate(fixture);
        var second = FixtureEvaluationRunner.Evaluate(fixture);

        Assert.Equal(first.CommercialPrecision, second.CommercialPrecision);
        Assert.Equal(first.CommercialRecall, second.CommercialRecall);
        Assert.Equal(first.FalseReplacementCount, second.FalseReplacementCount);
        Assert.Equal(first.FalseReplacementDuration, second.FalseReplacementDuration);
    }

    [Fact]
    public void Evaluate_NullFixture_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FixtureEvaluationRunner.Evaluate(null!));
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
