namespace SmartTVRelay.Core.Fixtures;

using System.Linq;

public sealed record FixtureReport(string FixtureName, IReadOnlyList<SegmentEvaluation> Segments)
{
    public int CorrectCount => Segments.Count(s => s.Outcome == SegmentOutcome.Correct);
    public int FalseReplacementCount => Segments.Count(s => s.Outcome == SegmentOutcome.FalseReplacement);
    public int MissedCommercialCount => Segments.Count(s => s.Outcome == SegmentOutcome.MissedCommercial);
}
