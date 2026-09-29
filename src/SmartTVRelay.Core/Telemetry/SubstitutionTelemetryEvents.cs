using SmartTVRelay.Core.Relay;

namespace SmartTVRelay.Core.Telemetry;

/// <summary>Common shape shared by every event #50's telemetry trace can hold, so a consumer can walk the whole ordered trace without knowing each concrete event type up front.</summary>
public interface ISubstitutionTelemetryEvent
{
    /// <summary>Correlates this event to the segment/fault lifecycle it belongs to.</summary>
    Guid CorrelationId { get; }

    /// <summary>When this event was recorded.</summary>
    DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// One structured, traceable record of why a replacement decision occurred for a single segment
/// (#50). Carries only ids, enums, numbers, short strings, and timestamps -- no raw media payload
/// -- so it can be logged or exported without ever leaking frame/audio content.
/// </summary>
public sealed record SubstitutionDecisionEvent(
    Guid CorrelationId,
    DateTimeOffset SegmentStart,
    DateTimeOffset SegmentEnd,
    ReplacementDecision PolicyDecision,
    RelayState RelayState,
    BroadcastState FusedState,
    double FusedConfidence,
    double PolicyThreshold,
    IReadOnlyList<EvidenceSummary> ContributingEvidence,
    string? SelectedScheduleSummary) : ISubstitutionTelemetryEvent
{
    /// <summary>This event's timestamp for trace-ordering purposes -- the segment's end, since that is when the decision for it became final.</summary>
    public DateTimeOffset OccurredAt => SegmentEnd;

    /// <summary>
    /// True only once the decision actually took effect on the relay output -- distinct from
    /// merely being policy-authorized, since #48's debounced entry means an
    /// <see cref="ReplacementDecision.AuthorizeReplacement"/> decision does not immediately commit
    /// to <see cref="RelayState.Replacement"/>. Everything else (<see cref="RelayState.Original"/>,
    /// <see cref="RelayState.EnteringReplacement"/>, <see cref="RelayState.ReturningToOriginal"/>,
    /// <see cref="RelayState.FailOpen"/>) is "denied" in the sense that no replacement is airing as
    /// of this event -- callers that need the finer distinction still have the full
    /// <see cref="RelayState"/> and <see cref="PolicyDecision"/> to inspect.
    /// </summary>
    public bool Executed => RelayState == RelayState.Replacement;
}

/// <summary>Distinguishes the two lifecycle events <see cref="RelayFaultTelemetryEvent"/> can represent.</summary>
public enum RelayFaultTelemetryKind
{
    /// <summary>A fault forced <see cref="RelayState.FailOpen"/>.</summary>
    Fault,

    /// <summary>An explicit, deterministic recovery cleared a fault.</summary>
    Recovery,
}

/// <summary>
/// Records a fail-open or recovery event (#50's "fail-open/recovery events" data point) --
/// deliberately a separate event shape from <see cref="SubstitutionDecisionEvent"/>, since a fault
/// is not itself a per-segment replacement decision.
/// </summary>
public sealed record RelayFaultTelemetryEvent(
    Guid CorrelationId,
    DateTimeOffset OccurredAt,
    RelayFaultTelemetryKind Kind,
    RelayState RelayState,
    RelayFaultReason? Reason,
    string? Message) : ISubstitutionTelemetryEvent;
