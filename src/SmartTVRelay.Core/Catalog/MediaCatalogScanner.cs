namespace SmartTVRelay.Core.Catalog;

using SmartTVRelay.Core.Ingest;

/// <summary>
/// Scans a directory of user-owned replacement clips and builds a normalized, deterministic
/// catalog of their metadata (duration, codec/container compatibility). Reuses
/// <see cref="TransportStreamInspector"/> for ffprobe plumbing rather than shelling out directly.
///
/// Catalog only: this class does not decide which clip should play when, does not perform
/// duration-matching, and does not otherwise implement selection/scheduling logic. That is issue
/// #47's responsibility, not this one's.
/// </summary>
public sealed class MediaCatalogScanner
{
    /// <summary>
    /// File extensions considered plausible replacement-clip media and worth probing. Anything
    /// else in the scanned directory (e.g. ".txt", ".json", ".nfo", directories) is silently
    /// skipped -- it is not media at all, so it is not reported as an invalid catalog entry either.
    /// This list intentionally covers common container formats this repo already works with
    /// (transport streams) plus common file-based replacement-clip formats.
    /// </summary>
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".mp4", ".mkv", ".mov", ".m4v", ".avi", ".webm",
    };

    private readonly TransportStreamInspector _inspector;

    /// <summary>
    /// Initializes a new MediaCatalogScanner.
    /// </summary>
    /// <param name="inspector">
    /// The ffprobe-backed inspector used to read media metadata. Defaults to a new
    /// <see cref="TransportStreamInspector"/> with its own default ffprobe path/timeout when not
    /// supplied.
    /// </param>
    public MediaCatalogScanner(TransportStreamInspector? inspector = null)
    {
        _inspector = inspector ?? new TransportStreamInspector();
    }

    /// <summary>
    /// Scans <paramref name="directoryPath"/> for candidate media files and builds a catalog.
    /// Deterministic: file candidates are always sorted by full path (ordinal comparison) before
    /// probing, so the same directory contents always produce the same catalog in the same order,
    /// regardless of the underlying filesystem's directory-enumeration order.
    ///
    /// A per-file failure (ffprobe fails/throws, zero duration, no usable streams) is recorded as
    /// an <see cref="InvalidCatalogEntry"/> with a diagnostic reason; it never aborts the scan.
    /// </summary>
    /// <param name="directoryPath">Directory to scan. A missing directory yields an empty result.</param>
    /// <param name="cancellationToken">
    /// Caller cancellation token. Cancelling this aborts the whole scan (propagates as
    /// <see cref="OperationCanceledException"/>) -- distinct from a single file's own ffprobe
    /// timing out internally, which is recorded as an invalid entry instead.
    /// </param>
    public async Task<CatalogScanResult> ScanAsync(string directoryPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            return new CatalogScanResult(Array.Empty<ReplacementClipEntry>(), Array.Empty<InvalidCatalogEntry>());
        }

        // Directory.EnumerateFiles' order is filesystem/OS-dependent and unspecified -- never rely
        // on it directly. Sorting by full path (ordinal) is what actually makes the scan
        // deterministic across runs/machines.
        var candidatePaths = Directory.EnumerateFiles(directoryPath)
            .Where(path => MediaExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var entries = new List<ReplacementClipEntry>();
        var invalid = new List<InvalidCatalogEntry>();

        foreach (var path in candidatePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TransportStreamMetadata metadata;
            try
            {
                metadata = await _inspector.InspectAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Genuine caller-requested cancellation: abort the whole scan rather than
                // recording it as an invalid file.
                throw;
            }
            catch (Exception ex)
            {
                // Anything else -- ffprobe exiting non-zero, the subprocess failing to start, the
                // inspector's own internal timeout (surfaces as OperationCanceledException too,
                // but the caller's token is NOT cancelled in that case, so it lands here instead of
                // the rethrow above), malformed output, etc. One bad file must never abort the scan.
                invalid.Add(new InvalidCatalogEntry(path, $"ffprobe failed: {ex.Message}"));
                continue;
            }

            var hasVideo = metadata.Streams.Any(stream => stream.CodecType == "video");
            var hasAudio = metadata.Streams.Any(stream => stream.CodecType == "audio");

            if (!hasVideo && !hasAudio)
            {
                invalid.Add(new InvalidCatalogEntry(path, "no video or audio streams found"));
                continue;
            }

            if (metadata.Duration is null || metadata.Duration <= 0)
            {
                invalid.Add(new InvalidCatalogEntry(path, "ffprobe reported zero or missing duration"));
                continue;
            }

            var videoCodec = metadata.Streams.FirstOrDefault(stream => stream.CodecType == "video")?.CodecName;
            var audioCodec = metadata.Streams.FirstOrDefault(stream => stream.CodecType == "audio")?.CodecName;
            var codec = new CodecCompatibilitySummary(metadata.FormatName, videoCodec, audioCodec);

            entries.Add(new ReplacementClipEntry(
                Id: path,
                Path: path,
                Duration: TimeSpan.FromSeconds(metadata.Duration.Value),
                Codec: codec,
                Tags: Array.Empty<string>(),
                Enabled: true));
        }

        return new CatalogScanResult(entries, invalid);
    }
}
