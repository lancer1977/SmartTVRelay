namespace SmartTVRelay.Evaluation;

using System.Globalization;

/// <summary>Parsed command-line configuration for the CI evaluation gate (#27).</summary>
public sealed record EvaluationCliOptions(
    int Seed = 42,
    string OutputDirectory = "artifacts/evaluation",
    double? MinCommercialPrecision = 0.9,
    double? MinCommercialRecall = 0.9,
    int MaxFalseReplacementCount = 0,
    string Mode = "evaluation",
    string? Host = null,
    string? Channel = null,
    int? Seconds = null)
{
    /// <summary>
    /// Parses `--flag value` pairs. Unrecognized flags are rejected (rather than silently ignored)
    /// so a CI workflow typo surfaces immediately instead of quietly running with defaults.
    /// `--min-commercial-precision none` / `--min-commercial-recall none` disable that threshold.
    /// </summary>
    public static EvaluationCliOptions Parse(IReadOnlyList<string> args)
    {
        var options = new EvaluationCliOptions();

        if (args.Count > 0 && args[0] == "live-smoke")
        {
            options = options with { Mode = "live-smoke" };
            args = args.Skip(1).ToArray();
        }

        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i];
            string Value()
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException($"Flag '{flag}' requires a value.");
                }

                return args[++i];
            }

            options = flag switch
            {
                "--seed" => options with { Seed = int.Parse(Value(), CultureInfo.InvariantCulture) },
                "--output" => options with { OutputDirectory = Value() },
                "--min-commercial-precision" => options with { MinCommercialPrecision = ParseNullableDouble(Value()) },
                "--min-commercial-recall" => options with { MinCommercialRecall = ParseNullableDouble(Value()) },
                "--max-false-replacement-count" => options with { MaxFalseReplacementCount = int.Parse(Value(), CultureInfo.InvariantCulture) },
                "--host" => options with { Host = Value() },
                "--channel" => options with { Channel = Value() },
                "--seconds" => options with { Seconds = int.Parse(Value(), CultureInfo.InvariantCulture) },
                _ => throw new ArgumentException($"Unrecognized flag '{flag}'."),
            };
        }

        if (options.Mode == "live-smoke" && (string.IsNullOrWhiteSpace(options.Host) || string.IsNullOrWhiteSpace(options.Channel) || options.Seconds is null or <= 0))
        {
            throw new ArgumentException("live-smoke requires --host, --channel, and positive --seconds.");
        }

        return options;
    }

    private static double? ParseNullableDouble(string value) =>
        string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ? null : double.Parse(value, CultureInfo.InvariantCulture);
}
