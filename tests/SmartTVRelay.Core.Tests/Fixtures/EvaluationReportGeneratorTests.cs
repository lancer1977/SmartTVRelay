namespace SmartTVRelay.Core.Tests.Fixtures;

using SmartTVRelay.Core;
using SmartTVRelay.Core.Fixtures;
using Xunit;

public class EvaluationReportGeneratorTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static readonly DateTimeOffset FixedInstant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ScoringReport CleanScoring() => new(
        CommercialPrecision: 1.0,
        CommercialRecall: 1.0,
        BoundaryErrors: Array.Empty<SegmentBoundaryError>(),
        MissedCommercialDuration: TimeSpan.Zero,
        FalseReplacementCount: 0,
        FalseReplacementDuration: TimeSpan.Zero,
        ConfusionCounts: new Dictionary<(BroadcastState, BroadcastState), int>
        {
            [(BroadcastState.Program, BroadcastState.Program)] = 2,
            [(BroadcastState.Commercial, BroadcastState.Commercial)] = 1,
        });

    private static ScoringReport ScoringWithFalseReplacement()
    {
        var expected = new ExpectedSegment(S(0), S(60), BroadcastState.Program);
        var predicted = new PredictedSegment(S(20), S(30), BroadcastState.Commercial);

        return new ScoringReport(
            CommercialPrecision: 0.0,
            CommercialRecall: null,
            BoundaryErrors: [new SegmentBoundaryError(expected, predicted, S(20), S(-30))],
            MissedCommercialDuration: TimeSpan.Zero,
            FalseReplacementCount: 1,
            FalseReplacementDuration: S(10),
            ConfusionCounts: new Dictionary<(BroadcastState, BroadcastState), int>
            {
                [(BroadcastState.Program, BroadcastState.Program)] = 2,
                [(BroadcastState.Program, BroadcastState.Commercial)] = 1,
            });
    }

    [Fact]
    public void ToJson_Run_SameInputsTwice_ProducesIdenticalOutput()
    {
        var run = new EvaluationRun("Synthetic Clean Break", "v1", FixedInstant, CleanScoring());

        Assert.Equal(EvaluationReportGenerator.ToJson(run), EvaluationReportGenerator.ToJson(run));
    }

    [Fact]
    public void ToMarkdown_Run_SameInputsTwice_ProducesIdenticalOutput()
    {
        var run = new EvaluationRun("Synthetic Clean Break", "v1", FixedInstant, CleanScoring());

        Assert.Equal(EvaluationReportGenerator.ToMarkdown(run), EvaluationReportGenerator.ToMarkdown(run));
    }

    [Fact]
    public void ToJson_Run_IsStableSnapshot()
    {
        var run = new EvaluationRun("Synthetic Clean Break", "v1", FixedInstant, CleanScoring());

        var json = EvaluationReportGenerator.ToJson(run);

        Assert.Equal(
            """
            {
              "fixtureSetName": "Synthetic Clean Break",
              "fixtureSetVersion": "v1",
              "generatedAt": "2026-01-01T00:00:00.0000000+00:00",
              "commercialPrecision": 1,
              "commercialRecall": 1,
              "missedCommercialDurationSeconds": 0,
              "falseReplacementCount": 0,
              "falseReplacementDurationSeconds": 0,
              "boundaryErrors": [],
              "confusionCounts": [
                {
                  "expected": "Program",
                  "predicted": "Program",
                  "count": 2
                },
                {
                  "expected": "Commercial",
                  "predicted": "Commercial",
                  "count": 1
                }
              ]
            }
            """,
            json,
            ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void ToMarkdown_Run_IsStableSnapshot()
    {
        var run = new EvaluationRun("Synthetic Clean Break", "v1", FixedInstant, CleanScoring());

        var markdown = EvaluationReportGenerator.ToMarkdown(run);

        Assert.Equal(
            """
            # Evaluation Report: Synthetic Clean Break

            Version: `v1`

            Generated: 2026-01-01T00:00:00.0000000+00:00

            ## Metrics

            | Metric | Value |
            | --- | --- |
            | Commercial Precision | 1.0000 |
            | Commercial Recall | 1.0000 |
            | Missed Commercial Duration | 00:00:00 |

            ## ⚠ False Replacement (most severe failure mode)

            | Count | Duration |
            | --- | --- |
            | 0 | 00:00:00 |

            ## Boundary Errors

            _None._

            ## Confusion Counts (expected vs. predicted)

            | Expected | Predicted | Count |
            | --- | --- | --- |
            | Program | Program | 2 |
            | Commercial | Commercial | 1 |

            """.Replace("\\u26a0", "⚠"),
            markdown,
            ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void ToMarkdown_Run_WithFalseReplacement_HighlightsItSeparately()
    {
        var run = new EvaluationRun("False Positive Trap", "v1", FixedInstant, ScoringWithFalseReplacement());

        var markdown = EvaluationReportGenerator.ToMarkdown(run);

        Assert.Contains("## ⚠ False Replacement (most severe failure mode)", markdown);
        Assert.Contains("| 1 | 00:00:10 |", markdown);
        Assert.Contains("| [00:00:00, 00:01:00) | Program |", markdown);
    }

    [Fact]
    public void ToJson_Comparison_IncludesBaselineCurrentAndDelta()
    {
        var baseline = new EvaluationRun("Fixture Set", "v1", FixedInstant, CleanScoring());
        var current = new EvaluationRun("Fixture Set", "v2", FixedInstant.AddDays(1), ScoringWithFalseReplacement());
        var comparison = new EvaluationComparison(baseline, current);

        var json = EvaluationReportGenerator.ToJson(comparison);
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(json)!;

        Assert.Equal("v1", parsed["baseline"]!["fixtureSetVersion"]!.GetValue<string>());
        Assert.Equal("v2", parsed["current"]!["fixtureSetVersion"]!.GetValue<string>());
        Assert.Equal(1, parsed["delta"]!["falseReplacementCountDelta"]!.GetValue<int>());
        Assert.Equal(10.0, parsed["delta"]!["falseReplacementDurationDeltaSeconds"]!.GetValue<double>());
        Assert.Equal(-1.0, parsed["delta"]!["commercialPrecisionDelta"]!.GetValue<double>());
        Assert.Null(parsed["delta"]!["commercialRecallDelta"]);
    }

    [Fact]
    public void ToMarkdown_Comparison_FlagsFalseReplacementRegression()
    {
        var baseline = new EvaluationRun("Fixture Set", "v1", FixedInstant, CleanScoring());
        var current = new EvaluationRun("Fixture Set", "v2", FixedInstant.AddDays(1), ScoringWithFalseReplacement());
        var comparison = new EvaluationComparison(baseline, current);

        var markdown = EvaluationReportGenerator.ToMarkdown(comparison);

        Assert.Contains("REGRESSION", markdown);
        Assert.Contains("| Count | 0 | 1 | +1 |", markdown);
    }

    [Fact]
    public void ToMarkdown_Comparison_NoRegression_DoesNotFlag()
    {
        var baseline = new EvaluationRun("Fixture Set", "v1", FixedInstant, CleanScoring());
        var current = new EvaluationRun("Fixture Set", "v2", FixedInstant.AddDays(1), CleanScoring());
        var comparison = new EvaluationComparison(baseline, current);

        var markdown = EvaluationReportGenerator.ToMarkdown(comparison);

        Assert.DoesNotContain("REGRESSION", markdown);
    }

    [Fact]
    public void ToJson_NullPrecisionAndRecall_SerializeAsJsonNull()
    {
        var scoring = new ScoringReport(
            CommercialPrecision: null,
            CommercialRecall: null,
            BoundaryErrors: Array.Empty<SegmentBoundaryError>(),
            MissedCommercialDuration: TimeSpan.Zero,
            FalseReplacementCount: 0,
            FalseReplacementDuration: TimeSpan.Zero,
            ConfusionCounts: new Dictionary<(BroadcastState, BroadcastState), int>());
        var run = new EvaluationRun("Empty", "v1", FixedInstant, scoring);

        var json = EvaluationReportGenerator.ToJson(run);
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(json)!;

        Assert.Null(parsed["commercialPrecision"]);
        Assert.Null(parsed["commercialRecall"]);
    }
}
