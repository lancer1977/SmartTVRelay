namespace SmartTVRelay.Core.Fixtures;

/// <summary>
/// One named, versioned scoring result (#26). <see cref="GeneratedAt"/> is supplied by the caller
/// rather than captured internally, so <see cref="EvaluationReportGenerator"/> itself has no
/// wall-clock dependency and stays a pure function of its inputs.
/// </summary>
public sealed record EvaluationRun(
    string FixtureSetName,
    string FixtureSetVersion,
    DateTimeOffset GeneratedAt,
    ScoringReport Scoring);

/// <summary>Two evaluation runs to compare -- e.g. a CI baseline against the current branch's result.</summary>
public sealed record EvaluationComparison(EvaluationRun Baseline, EvaluationRun Current);
