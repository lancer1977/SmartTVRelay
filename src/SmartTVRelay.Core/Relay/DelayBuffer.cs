using SmartTVRelay.Core.Ingest;

namespace SmartTVRelay.Core.Relay;

/// <summary>Bounded circular buffer that holds ingested <see cref="MediaChunk"/>s for delayed
/// playback (#44). Buffering only -- no switching/replacement policy, per the issue's agent
/// boundary. Readiness is derived entirely from <see cref="MediaChunk.SourceTime"/>, never wall-clock,
/// so recorded-source tests are fully deterministic.</summary>
public sealed class DelayBuffer : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly TimeSpan _maxBufferedDuration;
    private readonly long _maxBufferedBytes;
    private readonly Queue<MediaChunk> _chunks = new();
    private readonly object _gate = new();

    private long _bufferedBytes;
    private TimeSpan? _lastEnqueuedSourceTime;
    private bool _disposed;

    public DelayBuffer(TimeSpan delay, TimeSpan maxBufferedDuration, long maxBufferedBytes)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must not be negative.");
        }

        if (maxBufferedDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBufferedDuration), maxBufferedDuration, "Max buffered duration must be positive.");
        }

        if (maxBufferedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBufferedBytes), maxBufferedBytes, "Max buffered bytes must be positive.");
        }

        _delay = delay;
        _maxBufferedDuration = maxBufferedDuration;
        _maxBufferedBytes = maxBufferedBytes;
    }

    /// <summary>Number of chunks currently buffered (dequeued and evicted chunks excluded).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _chunks.Count;
            }
        }
    }

    /// <summary>Total bytes across all currently buffered chunks.</summary>
    public long BufferedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bufferedBytes;
            }
        }
    }

    /// <summary>Buffers one chunk. Source timestamps must be monotonically non-decreasing across calls
    /// -- a source that emits an out-of-order timestamp indicates a bug upstream, not a condition this
    /// buffer can recover from. Evicts the oldest buffered chunks while size/duration bounds are
    /// exceeded, but always keeps at least one chunk so a single oversized chunk is never silently
    /// dropped.</summary>
    public void Enqueue(MediaChunk chunk)
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            if (_lastEnqueuedSourceTime is { } last && chunk.SourceTime < last)
            {
                throw new ArgumentException(
                    $"MediaChunk.SourceTime must be monotonically non-decreasing; received {chunk.SourceTime} after {last}.",
                    nameof(chunk));
            }

            _chunks.Enqueue(chunk);
            _bufferedBytes += chunk.Data.Length;
            _lastEnqueuedSourceTime = chunk.SourceTime;

            EvictOverrunLocked();
        }
    }

    /// <summary>Dequeues the oldest buffered chunk once it has aged at least <c>delay</c> relative to
    /// the most recently enqueued chunk's <see cref="MediaChunk.SourceTime"/>. Returns
    /// <see langword="false"/> without blocking when nothing is buffered yet or the oldest chunk has
    /// not aged enough (underrun) -- callers poll rather than await readiness.</summary>
    public bool TryDequeue(out MediaChunk chunk)
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            if (_lastEnqueuedSourceTime is not { } last || _chunks.Count == 0)
            {
                chunk = default!;
                return false;
            }

            var oldest = _chunks.Peek();
            if (last - oldest.SourceTime < _delay)
            {
                chunk = default!;
                return false;
            }

            chunk = _chunks.Dequeue();
            _bufferedBytes -= chunk.Data.Length;
            return true;
        }
    }

    /// <summary>Convenience helper that enqueues every chunk read from <paramref name="source"/> until
    /// the sequence completes or <paramref name="cancellationToken"/> is triggered.</summary>
    public async Task FillFromAsync(IAsyncEnumerable<MediaChunk> source, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await foreach (var chunk in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            Enqueue(chunk);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _chunks.Clear();
            _bufferedBytes = 0;
        }
    }

    private void EvictOverrunLocked()
    {
        while (_chunks.Count > 1 && IsOverrunLocked())
        {
            var evicted = _chunks.Dequeue();
            _bufferedBytes -= evicted.Data.Length;
        }
    }

    private bool IsOverrunLocked()
    {
        if (_bufferedBytes > _maxBufferedBytes)
        {
            return true;
        }

        var oldest = _chunks.Peek();
        var bufferedDuration = _lastEnqueuedSourceTime!.Value - oldest.SourceTime;
        return bufferedDuration > _maxBufferedDuration;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DelayBuffer));
        }
    }
}
