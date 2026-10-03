namespace SmartTVRelay.Core.Ingest;

using System.Runtime.CompilerServices;

/// <summary>Reads a local MPEG-TS file as an <see cref="IBroadcastMediaSource"/> for offline development
/// and tests. Chunks raw bytes with deterministic, evenly-spaced timestamps; does not interpret
/// TS packet semantics (PIDs, PSI, PES, etc.) -- that is a detector's job, not the ingest layer's.
/// Handles EOF and cancellation cleanly per #29.</summary>
public sealed class RecordedFileMediaSource : IBroadcastMediaSource
{
    private readonly string filePath;
    private readonly int chunkSizeBytes;
    private readonly TimeSpan chunkInterval;
    private long bytesProcessed;
    private long chunksProcessed;
    private TimeSpan? lastMediaTimestamp;
    private bool? captionsAvailable;
    private bool? markersAvailable;

    /// <summary>
    /// Creates a <see cref="RecordedFileMediaSource"/> for a local MPEG-TS file.
    /// </summary>
    /// <param name="sourceId">Unique identifier for this source; passed to detectors as the SourceId on
    /// their Observation Core emissions. Must not be null, empty, or whitespace.</param>
    /// <param name="filePath">Absolute or relative path to an existing MPEG-TS file. Must exist on disk at construction time.</param>
    /// <param name="chunkSizeBytes">Number of bytes to read per chunk. Defaults to 65536 (64 KiB), a compromise between granularity and I/O overhead. The last chunk may be smaller.</param>
    /// <param name="chunkInterval">Artificial, deterministic interval between chunk timestamps. Defaults to 100 ms. Together with a zero-indexed chunk count N, yields <c>SourceTime = N * chunkInterval</c>, giving replay predictability independent of actual file-read timing.</param>
    /// <exception cref="ArgumentException">Thrown if <paramref name="sourceId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="FileNotFoundException">Thrown if the file at <paramref name="filePath"/> does not exist.</exception>
    public RecordedFileMediaSource(
        string sourceId,
        string filePath,
        int chunkSizeBytes = 65536,
        TimeSpan? chunkInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}", filePath);
        }

        SourceId = sourceId;
        this.filePath = filePath;
        this.chunkSizeBytes = chunkSizeBytes;
        this.chunkInterval = chunkInterval ?? TimeSpan.FromMilliseconds(100);
    }

    public string SourceId { get; }

    /// <summary>
    /// Returns the current health status of the source.
    /// For recorded files, this is always <see cref="SourceHealth.Healthy"/> -- they don't
    /// degrade mid-read like a live tuner might, and reaching EOF is a normal completion,
    /// not a failure condition.
    /// </summary>
    public SourceStatus Status => new(SourceHealth.Healthy);

    /// <summary>
    /// Structured ingest diagnostics (#35) for the most recent (or in-progress) <see cref="ReadAsync"/>
    /// call. Counters reset at the start of each new read, so a repeated read of the same file (this
    /// class explicitly supports being read multiple times) reports that read's own progress rather
    /// than an accumulation across reads. Recorded files never probe/decode-fail or reconnect --
    /// reading raw bytes from an already-open local file has no such failure modes -- so those
    /// counters are always 0.
    /// </summary>
    public IngestDiagnostics Diagnostics => new(
        SourceId,
        SourceHealth.Healthy,
        Detail: null,
        bytesProcessed,
        chunksProcessed,
        lastMediaTimestamp,
        ProbeErrorCount: 0,
        DecodeErrorCount: 0,
        ReconnectAttempts: 0,
        CaptionsAvailable: captionsAvailable,
        MarkersAvailable: markersAvailable);

    /// <summary>
    /// Streams chunks of raw bytes from the file, with deterministic timestamps.
    /// Completes normally (no exception) when EOF is reached; honors <paramref name="cancellationToken"/>
    /// by throwing <see cref="OperationCanceledException"/> if cancellation is requested between chunks.
    /// </summary>
    public async IAsyncEnumerable<MediaChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        bytesProcessed = 0;
        chunksProcessed = 0;
        lastMediaTimestamp = null;
        captionsAvailable = null;
        markersAvailable = null;

        // Open the file fresh on each read, in case the same source is read multiple times.
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: chunkSizeBytes, useAsync: true);

        var buffer = new byte[chunkSizeBytes];
        int chunkIndex = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int bytesRead = await fs.ReadAsync(buffer, 0, chunkSizeBytes, cancellationToken);
            if (bytesRead == 0)
            {
                // EOF reached; end the enumerable normally (no exception).
                break;
            }

            var sourceTime = TimeSpan.FromTicks(chunkIndex * chunkInterval.Ticks);

            // Updated before yielding, not after, so Diagnostics already reflects this chunk by
            // the time the caller observes it (a caller inspecting Diagnostics from inside its own
            // consumption of the yielded chunk should see it counted, not lag one chunk behind).
            bytesProcessed += bytesRead;
            chunksProcessed++;
            lastMediaTimestamp = sourceTime;

            yield return new MediaChunk(buffer[..bytesRead], sourceTime);

            chunkIndex++;
        }

        var availability = await RecordedAvailabilityInspector
            .InspectAsync(filePath, cancellationToken)
            .ConfigureAwait(false);
        captionsAvailable = availability.CaptionsAvailable;
        markersAvailable = availability.MarkersAvailable;
    }

    public ValueTask DisposeAsync()
    {
        // No persistent resources to clean up; each ReadAsync opens and closes its own stream.
        return ValueTask.CompletedTask;
    }
}
