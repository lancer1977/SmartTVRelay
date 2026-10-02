namespace SmartTVRelay.Core.Tests.Fixtures;

using SmartTVRelay.Core;
using SmartTVRelay.Core.Fixtures;
using Xunit;

public class BroadcastScoringEngineTests
{
    private static readonly TimeSpan Zero = TimeSpan.Zero;

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Score_PerfectMatch_ReportsFullPrecisionRecallAndNoErrors()
    {
        var expected = new List<ExpectedSegment>
        {
            new(S(0), S(30), BroadcastState.Program),
            new(S(30), S(90), BroadcastState.Commercial),
            new(S(90), S(120), BroadcastState.Program),
        };
        var predicted = new List<PredictedSegment>
        {
            new(S(0), S(30), BroadcastState.Program),
            new(S(30), S(90), BroadcastState.Commercial),
            new(S(90), S(120), BroadcastState.Program),
        };

        var report = BroadcastScoringEngine.Score(expected, predicted);

        Assert.Equal(1.0, report.CommercialPrecision);
        Assert.Equal(1.0, report.CommercialRecall);
        Assert.Equal(Zero, report.MissedCommercialDuration);
        Assert.Equal(0, report.FalseReplacementCount);
        Assert.Equal(Zero, report.FalseReplacementDuration);

        Assert.Equal(3, report.BoundaryErrors.Count);
        Assert.All(report.BoundaryErrors, e => Assert.Equal(Zero, e.StartError));
        Assert.All(report.BoundaryErrors, e => Assert.Equal(Zero, e.EndError));

        Assert.Equal(2, report.ConfusionCounts[(BroadcastState.Program, BroadcastState.Program)]);
        Assert.Equal(1, report.ConfusionCounts[(BroadcastState.Commercial, BroadcastState.Commercial)]);
        Assert.Equal(3, report.ConfusionCounts.Values.Sum());
    }

    [Fact]
    public void Score_LateCommercialStart_ReportsPositiveStartErrorAndPartialMiss()
    {
        var expected = new List<ExpectedSegment>
        {
            new(S(0), S(30), BroadcastState.Program),
            new(S(30), S(90), BroadcastState.Commercial),
            new(S(90), S(120), BroadcastState.Program),
        };
        var predicted = new List<PredictedSegment>
        {
            new(S(0), S(35), BroadcastState.Program),
            new(S(35), S(90), BroadcastState.Commercial),
            new(S(90), S(120), BroadcastState.Program),
        };

        var report = BroadcastScoringEngine.Score(expected, predicted);

        var commercialError = Assert.Single(report.BoundaryErrors, e => e.Expected.ExpectedState == BroadcastState.Commercial);
        Assert.Equal(S(5), commercialError.StartError);
        Assert.Equal(Zero, commercialError.EndError);

        Assert.Equal(S(5), report.MissedCommercialDuration);
        Assert.Equal(1.0, report.CommercialPrecision);
        Assert.Equal(55.0 / 60.0, report.CommercialRecall!.Value, precision: 10);
        Assert.Equal(0, report.FalseReplacementCount);
    }

    [Fact]
    public void Score_EarlyCommercialEnd_ReportsNegativeEndErrorAndPartialMiss()
    {
        var expected = new List<ExpectedSegment>
        {
            new(S(0), S(30), BroadcastState.Program),
            new(S(30), S(90), BroadcastState.Commercial),
            new(S(90), S(120), BroadcastState.Program),
        };
        var predicted = new List<PredictedSegment>
        {
            new(S(0), S(30), BroadcastState.Program),
            new(S(30), S(85), BroadcastState.Commercial),
            new(S(85), S(120), BroadcastState.Program),
        };

        var report = BroadcastScoringEngine.Score(expected, predicted);

        var commercialError = Assert.Single(report.BoundaryErrors, e => e.Expected.ExpectedState == BroadcastState.Commercial);
        Assert.Equal(Zero, commercialError.StartError);
        Assert.Equal(S(-5), commercialError.EndError);

        Assert.Equal(S(5), report.MissedCommercialDuration);
        Assert.Equal(1.0, report.CommercialPrecision);
        Assert.Equal(55.0 / 60.0, report.CommercialRecall!.Value, precision: 10);
        Assert.Equal(0, report.FalseReplacementCount);
    }

    [Fact]
    public void Score_CommercialNeverPredicted_IsAFullMissWithNullPrecision()
    {
        var expected = new List<ExpectedSegment>
        {
            new(S(0), S(30), BroadcastState.Program),
            new(S(30), S(90), BroadcastState.Commercial),
            new(S(90), S(120), BroadcastState.Program),
        };
        var predicted = new List<PredictedSegment>
        {
            new(S(0), S(120), BroadcastState.Program),
        };

        var report = BroadcastScoringEngine.Score(expected, predicted);

        Assert.Null(report.CommercialPrecision);
        Assert.Equal(0.0, report.CommercialRecall);
        Assert.Equal(S(60), report.MissedCommercialDuration);
        Assert.Equal(0, report.FalseReplacementCount);
        Assert.Equal(Zero, report.FalseReplacementDuration);

        Assert.DoesNotContain(report.BoundaryErrors, e => e.Expected.ExpectedState == BroadcastState.Commercial);
        Assert.Equal(2, report.ConfusionCounts[(BroadcastState.Program, BroadcastState.Program)]);
        Assert.Equal(1, report.ConfusionCounts[(BroadcastState.Commercial, BroadcastState.Program)]);
    }

    [Fact]
    public void Score_SpuriousCommercialInRealProgramming_IsFlaggedAsFalseReplacement()
    {
        var expected = new List<ExpectedSegment>
        {
            new(S(0), S(60), BroadcastState.Program),
        };
        var predicted = new List<PredictedSegment>
        {
            new(S(0), S(20), BroadcastState.Program),
            new(S(20), S(30), BroadcastState.Commercial),
            new(S(30), S(60), BroadcastState.Program),
        };

        var report = BroadcastScoringEngine.Score(expected, predicted);

        // This is the failure mode AGENTS.md treats as most severe: real programming replaced.
        Assert.Equal(1, report.FalseReplacementCount);
        Assert.Equal(S(10), report.FalseReplacementDuration);
        Assert.Equal(Zero, report.MissedCommercialDuration);
        Assert.Null(report.CommercialRecall);
        Assert.Equal(0.0, report.CommercialPrecision);

        Assert.Equal(2, report.ConfusionCounts[(BroadcastState.Program, BroadcastState.Program)]);
        Assert.Equal(1, report.ConfusionCounts[(BroadcastState.Program, BroadcastState.Commercial)]);
    }

    [Fact]
    public void Score_AmbiguousTransitionPreservedAsProgram_IsNotAFalseReplacement()
    {
        // Mirrors SyntheticFixtureGenerator.GenerateAmbiguousTransition's shape: a genuinely
        // ambiguous window is labeled Transition in ground truth. A policy that conservatively
        // stays on Program through it -- per AGENTS.md's "preserve the original broadcast when
        // evidence is disputed" rule -- must not be penalized as a false replacement.
        var expected = new List<ExpectedSegment>
        {
            new(S(0), S(10), BroadcastState.Program),
            new(S(10), S(15), BroadcastState.Transition),
            new(S(15), S(30), BroadcastState.Commercial),
        };
        var predicted = new List<PredictedSegment>
        {
            new(S(0), S(15), BroadcastState.Program),
            new(S(15), S(30), BroadcastState.Commercial),
        };

        var report = BroadcastScoringEngine.Score(expected, predicted);

        Assert.Equal(0, report.FalseReplacementCount);
        Assert.Equal(Zero, report.FalseReplacementDuration);
        Assert.Equal(Zero, report.MissedCommercialDuration);
        Assert.Equal(1, report.ConfusionCounts[(BroadcastState.Transition, BroadcastState.Program)]);
    }

    [Fact]
    public void Score_AmbiguousTransitionReplaced_IsFlaggedAsFalseReplacement()
    {
        // The unsafe counterpart to the test above: a policy that authorizes replacement DURING
        // the genuinely ambiguous window must be caught, since that's exactly the case AGENTS.md's
        // safety rule exists to guard against.
        var expected = new List<ExpectedSegment>
        {
            new(S(0), S(10), BroadcastState.Program),
            new(S(10), S(15), BroadcastState.Transition),
            new(S(15), S(30), BroadcastState.Commercial),
        };
        var predicted = new List<PredictedSegment>
        {
            new(S(0), S(10), BroadcastState.Program),
            new(S(10), S(30), BroadcastState.Commercial),
        };

        var report = BroadcastScoringEngine.Score(expected, predicted);

        Assert.Equal(1, report.FalseReplacementCount);
        Assert.Equal(S(5), report.FalseReplacementDuration);
        Assert.Equal(1, report.ConfusionCounts[(BroadcastState.Transition, BroadcastState.Commercial)]);
    }

    [Fact]
    public void Score_SameInputsTwice_ProducesIdenticalReport()
    {
        var expected = new List<ExpectedSegment>
        {
            new(S(0), S(60), BroadcastState.Program),
        };
        var predicted = new List<PredictedSegment>
        {
            new(S(0), S(20), BroadcastState.Program),
            new(S(20), S(30), BroadcastState.Commercial),
            new(S(30), S(60), BroadcastState.Program),
        };

        var first = BroadcastScoringEngine.Score(expected, predicted);
        var second = BroadcastScoringEngine.Score(expected, predicted);

        Assert.Equal(first.CommercialPrecision, second.CommercialPrecision);
        Assert.Equal(first.CommercialRecall, second.CommercialRecall);
        Assert.Equal(first.MissedCommercialDuration, second.MissedCommercialDuration);
        Assert.Equal(first.FalseReplacementCount, second.FalseReplacementCount);
        Assert.Equal(first.FalseReplacementDuration, second.FalseReplacementDuration);
        Assert.Equal(first.BoundaryErrors, second.BoundaryErrors);

        Assert.Equal(
            first.ConfusionCounts.OrderBy(kv => kv.Key),
            second.ConfusionCounts.OrderBy(kv => kv.Key));
    }

    [Fact]
    public void Score_EmptyExpectedTimeline_ReturnsEmptyReportWithoutThrowing()
    {
        var report = BroadcastScoringEngine.Score(
            Array.Empty<ExpectedSegment>(),
            new[] { new PredictedSegment(S(0), S(30), BroadcastState.Program) });

        Assert.Null(report.CommercialPrecision);
        Assert.Null(report.CommercialRecall);
        Assert.Empty(report.BoundaryErrors);
        Assert.Equal(Zero, report.MissedCommercialDuration);
        Assert.Equal(0, report.FalseReplacementCount);
        Assert.Empty(report.ConfusionCounts);
    }
}
