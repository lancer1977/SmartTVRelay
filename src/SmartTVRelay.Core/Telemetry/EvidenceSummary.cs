namespace SmartTVRelay.Core.Telemetry;

/// <summary>
/// Compact, non-sensitive summary of one fused evidence candidate contributing to a replacement
/// decision (#50) -- only the candidate's fused value, confidence, and the evidence ids behind it.
/// Fusion itself does not preserve per-evidence source kind once candidates are merged, and this
/// type intentionally carries no raw media payload, only provenance metadata.
/// </summary>
public sealed record EvidenceSummary(BroadcastState CandidateValue, double Confidence, IReadOnlyList<Guid> EvidenceIds);
