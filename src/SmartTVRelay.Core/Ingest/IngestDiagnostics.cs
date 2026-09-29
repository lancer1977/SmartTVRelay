namespace SmartTVRelay.Core.Ingest;

/// <summary>
/// Structured, testable diagnostics for a broadcast media source (#35) -- enough to debug ingest
/// behavior without a debugger, for both recorded and live adapters. Deliberately excludes
/// anything sensitive (auth tokens, credentials): only counts, timestamps, and health/detail text
/// that a source already surfaces via <see cref="SourceStatus"/>.
/// </summary>
/// <param name="SourceId">The source's own stable identifier, included here (not just on the
/// owning <see cref="IBroadcastMediaSource"/>) so a diagnostics snapshot is self-describing when
/// logged or serialized on its own -- the acceptance criteria's "correlation/source IDs on
/// errors" requirement.</param>
/// <param name="Health">Current coarse health, mirroring <see cref="SourceStatus.Health"/>.</param>
/// <param name="Detail">Current human-readable detail, mirroring <see cref="SourceStatus.Detail"/>.</param>
/// <param name="BytesProcessed">Total bytes yielded by <see cref="IBroadcastMediaSource.ReadAsync"/>
/// so far, for throughput/progress visibility.</param>
/// <param name="ChunksProcessed">Total chunks yielded so far.</param>
/// <param name="LastMediaTimestamp">The source-relative <see cref="MediaChunk.SourceTime"/> of the
/// most recently yielded chunk, or <see langword="null"/> if none has been yielded yet.</param>
/// <param name="ProbeErrorCount">Count of errors encountered while probing/connecting to the
/// source (e.g. lineup lookup, channel resolution, stream-open failures for a live source).</param>
/// <param name="DecodeErrorCount">Count of errors encountered while reading/decoding chunks once a
/// connection is established.</param>
/// <param name="ReconnectAttempts">Count of reconnect attempts made after a connectivity failure.
/// Always 0 today: no source in this codebase currently implements reconnect-on-failure logic (a
/// failure surfaces once via <see cref="ProbeErrorCount"/>/<see cref="DecodeErrorCount"/> and the
/// read ends). This field exists so it becomes meaningful, without an API change, once retry logic
/// is added elsewhere -- adding that logic itself is out of scope for #35 ("Agent boundary:
/// Diagnostics only").</param>
/// <param name="CaptionsAvailable">Whether captions were found available on this source, or
/// <see langword="null"/> if not evaluated. Always <see langword="null"/> today: determining this
/// requires running <see cref="CaptionExtractor"/> against a whole file, a substantially heavier
/// operation than this per-chunk diagnostics snapshot, and a different architectural layer (this
/// class only knows about bytes, not caption semantics). Left as a documented gap rather than
/// wired up speculatively; see #35's follow-up discussion for when a real consumer needs it.</param>
/// <param name="MarkersAvailable">Whether SCTE-35 markers were found available on this source, or
/// <see langword="null"/> if not evaluated. Same rationale as <see cref="CaptionsAvailable"/>,
/// but for <see cref="Scte35MarkerExtractor"/>.</param>
public sealed record IngestDiagnostics(
    string SourceId,
    SourceHealth Health,
    string? Detail,
    long BytesProcessed,
    long ChunksProcessed,
    TimeSpan? LastMediaTimestamp,
    int ProbeErrorCount,
    int DecodeErrorCount,
    int ReconnectAttempts,
    bool? CaptionsAvailable = null,
    bool? MarkersAvailable = null);
