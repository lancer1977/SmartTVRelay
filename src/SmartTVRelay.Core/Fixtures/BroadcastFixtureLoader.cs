namespace SmartTVRelay.Core.Fixtures;

using System.Xml;
using System.Text.Json;
using System.Text.Json.Serialization;

public static class BroadcastFixtureLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(),
            new TimeSpanConverter(),
        },
        WriteIndented = true,
    };

    public static BroadcastFixture Load(string path)
    {
        var json = File.ReadAllText(path);
        var fixture = JsonSerializer.Deserialize<BroadcastFixture>(json, Options)
            ?? throw new InvalidDataException($"Fixture at '{path}' deserialized to null.");

        BroadcastFixtureValidator.ValidateOrThrow(fixture);
        return fixture;
    }

    private sealed class TimeSpanConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString();
            if (text == null)
            {
                throw new JsonException("TimeSpan value cannot be null");
            }

            // ISO-8601 duration format (e.g. "PT30S", "PT1H30M") -- System.Text.Json's
            // built-in TimeSpan support uses the "c" format ("00:00:30") instead, which
            // is less readable in hand-authored fixture files, so this reads/writes
            // ISO-8601 explicitly via XmlConvert.
            try
            {
                return XmlConvert.ToTimeSpan(text);
            }
            catch (Exception ex)
            {
                throw new JsonException($"Invalid TimeSpan value: '{text}'", ex);
            }
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(XmlConvert.ToString(value));
        }
    }
}
