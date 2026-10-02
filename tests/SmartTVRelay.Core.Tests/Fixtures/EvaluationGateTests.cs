namespace SmartTVRelay.Core.Tests.Fixtures;

using SmartTVRelay.Core;
using SmartTVRelay.Core.Fixtures;
using Xunit;

public class EvaluationGateTests
{
    private static ScoringReport Report(double? precision, double? recall, int falseReplacementCount) => new(
        CommercialPrecision: precision,
        CommercialRecall: recall,
        BoundaryErrors: Array.Empty<SegmentBoundaryError>(),
        MissedCommercialDuration: TimeSpan.Zero,
        FalseReplacementCount: falseReplacementCount,
        FalseReplacementDuration: TimeSpan.Zero,
        ConfusionCounts: new Dictionary<(BroadcastState, BroadcastState), int>());

    [Fact]
    public void Evaluate_AllMetricsMeetThresholds_Passes()
    {
        var report = Report(precision: 0.95, recall: 0.95, falseReplacementCount: 0);
        var thresholds = new EvaluationThresholds(MinCommercialPrecision: 0.9, MinCommercialRecall: 0.9, MaxFalseReplacementCount: 0);

        var (result, reasons) = EvaluationGate.Evaluate(report, thresholds);

        Assert.Equal(EvaluationGateResult.Pass, result);
        Assert.Empty(reasons);
    }

    [Fact]
    public void Evaluate_AnyFalseReplacement_IsARegressionByDefault()
    {
        var report = Report(precision: 1.0, recall: 1.0, falseReplacementCount: 1);
        var thresholds = new EvaluationThresholds();

        var (result, reasons) = EvaluationGate.Evaluate(report, thresholds);

        Assert.Equal(EvaluationGateResult.Regression, result);
        Assert.Contains(reasons, r => r.Contains("False replacement"));
    }

    [Fact]
    public void Evaluate_PrecisionBelowThreshold_IsARegression()
    {
        var report = Report(precision: 0.5, recall: 0.95, falseReplacementCount: 0);
        var thresholds = new EvaluationThresholds(MinCommercialPrecision: 0.9, MinCommercialRecall: 0.9, MaxFalseReplacementCount: 0);

        var (result, reasons) = EvaluationGate.Evaluate(report, thresholds);

        Assert.Equal(EvaluationGateResult.Regression, result);
        Assert.Contains(reasons, r => r.Contains("precision"));
    }

    [Fact]
    public void Evaluate_RecallBelowThreshold_IsARegression()
    {
        var report = Report(precision: 0.95, recall: 0.5, falseReplacementCount: 0);
        var thresholds = new EvaluationThresholds(MinCommercialPrecision: 0.9, MinCommercialRecall: 0.9, MaxFalseReplacementCount: 0);

        var (result, reasons) = EvaluationGate.Evaluate(report, thresholds);

        Assert.Equal(EvaluationGateResult.Regression, result);
        Assert.Contains(reasons, r => r.Contains("recall"));
    }

    [Fact]
    public void Evaluate_NullPrecisionAndRecall_DoesNotFailTheGateOnItsOwn()
    {
        var report = Report(precision: null, recall: null, falseReplacementCount: 0);
        var thresholds = new EvaluationThresholds(MinCommercialPrecision: 0.9, MinCommercialRecall: 0.9, MaxFalseReplacementCount: 0);

        var (result, reasons) = EvaluationGate.Evaluate(report, thresholds);

        Assert.Equal(EvaluationGateResult.Pass, result);
        Assert.Empty(reasons);
    }

    [Fact]
    public void Evaluate_ThresholdsSetToNull_DisablesThatCheck()
    {
        var report = Report(precision: 0.1, recall: 0.1, falseReplacementCount: 0);
        var thresholds = new EvaluationThresholds(MinCommercialPrecision: null, MinCommercialRecall: null, MaxFalseReplacementCount: 0);

        var (result, reasons) = EvaluationGate.Evaluate(report, thresholds);

        Assert.Equal(EvaluationGateResult.Pass, result);
    }

    [Fact]
    public void Evaluate_MultipleViolations_ReportsAllReasons()
    {
        var report = Report(precision: 0.1, recall: 0.1, falseReplacementCount: 3);
        var thresholds = new EvaluationThresholds(MinCommercialPrecision: 0.9, MinCommercialRecall: 0.9, MaxFalseReplacementCount: 0);

        var (result, reasons) = EvaluationGate.Evaluate(report, thresholds);

        Assert.Equal(EvaluationGateResult.Regression, result);
        Assert.Equal(3, reasons.Count);
    }
}
