namespace SmartTVRelay.Core.Catalog;

/// <summary>
/// Describes container and codec compatibility for a replacement clip, derived from ffprobe
/// inspection. This is a descriptive summary only -- it does not judge whether the combination is
/// "supported" for playback. That judgment (and any duration-matching/selection logic) belongs to
/// downstream scheduling/selection work, which is explicitly out of scope for the catalog.
/// </summary>
public sealed record CodecCompatibilitySummary(
    string Container,
    string? VideoCodec,
    string? AudioCodec);

/// <summary>
/// A single indexed replacement clip in the media catalog.
/// </summary>
/// <param name="Id">
/// Deterministic identifier for this entry, derived from the file path (currently the path
/// itself). Never a random value -- the same file must always produce the same Id across scans so
/// the catalog is stable/idempotent.
/// </param>
/// <param name="Path">Full path to the media file on disk.</param>
/// <param name="Duration">Media duration, as reported by ffprobe.</param>
/// <param name="Codec">Codec/container compatibility summary.</param>
/// <param name="Tags">
/// User/operator-supplied tags. The scanner has no way to infer intended tags from the media file
/// itself, so freshly-scanned entries always have an empty tag list; tagging is left to a future
/// enrichment step (e.g. a sidecar/manifest), which is out of scope for this issue.
/// </param>
/// <param name="Enabled">
/// Whether this clip is eligible for use. Defaults to true for freshly-scanned entries, since the
/// catalog itself makes no selection/eligibility decisions -- that belongs to issue #47.
/// </param>
public sealed record ReplacementClipEntry(
    string Id,
    string Path,
    TimeSpan Duration,
    CodecCompatibilitySummary Codec,
    IReadOnlyList<string> Tags,
    bool Enabled);

/// <summary>
/// A file that was considered for the catalog but could not be indexed, along with a diagnostic
/// reason. Invalid files are recorded, not silently dropped and not allowed to abort the scan.
/// </summary>
public sealed record InvalidCatalogEntry(
    string Path,
    string Reason);

/// <summary>
/// The result of scanning a directory of replacement clips: valid entries and invalid files, each
/// sorted deterministically by path (ordinal comparison) so repeated scans of the same directory
/// contents always produce the same catalog, regardless of OS directory-enumeration order.
/// </summary>
public sealed record CatalogScanResult(
    IReadOnlyList<ReplacementClipEntry> Entries,
    IReadOnlyList<InvalidCatalogEntry> Invalid);
