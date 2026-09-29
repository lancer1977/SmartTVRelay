namespace SmartTVRelay.Core.Fixtures;

/// <summary>
/// Generates deterministic synthetic <see cref="BroadcastFixture"/> values for the four scenario
/// shapes #23 requires, without any live hardware/network dependency -- pure in-memory record
/// construction, matching the hand-authored samples under fixtures/samples/
/// (program-commercial-program.json, ambiguous-transition.json) but produced programmatically so
/// new variations don't require hand-editing JSON. This class only builds fixture data: it does
/// not score, ingest, or run any real detector (#23's own agent boundary).
/// </summary>
public static class SyntheticFixtureGenerator
{
    private const string SourceKind = "heuristic";
    private const string DetectorOne = "detector-1";
    private const string DetectorTwo = "detector-2";

    /// <summary>
    /// A clean Program -&gt; Commercial -&gt; Program transition: two detectors agreeing at high
    /// confidence at every emission, mirroring fixtures/samples/program-commercial-program.json.
    /// </summary>
    public static BroadcastFixture GenerateCleanBreak(int seed)
    {
        var random = new Random(seed);

        var emissions = new List<DetectorEmission>
        {
            Emit(random, TimeSpan.Zero, BroadcastState.Program, DetectorOne, 0.94),
            Emit(random, TimeSpan.Zero, BroadcastState.Program, DetectorTwo, 0.92),
            Emit(random, TimeSpan.FromSeconds(30), BroadcastState.Commercial, DetectorOne, 0.96),
            Emit(random, TimeSpan.FromSeconds(30), BroadcastState.Commercial, DetectorTwo, 0.94),
            Emit(random, TimeSpan.FromSeconds(90), BroadcastState.Program, DetectorOne, 0.93),
            Emit(random, TimeSpan.FromSeconds(90), BroadcastState.Program, DetectorTwo, 0.91),
        };

        var expectedTimeline = new List<ExpectedSegment>
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(30), BroadcastState.Program),
            new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90), BroadcastState.Commercial),
            new(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(120), BroadcastState.Program),
        };

        return Build("Synthetic Clean Break", "synthetic-clean-break", emissions, expectedTimeline);
    }

    /// <summary>
    /// The same clean-break shape as <see cref="GenerateCleanBreak"/>, but with a much shorter
    /// commercial segment (10s), to exercise minimum-duration edge cases in whatever consumes
    /// these fixtures downstream (scoring, end-to-end relay tests).
    /// </summary>
    public static BroadcastFixture GenerateShortBreak(int seed)
    {
        var random = new Random(seed);

        var emissions = new List<DetectorEmission>
        {
            Emit(random, TimeSpan.Zero, BroadcastState.Program, DetectorOne, 0.94),
            Emit(random, TimeSpan.Zero, BroadcastState.Program, DetectorTwo, 0.92),
            Emit(random, TimeSpan.FromSeconds(20), BroadcastState.Commercial, DetectorOne, 0.96),
            Emit(random, TimeSpan.FromSeconds(20), BroadcastState.Commercial, DetectorTwo, 0.94),
            Emit(random, TimeSpan.FromSeconds(30), BroadcastState.Program, DetectorOne, 0.93),
            Emit(random, TimeSpan.FromSeconds(30), BroadcastState.Program, DetectorTwo, 0.91),
        };

        var expectedTimeline = new List<ExpectedSegment>
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(20), BroadcastState.Program),
            new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), BroadcastState.Commercial),
            new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), BroadcastState.Program),
        };

        return Build("Synthetic Short Break", "synthetic-short-break", emissions, expectedTimeline);
    }

    /// <summary>
    /// Two detectors agree strongly on Program, briefly disagree at low confidence right at the
    /// transition boundary, then agree strongly on Commercial -- mirroring
    /// fixtures/samples/ambiguous-transition.json. The expected timeline includes an explicit
    /// <see cref="BroadcastState.Transition"/> segment bridging the disagreement window, since
    /// neither state is confidently supported by both detectors during it.
    /// </summary>
    public static BroadcastFixture GenerateAmbiguousTransition(int seed)
    {
        var random = new Random(seed);

        var emissions = new List<DetectorEmission>
        {
            Emit(random, TimeSpan.Zero, BroadcastState.Program, DetectorOne, 0.94),
            Emit(random, TimeSpan.Zero, BroadcastState.Program, DetectorTwo, 0.93),
            Emit(random, TimeSpan.FromSeconds(10), BroadcastState.Program, DetectorOne, 0.62),
            Emit(random, TimeSpan.FromSeconds(10), BroadcastState.Commercial, DetectorTwo, 0.61),
            Emit(random, TimeSpan.FromSeconds(15), BroadcastState.Commercial, DetectorOne, 0.95),
            Emit(random, TimeSpan.FromSeconds(15), BroadcastState.Commercial, DetectorTwo, 0.92),
        };

        var expectedTimeline = new List<ExpectedSegment>
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(10), BroadcastState.Program),
            new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), BroadcastState.Transition),
            new(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), BroadcastState.Commercial),
        };

        return Build("Synthetic Ambiguous Transition", "synthetic-ambiguous-transition", emissions, expectedTimeline);
    }

    /// <summary>
    /// A false-positive trap: one detector spuriously reports a low-confidence Commercial-like
    /// blip in the middle of an otherwise-agreeing Program segment, while the other detector keeps
    /// confidently reporting Program throughout. The expected timeline stays Program from start to
    /// end -- no Commercial or Transition segment -- directly exercising AGENTS.md's design rule
    /// that false replacement of real programming is a more severe failure than showing some
    /// commercials: a fusion/scoring layer must not be fooled by one dissenting low-confidence
    /// signal into treating this as a genuine transition.
    /// </summary>
    public static BroadcastFixture GenerateFalsePositiveTrap(int seed)
    {
        var random = new Random(seed);

        var emissions = new List<DetectorEmission>
        {
            Emit(random, TimeSpan.Zero, BroadcastState.Program, DetectorOne, 0.95),
            Emit(random, TimeSpan.Zero, BroadcastState.Program, DetectorTwo, 0.93),
            Emit(random, TimeSpan.FromSeconds(30), BroadcastState.Program, DetectorOne, 0.94),
            Emit(random, TimeSpan.FromSeconds(30), BroadcastState.Commercial, DetectorTwo, 0.55),
            Emit(random, TimeSpan.FromSeconds(60), BroadcastState.Program, DetectorOne, 0.95),
            Emit(random, TimeSpan.FromSeconds(60), BroadcastState.Program, DetectorTwo, 0.92),
        };

        var expectedTimeline = new List<ExpectedSegment>
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(60), BroadcastState.Program),
        };

        return Build("Synthetic False-Positive Trap", "synthetic-false-positive-trap", emissions, expectedTimeline);
    }

    /// <summary>
    /// Generates all four required scenarios from a single input seed. Each scenario derives its
    /// own distinct seed (<paramref name="seed"/>, <paramref name="seed"/>+1, +2, +3) so the whole
    /// set is reproducible from one seed without the scenarios' internal randomness colliding.
    /// </summary>
    public static IReadOnlyList<BroadcastFixture> GenerateAll(int seed) =>
    [
        GenerateCleanBreak(seed),
        GenerateShortBreak(seed + 1),
        GenerateAmbiguousTransition(seed + 2),
        GenerateFalsePositiveTrap(seed + 3),
    ];

    private static DetectorEmission Emit(Random random, TimeSpan at, BroadcastState value, string sourceId, double baseConfidence)
    {
        // Small, bounded jitter (+/-0.02) around the base confidence, and a deterministic Guid
        // derived from the same seeded Random -- never Guid.NewGuid() -- so that constructing the
        // same scenario with the same seed twice produces byte-identical output, per #23's "same
        // seed produces identical fixture metadata" acceptance criterion.
        var jitter = (random.NextDouble() * 2 - 1) * 0.02;
        var confidence = Math.Clamp(baseConfidence + jitter, 0.0, 1.0);

        var guidBytes = new byte[16];
        random.NextBytes(guidBytes);
        var id = new Guid(guidBytes);

        return new DetectorEmission(id, at, value, sourceId, SourceKind, confidence);
    }

    private static BroadcastFixture Build(string name, string scope, IReadOnlyList<DetectorEmission> emissions, IReadOnlyList<ExpectedSegment> expectedTimeline)
    {
        var fixture = new BroadcastFixture(name, scope, emissions, expectedTimeline);

        // Validated before returning so "generated fixtures validate against the fixture schema"
        // is a hard guarantee of this generator, not just an expectation left to the caller.
        BroadcastFixtureValidator.ValidateOrThrow(fixture);

        return fixture;
    }
}
