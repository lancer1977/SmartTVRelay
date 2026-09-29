using System.Runtime.CompilerServices;
using SmartTVRelay.Core.Ingest;

namespace SmartTVRelay.Core.Relay;

/// <summary>Lifecycle state of a <see cref="ContinuousRelayOutput"/>'s background pump.</summary>
public enum RelayOutputStatus
{
    /// <summary>Created but <see cref="ContinuousRelayOutput.Start"/> has not been called yet.</summary>
    Idle,

    /// <summary>Actively reading from the source into the delay buffer.</summary>
    Running,

    /// <summary>The source's <see cref="IBroadcastMediaSource.ReadAsync"/> completed naturally
    /// (e.g. a recorded file reached EOF). Not an error.</summary>
    Completed,

    /// <summary>The pump stopped because reading from the source threw. See <see cref="ContinuousRelayOutput.Fault"/>.</summary>
    Faulted,
}

/// <summary>
/// Exposes delayed original broadcast programming as a continuous, client-consumable stream.
/// Wraps a <see cref="DelayBuffer"/> fed by a background pump reading from an
/// <see cref="IBroadcastMediaSource"/> -- the output abstraction callers subscribe to is entirely
/// separate from the source that feeds it. Relays original content only: this class makes no
/// replacement/switching decisions (that is <c>BroadcastReplacementPolicy</c> and the future relay
/// switching state machine's job), per the issue's agent boundary.
/// </summary>
/// <remarks>
/// <b>Continuity/timestamp behavior:</b> chunks are yielded in the same order and with the same
/// <see cref="MediaChunk.SourceTime"/> values the source produced -- this class does not
/// renumber, interpolate, or otherwise alter timestamps. Because the underlying
/// <see cref="DelayBuffer"/> evicts its oldest chunks once its own size/duration bounds are
/// exceeded, a subscriber that disconnects for longer than the buffer's configured window will,
/// on reconnecting, resume from whatever the buffer currently holds -- any chunks evicted while
/// unconsumed are gone. This mirrors live-broadcast semantics (there is no rewind/replay
/// capability) rather than guaranteeing delivery of every chunk to every subscriber.
/// <para>
/// While the source is still <see cref="RelayOutputStatus.Running"/>, a chunk is withheld until it
/// has aged past the configured delay relative to the most recently ingested chunk. Once the
/// source reaches <see cref="RelayOutputStatus.Completed"/> or <see cref="RelayOutputStatus.Faulted"/>,
/// no further chunks will ever arrive to make the remainder "age" further, so any chunks still
/// sitting in the buffer are drained and delivered immediately rather than being stranded --
/// e.g. a recorded-file source's final few chunks, ingested less than one delay interval before
/// EOF, are still fully delivered once the file ends, not silently dropped.
/// </para>
/// </remarks>
public sealed class ContinuousRelayOutput : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly IBroadcastMediaSource _source;
    private readonly DelayBuffer _buffer;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly object _stateGate = new();

    private Task? _pumpTask;
    private volatile RelayOutputStatus _status = RelayOutputStatus.Idle;
    private Exception? _fault;
    private bool _disposed;

    public ContinuousRelayOutput(
        IBroadcastMediaSource source,
        TimeSpan delay,
        TimeSpan maxBufferedDuration,
        long maxBufferedBytes,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _buffer = new DelayBuffer(delay, maxBufferedDuration, maxBufferedBytes);
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>Current pump lifecycle state.</summary>
    public RelayOutputStatus Status => _status;

    /// <summary>The exception that stopped the pump, when <see cref="Status"/> is <see cref="RelayOutputStatus.Faulted"/>; otherwise null.</summary>
    public Exception? Fault => _fault;

    /// <summary>
    /// Starts the background pump reading from the source into the delay buffer. Must be called at
    /// most once. Subscribers may call <see cref="Subscribe"/> before or after <see cref="Start"/>.
    /// </summary>
    public void Start()
    {
        lock (_stateGate)
        {
            ThrowIfDisposed();
            if (_pumpTask is not null)
            {
                throw new InvalidOperationException("ContinuousRelayOutput has already been started.");
            }

            _status = RelayOutputStatus.Running;
            _pumpTask = Task.Run(PumpAsync);
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await _buffer.FillFromAsync(_source.ReadAsync(_pumpCts.Token), _pumpCts.Token).ConfigureAwait(false);
            _status = RelayOutputStatus.Completed;
        }
        catch (OperationCanceledException) when (_pumpCts.IsCancellationRequested)
        {
            // Disposal-driven shutdown -- not a fault, no status change needed beyond what
            // DisposeAsync already reflects.
        }
        catch (Exception ex)
        {
            _fault = ex;
            _status = RelayOutputStatus.Faulted;
        }
    }

    /// <summary>
    /// Subscribes to the continuous output stream, yielding chunks as they age past the configured
    /// delay. Safe to call again after a previous subscription's enumeration ends -- client
    /// disconnect/reconnect is just stopping and starting a new call to this method; the background
    /// pump keeps running independently of whether anything is currently subscribed. Completes
    /// normally once the source has completed (or faulted) and the buffer has been fully drained;
    /// otherwise polls at the configured interval while waiting for more data.
    /// </summary>
    public async IAsyncEnumerable<MediaChunk> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_buffer.TryDequeue(out var chunk))
            {
                yield return chunk;
                continue;
            }

            if (_status is RelayOutputStatus.Completed or RelayOutputStatus.Faulted)
            {
                // No further chunks will ever be enqueued, so waiting for the remainder to "age"
                // relative to a source time that will never advance again serves no purpose --
                // drain whatever is left immediately instead of stranding it in the buffer.
                if (_buffer.TryDrainAny(out var remaining))
                {
                    yield return remaining;
                    continue;
                }

                yield break;
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await _pumpCts.CancelAsync().ConfigureAwait(false);

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch
            {
                // Already captured via Fault; DisposeAsync itself must not throw for a pump failure.
            }
        }

        _pumpCts.Dispose();
        _buffer.Dispose();
        await _source.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ContinuousRelayOutput));
        }
    }
}
