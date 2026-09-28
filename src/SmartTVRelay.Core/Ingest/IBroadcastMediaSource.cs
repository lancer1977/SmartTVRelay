namespace SmartTVRelay.Core.Ingest;

/// <summary>Stable boundary between live and recorded broadcast media (#28). Implementations include a
/// live HDHomeRun tuner adapter, a recorded MPEG-TS file adapter, and (for tests) <see cref="FakeBroadcastMediaSource"/>.
/// No HDHomeRun-specific or file-specific concept may appear on this interface -- concrete adapters own
/// their own connection details; this contract only exposes what downstream ingest/detector code needs.</summary>
public interface IBroadcastMediaSource : IAsyncDisposable
{
    /// <summary>Stable identifier for this source, matching the SourceId detectors attach to their
    /// <c>Observation&lt;BroadcastState&gt;</c> emissions.</summary>
    string SourceId { get; }

    /// <summary>Current health/status of the source, independent of whether a read is in progress.</summary>
    SourceStatus Status { get; }

    /// <summary>Streams chunks with source-relative timestamps starting at <see cref="TimeSpan.Zero"/>.
    /// The enumerable completes normally when the source reaches a natural end (a recorded file is
    /// exhausted); it does not throw to signal end-of-stream. A live source instead keeps yielding until
    /// <paramref name="cancellationToken"/> is triggered, at which point reading stops via cancellation,
    /// not a natural completion.</summary>
    IAsyncEnumerable<MediaChunk> ReadAsync(CancellationToken cancellationToken);
}
