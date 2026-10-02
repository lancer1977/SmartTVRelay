namespace SmartTVRelay.Core.Ingest;

using System.Runtime.CompilerServices;

/// <summary>In-memory <see cref="IBroadcastMediaSource"/> for tests -- replays a fixed sequence of chunks
/// with no real I/O, honors cancellation, and reports a configurable status. Required by #28's "fake
/// source exists for tests" acceptance criterion.</summary>
public sealed class FakeBroadcastMediaSource : IBroadcastMediaSource
{
    private readonly IReadOnlyList<MediaChunk> chunks;
    private bool disposed;

    public FakeBroadcastMediaSource(string sourceId, IReadOnlyList<MediaChunk> chunks, SourceStatus? status = null, IngestDiagnostics? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(chunks);

        SourceId = sourceId;
        this.chunks = chunks;
        Status = status ?? new SourceStatus(SourceHealth.Healthy);
        Diagnostics = diagnostics ?? new IngestDiagnostics(
            sourceId,
            Status.Health,
            Status.Detail,
            BytesProcessed: 0,
            ChunksProcessed: 0,
            LastMediaTimestamp: null,
            ProbeErrorCount: 0,
            DecodeErrorCount: 0,
            ReconnectAttempts: 0);
    }

    public string SourceId { get; }

    public SourceStatus Status { get; }

    /// <summary>Fixed diagnostics snapshot for this fake source -- either the value passed to the
    /// constructor, or a zeroed default derived from <see cref="Status"/>. Unlike the real sources,
    /// this does not update as <see cref="ReadAsync"/> is consumed; a test that needs to assert on
    /// progressing counters should inject the expected end-state value directly.</summary>
    public IngestDiagnostics Diagnostics { get; }

    public bool DisposeCalled => disposed;

    public async IAsyncEnumerable<MediaChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ValueTask.CompletedTask;
    }
}
