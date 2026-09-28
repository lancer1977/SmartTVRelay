namespace SmartTVRelay.Core.Ingest;

using System.Runtime.CompilerServices;

/// <summary>In-memory <see cref="IBroadcastMediaSource"/> for tests -- replays a fixed sequence of chunks
/// with no real I/O, honors cancellation, and reports a configurable status. Required by #28's "fake
/// source exists for tests" acceptance criterion.</summary>
public sealed class FakeBroadcastMediaSource : IBroadcastMediaSource
{
    private readonly IReadOnlyList<MediaChunk> chunks;
    private bool disposed;

    public FakeBroadcastMediaSource(string sourceId, IReadOnlyList<MediaChunk> chunks, SourceStatus? status = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(chunks);

        SourceId = sourceId;
        this.chunks = chunks;
        Status = status ?? new SourceStatus(SourceHealth.Healthy);
    }

    public string SourceId { get; }

    public SourceStatus Status { get; }

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
