namespace SmartTVRelay.Core;

using Observation.Core;

/// <summary>
/// Pairs a caption/OCR-derived <see cref="Observation{BroadcastState}"/> with the specific
/// commercial-indicative phrases that were matched to produce it.
/// </summary>
/// <remarks>
/// <see cref="Observation{T}"/>'s actual shape (confirmed by inspecting the compiled
/// Observation.Core contract) is exactly: Id, Scope, Subject, Predicate, Value, SourceId,
/// SourceKind, Confidence, ObservedAt -- there is no free-text or structured-tag field for
/// "why". <see cref="Observation{T}.SourceId"/> is identity/scope (which capture source this
/// came from), not evidence, so matched phrases must never be packed into it.
///
/// Rather than lose the "provenance includes matched features" requirement, or invent a
/// side-channel keyed by <see cref="Observation{T}.Id"/>, <see cref="CaptionOcrDetector"/>
/// returns this wrapper: the untouched <see cref="Observation{BroadcastState}"/> (so callers
/// that only need fusion input can project <c>.Observation</c> straight into
/// <see cref="BroadcastStateFusion.Fuse"/>) plus the matched phrases alongside it, for callers
/// that need the "why" -- audit logs, evaluation/metrics tooling, debugging false positives.
/// </remarks>
/// <param name="Observation">The emitted commercial-likelihood evidence.</param>
/// <param name="MatchedFeatures">
/// The commercial-indicative phrases (from the detector's configured rule set) that were found
/// in the source caption text, in rule-set order. Never empty for an emitted observation.
/// </param>
public sealed record CaptionCommercialObservation(
    Observation<BroadcastState> Observation,
    IReadOnlyList<string> MatchedFeatures);
