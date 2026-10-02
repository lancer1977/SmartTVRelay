using System.Globalization;
using SmartTVRelay.Core.Fixtures;
using SmartTVRelay.Evaluation;

// Exit codes distinguish "the evaluation itself couldn't run" (2, an evaluation failure -- a bug in
// the harness/tooling) from "the evaluation ran fine but didn't meet the bar" (1, a metric
// regression) from success (0). Per #27's acceptance criteria, these must never be conflated: a CI
// consumer needs to react very differently to "our tooling is broken" than to "the branch made
// commercial detection worse."
try
{
    var options = EvaluationCliOptions.Parse(args);
    if (options.Mode == "live-smoke")
    {
        using var client = new HttpClient();
        var result = await new LiveSmokeRunner().RunAsync(options, client);
        Console.WriteLine(result.Summary);
        return result.ExitCode;
    }

    Directory.CreateDirectory(options.OutputDirectory);

    var thresholds = new EvaluationThresholds(options.MinCommercialPrecision, options.MinCommercialRecall, options.MaxFalseReplacementCount);
    var generatedAt = DateTimeOffset.UtcNow;

    // Fixed, checked-in synthetic corpus only -- no live hardware/network dependency, per #27's
    // acceptance criteria and AGENTS.md's hardware/live-stream rule.
    var fixtures = SyntheticFixtureGenerator.GenerateAll(options.Seed);

    var anyRegression = false;

    foreach (var fixture in fixtures)
    {
        var scoring = FixtureEvaluationRunner.Evaluate(fixture);
        var run = new EvaluationRun(fixture.Name, fixture.SchemaVersion.ToString(CultureInfo.InvariantCulture), generatedAt, scoring);

        var slug = Slugify(fixture.Name);
        File.WriteAllText(Path.Combine(options.OutputDirectory, $"{slug}.json"), EvaluationReportGenerator.ToJson(run));
        File.WriteAllText(Path.Combine(options.OutputDirectory, $"{slug}.md"), EvaluationReportGenerator.ToMarkdown(run));

        var (result, reasons) = EvaluationGate.Evaluate(scoring, thresholds);
        if (result == EvaluationGateResult.Regression)
        {
            anyRegression = true;
            Console.WriteLine($"REGRESSION: {fixture.Name}");
            foreach (var reason in reasons)
            {
                Console.WriteLine($"  - {reason}");
            }
        }
        else
        {
            Console.WriteLine($"PASS: {fixture.Name}");
        }
    }

    return anyRegression ? 1 : 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("EVALUATION FAILURE: fixture evaluation could not run.");
    Console.Error.WriteLine(ex);
    return 2;
}

static string Slugify(string name) => name
    .ToLowerInvariant()
    .Replace(' ', '-');
