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
    private long bytesProcessed;
    private long chunksProcessed;
    private TimeSpan? lastMediaTimestamp;
    private int probeErrorCount;
    private int decodeErrorCount;

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

    /// <summary>
    /// Structured ingest diagnostics (#35), reflecting the current/most recent <see cref="ReadAsync"/>
    /// attempt. <c>ReconnectAttempts</c> is always 0: this class does not retry on failure today (a
    /// connectivity or read failure surfaces once via <c>ProbeErrorCount</c>/<c>DecodeErrorCount</c>
    /// and the read ends) -- see <see cref="IngestDiagnostics.ReconnectAttempts"/> for why the field
    /// still exists.
    /// </summary>
    public IngestDiagnostics Diagnostics => new(
        SourceId,
        Status.Health,
        Status.Detail,
        bytesProcessed,
        chunksProcessed,
        lastMediaTimestamp,
        probeErrorCount,
        decodeErrorCount,
        ReconnectAttempts: 0);

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
                var detail = $"[{SourceId}] Failed to reach HDHomeRun lineup at {lineupUrl}: HTTP {(int)lineupResponse.StatusCode} {lineupResponse.ReasonPhrase}";
                probeErrorCount++;
                Status = new SourceStatus(SourceHealth.Unavailable, detail);
                throw new InvalidOperationException(detail);
            }

            var lineupJson = await lineupResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var lineup = JsonSerializer.Deserialize<LineupChannel[]>(lineupJson) ?? Array.Empty<LineupChannel>();

            selectedChannel = lineup.FirstOrDefault(c => c.GuideNumber == channelGuideNumber);
            if (selectedChannel is null)
            {
                var detail = $"[{SourceId}] Channel '{channelGuideNumber}' not found in lineup at {baseUrl}";
                probeErrorCount++;
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
            var detail = $"[{SourceId}] Failed to reach HDHomeRun lineup at {baseUrl}/lineup.json: {ex.Message}";
            probeErrorCount++;
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
                var detail = $"[{SourceId}] Failed to open stream at {selectedChannel.URL}: HTTP {(int)streamResponse.StatusCode}";
                probeErrorCount++;
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
                    var detail = $"[{SourceId}] Stream read failed from {selectedChannel.URL}: {ex.Message}";
                    decodeErrorCount++;
                    Status = new SourceStatus(SourceHealth.Unavailable, detail);
                    throw;
                }

                if (bytesRead == 0)
                {
                    // End of stream
                    break;
                }

                // Copy out of the shared read buffer -- buffer is reused on the next iteration,
                // so a MediaChunk wrapping it directly (instead of copying) would silently alias
                // whatever the next read overwrites it with, corrupting every previously-yielded
                // chunk's Data as soon as the consumer stops holding the enumerator at that item.
                var chunk = new MediaChunk(buffer[..bytesRead], stopwatch.Elapsed);

                // Updated before yielding, matching RecordedFileMediaSource's convention, so a
                // caller inspecting Diagnostics while consuming this chunk sees it already counted.
                bytesProcessed += bytesRead;
                chunksProcessed++;
                lastMediaTimestamp = chunk.SourceTime;

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
