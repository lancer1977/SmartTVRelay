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
    private int reconnectAttempts;

    private const int MaxReconnectAttempts = 3;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(50);

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
    /// attempt. <c>ReconnectAttempts</c> counts the bounded reconnects made after a stream open,
    /// read, or end-of-stream failure.
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
        ReconnectAttempts: reconnectAttempts);

    public async IAsyncEnumerable<MediaChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Fetch and parse lineup.json
        LineupChannel? selectedChannel;
        try
        {
            var lineupUrl = $"{baseUrl}/lineup.json";
            using var lineupResponse = await httpClient.GetAsync(lineupUrl, cancellationToken).ConfigureAwait(false);

            if (!lineupResponse.IsSuccessStatusCode)
            {
                var detail = $"[{SourceId}] Failed to reach HDHomeRun lineup at {DescribeEndpoint(lineupUrl)}: HTTP {(int)lineupResponse.StatusCode}";
                probeErrorCount++;
                Status = new SourceStatus(SourceHealth.Unavailable, detail);
                throw new InvalidOperationException(detail);
            }

            var lineupJson = await lineupResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var lineup = JsonSerializer.Deserialize<LineupChannel[]>(lineupJson) ?? Array.Empty<LineupChannel>();

            selectedChannel = lineup.FirstOrDefault(c => c.GuideNumber == channelGuideNumber);
            if (selectedChannel is null)
            {
                var detail = $"[{SourceId}] Channel '{channelGuideNumber}' not found in lineup at {DescribeEndpoint(baseUrl)}";
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
            // Remote exception text may contain URL paths, query tokens, or credentials in
            // forms that cannot be reliably recognized. Expose only the exception type.
            var detail = $"[{SourceId}] Failed to reach HDHomeRun lineup at {DescribeEndpoint(baseUrl)}: {ex.GetType().Name}";
            probeErrorCount++;
            Status = new SourceStatus(SourceHealth.Unavailable, detail);
            // The transport exception may contain the original URL and query string.
            throw new InvalidOperationException(detail);
        }

        // Reconnect a dropped tuner stream a bounded number of times. The lineup is resolved once;
        // reconnecting the selected stream does not require UDP discovery or a new channel lookup.
        var reconnectAttemptsThisRead = 0;
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? failure = null;
            var streamEnded = false;
            var reconnectOnEnd = true;
            Stream? stream = null;

            try
            {
                HttpResponseMessage? streamResponse = null;
                try
                {
                    streamResponse = await httpClient.GetAsync(
                        selectedChannel.URL,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    currentResponse = streamResponse;

                    if (!streamResponse.IsSuccessStatusCode)
                    {
                        probeErrorCount++;
                        failure = new InvalidOperationException(
                            $"[{SourceId}] Failed to open HDHomeRun stream at {DescribeEndpoint(selectedChannel.URL)}: HTTP {(int)streamResponse.StatusCode}");
                    }
                    else
                    {
                        reconnectOnEnd = !streamResponse.Content.Headers.ContentLength.HasValue;
                        stream = currentStream = await streamResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    probeErrorCount++;
                    failure = ex;
                }

                if (failure is null && stream is not null)
                {
                    Status = new SourceStatus(SourceHealth.Healthy);
                    const int ChunkSize = 65536;
                    var buffer = new byte[ChunkSize];

                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int bytesRead;
                        Exception? readFailure = null;
                        try
                        {
                            bytesRead = await stream.ReadAsync(buffer, 0, ChunkSize, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            bytesRead = 0;
                            readFailure = ex;
                        }

                        if (readFailure is not null)
                        {
                            decodeErrorCount++;
                            failure = readFailure;
                            break;
                        }

                        if (bytesRead == 0)
                        {
                            streamEnded = true;
                            break;
                        }

                        var chunk = new MediaChunk(buffer[..bytesRead], stopwatch.Elapsed);
                        bytesProcessed += bytesRead;
                        chunksProcessed++;
                        lastMediaTimestamp = chunk.SourceTime;
                        yield return chunk;
                    }
                }
            }
            finally
            {
                currentStream?.Dispose();
                currentResponse?.Dispose();
                currentStream = null;
                currentResponse = null;
            }

            if (failure is null && !streamEnded)
            {
                yield break;
            }

            if (streamEnded && !reconnectOnEnd)
            {
                yield break;
            }

            if (reconnectAttemptsThisRead >= MaxReconnectAttempts)
            {
                var reason = streamEnded ? "stream ended" : "stream failure";
                Status = new SourceStatus(
                    SourceHealth.Unavailable,
                    $"[{SourceId}] HDHomeRun {reason} after {reconnectAttemptsThisRead} reconnect attempts; terminal error {failure?.GetType().Name ?? "none"}");

                // Preserve the existing natural-completion behavior for an exhausted clean EOF;
                // transport/read failures remain observable to callers after retries are exhausted.
                if (streamEnded)
                {
                    yield break;
                }

                throw new InvalidOperationException(Status.Detail);
            }

            reconnectAttemptsThisRead++;
            reconnectAttempts++;
            await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            Status = new SourceStatus(
                SourceHealth.Unknown,
                $"[{SourceId}] Reconnecting HDHomeRun stream (attempt {reconnectAttemptsThisRead}/{MaxReconnectAttempts})");
        }
    }

    private static string DescribeEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return "[redacted endpoint]";
        }

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{port}";
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
