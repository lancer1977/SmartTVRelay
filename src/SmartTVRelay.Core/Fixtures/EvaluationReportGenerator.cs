namespace SmartTVRelay.Core.Fixtures;

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Renders a <see cref="ScoringReport"/> (#24) as JSON and Markdown evaluation artifacts (#26).
/// Pure formatting: consumes scoring results, never recomputes them -- #26's own agent boundary.
/// Every output is a deterministic function of its inputs alone (no wall-clock capture, no
/// culture-dependent number formatting, confusion counts sorted before rendering rather than
/// relying on dictionary iteration order), so the same inputs always produce byte-identical text --
/// what makes snapshot-testing this format meaningful at all.
/// </summary>
public static class EvaluationReportGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson(EvaluationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return BuildRunJson(run).ToJsonString(JsonOptions);
    }

    public static string ToJson(EvaluationComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var root = new JsonObject
        {
            ["baseline"] = BuildRunJson(comparison.Baseline),
            ["current"] = BuildRunJson(comparison.Current),
            ["delta"] = BuildDeltaJson(comparison),
        };

        return root.ToJsonString(JsonOptions);
    }

    public static string ToMarkdown(EvaluationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var sb = new StringBuilder();
        sb.AppendLine($"# Evaluation Report: {run.FixtureSetName}");
        sb.AppendLine();
        sb.AppendLine($"Version: `{run.FixtureSetVersion}`");
        sb.AppendLine();
        sb.AppendLine($"Generated: {FormatInstant(run.GeneratedAt)}");
        sb.AppendLine();

        AppendMetricsTable(sb, run.Scoring);
        AppendFalseReplacementSection(sb, run.Scoring, regressionMarker: string.Empty);
        AppendBoundaryErrorsTable(sb, run.Scoring);
        AppendConfusionMatrix(sb, run.Scoring);

        return sb.ToString();
    }

    public static string ToMarkdown(EvaluationComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var baseline = comparison.Baseline;
        var current = comparison.Current;
        var sb = new StringBuilder();

        sb.AppendLine($"# Evaluation Comparison: {current.FixtureSetName}");
        sb.AppendLine();
        sb.AppendLine($"Baseline: `{baseline.FixtureSetVersion}` ({FormatInstant(baseline.GeneratedAt)})");
        sb.AppendLine();
        sb.AppendLine($"Current: `{current.FixtureSetVersion}` ({FormatInstant(current.GeneratedAt)})");
        sb.AppendLine();

        sb.AppendLine("## Metrics");
        sb.AppendLine();
        sb.AppendLine("| Metric | Baseline | Current | Delta |");
        sb.AppendLine("| --- | --- | --- | --- |");
        AppendMetricComparisonRow(sb, "Commercial Precision", baseline.Scoring.CommercialPrecision, current.Scoring.CommercialPrecision);
        AppendMetricComparisonRow(sb, "Commercial Recall", baseline.Scoring.CommercialRecall, current.Scoring.CommercialRecall);
        var missedDelta = current.Scoring.MissedCommercialDuration - baseline.Scoring.MissedCommercialDuration;
        sb.AppendLine($"| Missed Commercial Duration | {FormatDuration(baseline.Scoring.MissedCommercialDuration)} | {FormatDuration(current.Scoring.MissedCommercialDuration)} | {FormatDurationDelta(missedDelta)} |");
        sb.AppendLine();

        var countDelta = current.Scoring.FalseReplacementCount - baseline.Scoring.FalseReplacementCount;
        var durationDelta = current.Scoring.FalseReplacementDuration - baseline.Scoring.FalseReplacementDuration;
        var regressionMarker = countDelta > 0 || durationDelta > TimeSpan.Zero ? " -- ⚠ REGRESSION" : string.Empty;

        sb.AppendLine($"## ⚠ False Replacement (most severe failure mode){regressionMarker}");
        sb.AppendLine();
        sb.AppendLine("| | Baseline | Current | Delta |");
        sb.AppendLine("| --- | --- | --- | --- |");
        sb.AppendLine($"| Count | {baseline.Scoring.FalseReplacementCount} | {current.Scoring.FalseReplacementCount} | {FormatIntDelta(countDelta)} |");
        sb.AppendLine($"| Duration | {FormatDuration(baseline.Scoring.FalseReplacementDuration)} | {FormatDuration(current.Scoring.FalseReplacementDuration)} | {FormatDurationDelta(durationDelta)} |");

        return sb.ToString();
    }

    private static JsonObject BuildRunJson(EvaluationRun run)
    {
        var scoring = run.Scoring;
        return new JsonObject
        {
            ["fixtureSetName"] = run.FixtureSetName,
            ["fixtureSetVersion"] = run.FixtureSetVersion,
            ["generatedAt"] = FormatInstant(run.GeneratedAt),
            ["commercialPrecision"] = scoring.CommercialPrecision is { } p ? JsonValue.Create(p) : null,
            ["commercialRecall"] = scoring.CommercialRecall is { } r ? JsonValue.Create(r) : null,
            ["missedCommercialDurationSeconds"] = scoring.MissedCommercialDuration.TotalSeconds,
            ["falseReplacementCount"] = scoring.FalseReplacementCount,
            ["falseReplacementDurationSeconds"] = scoring.FalseReplacementDuration.TotalSeconds,
            ["boundaryErrors"] = BuildBoundaryErrorsJson(scoring.BoundaryErrors),
            ["confusionCounts"] = BuildConfusionCountsJson(scoring.ConfusionCounts),
        };
    }

    private static JsonArray BuildBoundaryErrorsJson(IReadOnlyList<SegmentBoundaryError> errors)
    {
        var array = new JsonArray();
        foreach (var error in errors)
        {
            array.Add(new JsonObject
            {
                ["expectedStartSeconds"] = error.Expected.Start.TotalSeconds,
                ["expectedEndSeconds"] = error.Expected.End.TotalSeconds,
                ["expectedState"] = error.Expected.ExpectedState.ToString(),
                ["predictedStartSeconds"] = error.Predicted.Start.TotalSeconds,
                ["predictedEndSeconds"] = error.Predicted.End.TotalSeconds,
                ["startErrorSeconds"] = error.StartError.TotalSeconds,
                ["endErrorSeconds"] = error.EndError.TotalSeconds,
            });
        }

        return array;
    }

    private static JsonArray BuildConfusionCountsJson(IReadOnlyDictionary<(BroadcastState Expected, BroadcastState Predicted), int> counts)
    {
        var array = new JsonArray();
        foreach (var entry in counts.OrderBy(kv => kv.Key.Expected).ThenBy(kv => kv.Key.Predicted))
        {
            array.Add(new JsonObject
            {
                ["expected"] = entry.Key.Expected.ToString(),
                ["predicted"] = entry.Key.Predicted.ToString(),
                ["count"] = entry.Value,
            });
        }

        return array;
    }

    private static JsonObject BuildDeltaJson(EvaluationComparison comparison)
    {
        var b = comparison.Baseline.Scoring;
        var c = comparison.Current.Scoring;

        double? precisionDelta = b.CommercialPrecision is { } bp && c.CommercialPrecision is { } cp ? cp - bp : null;
        double? recallDelta = b.CommercialRecall is { } br && c.CommercialRecall is { } cr ? cr - br : null;

        return new JsonObject
        {
            ["commercialPrecisionDelta"] = precisionDelta is { } pd ? JsonValue.Create(pd) : null,
            ["commercialRecallDelta"] = recallDelta is { } rd ? JsonValue.Create(rd) : null,
            ["missedCommercialDurationDeltaSeconds"] = (c.MissedCommercialDuration - b.MissedCommercialDuration).TotalSeconds,
            ["falseReplacementCountDelta"] = c.FalseReplacementCount - b.FalseReplacementCount,
            ["falseReplacementDurationDeltaSeconds"] = (c.FalseReplacementDuration - b.FalseReplacementDuration).TotalSeconds,
        };
    }

    private static void AppendMetricsTable(StringBuilder sb, ScoringReport scoring)
    {
        sb.AppendLine("## Metrics");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| Commercial Precision | {FormatRatio(scoring.CommercialPrecision)} |");
        sb.AppendLine($"| Commercial Recall | {FormatRatio(scoring.CommercialRecall)} |");
        sb.AppendLine($"| Missed Commercial Duration | {FormatDuration(scoring.MissedCommercialDuration)} |");
        sb.AppendLine();
    }

    private static void AppendFalseReplacementSection(StringBuilder sb, ScoringReport scoring, string regressionMarker)
    {
        sb.AppendLine($"## ⚠ False Replacement (most severe failure mode){regressionMarker}");
        sb.AppendLine();
        sb.AppendLine("| Count | Duration |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine($"| {scoring.FalseReplacementCount} | {FormatDuration(scoring.FalseReplacementDuration)} |");
        sb.AppendLine();
    }

    private static void AppendBoundaryErrorsTable(StringBuilder sb, ScoringReport scoring)
    {
        sb.AppendLine("## Boundary Errors");
        sb.AppendLine();

        if (scoring.BoundaryErrors.Count == 0)
        {
            sb.AppendLine("_None._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Expected Segment | State | Start Error | End Error |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var error in scoring.BoundaryErrors)
        {
            sb.AppendLine($"| [{FormatDuration(error.Expected.Start)}, {FormatDuration(error.Expected.End)}) | {error.Expected.ExpectedState} | {FormatDurationDelta(error.StartError)} | {FormatDurationDelta(error.EndError)} |");
        }

        sb.AppendLine();
    }

    private static void AppendConfusionMatrix(StringBuilder sb, ScoringReport scoring)
    {
        sb.AppendLine("## Confusion Counts (expected vs. predicted)");
        sb.AppendLine();

        if (scoring.ConfusionCounts.Count == 0)
        {
            sb.AppendLine("_None._");
            return;
        }

        sb.AppendLine("| Expected | Predicted | Count |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (var entry in scoring.ConfusionCounts.OrderBy(kv => kv.Key.Expected).ThenBy(kv => kv.Key.Predicted))
        {
            sb.AppendLine($"| {entry.Key.Expected} | {entry.Key.Predicted} | {entry.Value} |");
        }
    }

    private static void AppendMetricComparisonRow(StringBuilder sb, string label, double? baseline, double? current)
    {
        var delta = baseline is { } b && current is { } c ? (c - b).ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture) : "N/A";
        sb.AppendLine($"| {label} | {FormatRatio(baseline)} | {FormatRatio(current)} | {delta} |");
    }

    private static string FormatRatio(double? value) => value is { } v ? v.ToString("0.0000", CultureInfo.InvariantCulture) : "N/A";

    private static string FormatDuration(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

    private static string FormatDurationDelta(TimeSpan value)
    {
        var sign = value < TimeSpan.Zero ? "-" : "+";
        return sign + FormatDuration(value.Duration());
    }

    private static string FormatIntDelta(int value) => value >= 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);

    private static string FormatInstant(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
