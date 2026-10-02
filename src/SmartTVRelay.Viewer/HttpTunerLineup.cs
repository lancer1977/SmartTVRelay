using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SmartTVRelay.Viewer;

public sealed class HttpTunerLineup(HttpClient http, IOptions<TunerOptions> options) : ITunerLineup
{
    private sealed record Entry(
        [property: JsonPropertyName("GuideNumber")] string? GuideNumber,
        [property: JsonPropertyName("GuideName")] string? GuideName,
        [property: JsonPropertyName("VideoCodec")] string? VideoCodec);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var url = new Uri(new Uri(options.Value.BaseUrl.TrimEnd('/') + "/"), "lineup.json");
        var entries = await http.GetFromJsonAsync<List<Entry>>(url, Json, cancellationToken) ?? [];
        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.GuideNumber))
            .Select(e => new ChannelInfo(e.GuideNumber!, e.GuideName ?? e.GuideNumber!, e.VideoCodec ?? ""))
            .ToList();
    }
}
