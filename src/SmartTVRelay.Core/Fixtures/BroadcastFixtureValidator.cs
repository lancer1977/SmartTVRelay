namespace SmartTVRelay.Core.Fixtures;

/// <summary>Validates a <see cref="BroadcastFixture"/> against the #22 schema rules before it's used for
/// evaluation. Rejects malformed fixtures early rather than letting the harness silently misbehave on
/// bad input.</summary>
public static class BroadcastFixtureValidator
{
    /// <summary>Validates <paramref name="fixture"/>, returning the (possibly empty) list of problems
    /// found. An empty result means the fixture is valid.</summary>
    public static IReadOnlyList<string> Validate(BroadcastFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var errors = new List<string>();

        if (fixture.SchemaVersion != BroadcastFixtureSchema.CurrentVersion)
        {
            errors.Add($"Unsupported schema version {fixture.SchemaVersion}; expected {BroadcastFixtureSchema.CurrentVersion}.");
        }

        if (string.IsNullOrWhiteSpace(fixture.Name))
        {
            errors.Add("Name must not be null or whitespace.");
        }

        if (string.IsNullOrWhiteSpace(fixture.Scope))
        {
            errors.Add("Scope must not be null or whitespace.");
        }

        var isRecorded = !string.IsNullOrWhiteSpace(fixture.MediaPath);
        if (!isRecorded && fixture.Emissions.Count == 0)
        {
            errors.Add("A synthetic fixture (no MediaPath) must supply at least one emission.");
        }

        ValidateTimeline(fixture.ExpectedTimeline, errors);

        return errors;
    }

    /// <summary>Throws <see cref="InvalidDataException"/> if <paramref name="fixture"/> fails validation.</summary>
    public static void ValidateOrThrow(BroadcastFixture fixture)
    {
        var errors = Validate(fixture);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                $"Fixture '{fixture.Name}' failed validation: {string.Join("; ", errors)}");
        }
    }

    private static void ValidateTimeline(IReadOnlyList<ExpectedSegment> timeline, List<string> errors)
    {
        if (timeline.Count == 0)
        {
            errors.Add("ExpectedTimeline must not be empty.");
            return;
        }

        ExpectedSegment? previous = null;
        foreach (var segment in timeline)
        {
            if (segment.End <= segment.Start)
            {
                errors.Add($"Segment [{segment.Start}, {segment.End}) has End <= Start.");
            }

            if (segment.Start < TimeSpan.Zero)
            {
                errors.Add($"Segment start {segment.Start} is negative.");
            }

            if (previous is not null && segment.Start < previous.End)
            {
                errors.Add(
                    $"Segment [{segment.Start}, {segment.End}) overlaps the preceding segment " +
                    $"[{previous.Start}, {previous.End}).");
            }

            previous = segment;
        }
    }
}
