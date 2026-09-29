namespace SmartTVRelay.Core.Vlm;

/// <summary>
/// Isolated adapter boundary for an optional local vision-language model used as a last-resort
/// commercial/program classifier (#43). Per AGENTS.md's evidence priority ("do not introduce a VLM
/// dependency merely because image analysis is involved"), this sits behind deterministic evidence --
/// explicit markers, known fingerprints, deterministic visual/audio features, captions/OCR.
/// </summary>
public interface ILocalVlmClient
{
    /// <summary>
    /// Classifies a single bounded image. Never throws for model, timeout, or parse failures --
    /// those are reported through the returned <see cref="LocalVlmClassification"/> instead, so a
    /// caller cannot accidentally let a VLM failure propagate as an unhandled exception into a
    /// decision path that must otherwise fail open.
    /// </summary>
    Task<LocalVlmClassification> ClassifyAsync(ReadOnlyMemory<byte> imageJpegBytes, CancellationToken cancellationToken);
}
