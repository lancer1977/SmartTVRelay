namespace SmartTVRelay.Core.Ingest;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Live stream adapter for HDHomeRun devices. Discovers a selected channel by GuideNumber
/// from the device's lineup, opens the stream URL, and yields raw MPEG-TS chunks with
/// source-relative timestamps. Requires explicit device base URL; UDP discovery is out of scope.
/// </summary>
public sealed class HdHomeRunMediaSource : IBroadcastMediaSource
{
    private readonly HttpClient httpClient;
    private readonly string baseUrl;
    private readonly string channelGuideNumber;
    private Stream? currentStream;
    private HttpResponseMessage? currentResponse;

    public HdHomeRunMediaSource(HttpClient httpClient, string sourceId, string baseUrl, string channelGuideNumber)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelGuideNumber);

        this.httpClient = httpClient;
        SourceId = sourceId;
        this.baseUrl = baseUrl.TrimEnd('/');
        this.channelGuideNumber = channelGuideNumber;
        Status = new SourceStatus(SourceHealth.Unknown);
    }

    public string SourceId { get; }

    public SourceStatus Status { get; private set; }

    public async IAsyncEnumerable<MediaChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Fetch and parse lineup.json
        LineupChannel? selectedChannel;
        try
        {
            var lineupUrl = $"{baseUrl}/lineup.json";
            var lineupResponse = await httpClient.GetAsync(lineupUrl, cancellationToken).ConfigureAwait(false);

            if (!lineupResponse.IsSuccessStatusCode)
            {
                var detail = $"Failed to reach HDHomeRun lineup at {lineupUrl}: HTTP {(int)lineupResponse.StatusCode} {lineupResponse.ReasonPhrase}";
                Status = new SourceStatus(SourceHealth.Unavailable, detail);
                throw new InvalidOperationException(detail);
            }

            var lineupJson = await lineupResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var lineup = JsonSerializer.Deserialize<LineupChannel[]>(lineupJson) ?? Array.Empty<LineupChannel>();

            selectedChannel = lineup.FirstOrDefault(c => c.GuideNumber == channelGuideNumber);
            if (selectedChannel is null)
            {
                var detail = $"Channel '{channelGuideNumber}' not found in lineup at {baseUrl}";
                Status = new SourceStatus(SourceHealth.Unavailable, detail);
                throw new InvalidOperationException(detail);
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = $"Failed to reach HDHomeRun lineup at {baseUrl}/lineup.json: {ex.Message}";
            Status = new SourceStatus(SourceHealth.Unavailable, detail);
            throw new InvalidOperationException(detail, ex);
        }

        // Open the stream
        Status = new SourceStatus(SourceHealth.Healthy);

        try
        {
            var streamResponse = await httpClient.GetAsync(
                selectedChannel.URL,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!streamResponse.IsSuccessStatusCode)
            {
                var detail = $"Failed to open stream at {selectedChannel.URL}: HTTP {(int)streamResponse.StatusCode}";
                Status = new SourceStatus(SourceHealth.Unavailable, detail);
                throw new InvalidOperationException(detail);
            }

            currentResponse = streamResponse;
            var stream = await streamResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            currentStream = stream;

            var stopwatch = Stopwatch.StartNew();
            const int ChunkSize = 65536;
            var buffer = new byte[ChunkSize];

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesRead;
                try
                {
                    bytesRead = await stream.ReadAsync(buffer, 0, ChunkSize, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    var detail = $"Stream read failed from {selectedChannel.URL}: {ex.Message}";
                    Status = new SourceStatus(SourceHealth.Unavailable, detail);
                    throw;
                }

                if (bytesRead == 0)
                {
                    // End of stream
                    break;
                }

                var chunk = new MediaChunk(new ReadOnlyMemory<byte>(buffer, 0, bytesRead), stopwatch.Elapsed);
                yield return chunk;
            }
        }
        finally
        {
            currentStream?.Dispose();
            currentResponse?.Dispose();
            currentStream = null;
            currentResponse = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        currentStream?.Dispose();
        currentResponse?.Dispose();
        currentStream = null;
        currentResponse = null;
    }

    /// <summary>Minimal JSON shape for deserializing HDHomeRun lineup.json responses.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1812: Avoid uninstantiated internal classes", Justification = "Used by JsonSerializer")]
    private sealed class LineupChannel
    {
        [JsonPropertyName("GuideNumber")]
        public required string GuideNumber { get; set; }

        [JsonPropertyName("URL")]
        public required string URL { get; set; }
    }
}
