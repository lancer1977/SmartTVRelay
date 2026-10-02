namespace SmartTVRelay.Core.Fixtures;

/// <summary>Configurable pass/fail thresholds for a <see cref="ScoringReport"/> (#27).</summary>
/// <param name="MinCommercialPrecision">Minimum acceptable <see cref="ScoringReport.CommercialPrecision"/>,
/// or <see langword="null"/> to not gate on precision at all.</param>
/// <param name="MinCommercialRecall">Minimum acceptable <see cref="ScoringReport.CommercialRecall"/>,
/// or <see langword="null"/> to not gate on recall at all.</param>
/// <param name="MaxFalseReplacementCount">Maximum acceptable <see cref="ScoringReport.FalseReplacementCount"/>.
/// Defaults to 0 -- AGENTS.md treats any false replacement as the most severe failure mode, so the
/// default gate tolerates none.</param>
public sealed record EvaluationThresholds(
    double? MinCommercialPrecision = 0.9,
    double? MinCommercialRecall = 0.9,
    int MaxFalseReplacementCount = 0);

public enum EvaluationGateResult
{
    Pass,
    Regression,
}

/// <summary>
/// Applies <see cref="EvaluationThresholds"/> to a <see cref="ScoringReport"/> (#27), distinguishing
/// a genuine metric regression (the evaluation ran fine, but the result doesn't meet the configured
/// bar) from an evaluation failure (the evaluation itself couldn't run) -- the latter is the caller's
/// concern (an exception from <see cref="FixtureEvaluationRunner"/>/<see cref="BroadcastScoringEngine"/>),
/// not something this type observes. A <see langword="null"/> precision/recall (undefined because
/// nothing was ever predicted/expected as Commercial) never fails the gate on its own -- there is no
/// data to judge, which is not the same as failing to meet a bar.
/// </summary>
public static class EvaluationGate
{
    public static (EvaluationGateResult Result, IReadOnlyList<string> Reasons) Evaluate(ScoringReport report, EvaluationThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(thresholds);

        var reasons = new List<string>();

        if (report.FalseReplacementCount > thresholds.MaxFalseReplacementCount)
        {
            reasons.Add($"False replacement count {report.FalseReplacementCount} exceeds maximum {thresholds.MaxFalseReplacementCount}.");
        }

        if (thresholds.MinCommercialPrecision is { } minPrecision && report.CommercialPrecision is { } precision && precision < minPrecision)
        {
            reasons.Add($"Commercial precision {precision:0.0000} is below minimum {minPrecision:0.0000}.");
        }

        if (thresholds.MinCommercialRecall is { } minRecall && report.CommercialRecall is { } recall && recall < minRecall)
        {
            reasons.Add($"Commercial recall {recall:0.0000} is below minimum {minRecall:0.0000}.");
        }

        return reasons.Count == 0
            ? (EvaluationGateResult.Pass, reasons)
            : (EvaluationGateResult.Regression, reasons);
    }
}
