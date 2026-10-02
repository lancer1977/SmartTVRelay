using SmartTVRelay.Core.Relay;
using SmartTVRelay.Core.Scheduling;

namespace SmartTVRelay.Core.Telemetry;

/// <summary>
/// Builds and accumulates #50's structured telemetry trace: one <see cref="SubstitutionDecisionEvent"/>
/// per segment's replacement decision, and one <see cref="RelayFaultTelemetryEvent"/> per fail-open
/// or recovery event. Telemetry only, per the issue's agent boundary -- this class never influences
/// fusion, policy, scheduling, or the relay state machine; it only observes and records their
/// already-made outputs.
/// </summary>
public sealed class SubstitutionTelemetryRecorder
{
    private readonly List<ISubstitutionTelemetryEvent> _events = [];

    /// <summary>The full ordered trace, decision and fault/recovery events interleaved in recording order.</summary>
    public IReadOnlyList<ISubstitutionTelemetryEvent> Events => _events;

    /// <summary>Just the decision events, in recording order.</summary>
    public IEnumerable<SubstitutionDecisionEvent> DecisionEvents => _events.OfType<SubstitutionDecisionEvent>();

    /// <summary>Just the fault/recovery events, in recording order.</summary>
    public IEnumerable<RelayFaultTelemetryEvent> FaultEvents => _events.OfType<RelayFaultTelemetryEvent>();

    /// <summary>
    /// Records why a replacement decision occurred for one segment. <paramref name="correlationId"/>
    /// defaults to a fresh id when the caller has no existing correlation id for this segment to
    /// thread through (e.g. from an upstream request/trace context).
    /// </summary>
    public SubstitutionDecisionEvent RecordDecision(
        DateTimeOffset segmentStart,
        DateTimeOffset segmentEnd,
        ReplacementPolicyResult policyResult,
        RelayState relayState,
        double policyThreshold,
        ReplacementSchedule? selectedSchedule = null,
        Guid? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(policyResult);

        var fusion = policyResult.Fusion;
        var evidence = fusion.Candidates
            .Select(c => new EvidenceSummary(c.Value, c.Confidence, c.EvidenceIds))
            .ToArray();

        var evt = new SubstitutionDecisionEvent(
            correlationId ?? Guid.NewGuid(),
            segmentStart,
            segmentEnd,
            policyResult.Decision,
            relayState,
            fusion.Value,
            fusion.Confidence,
            policyThreshold,
            evidence,
            DescribeSchedule(selectedSchedule));

        _events.Add(evt);
        return evt;
    }

    /// <summary>Records a fail-open event from a <see cref="RelayFaultCoordinator"/>'s diagnostics.</summary>
    public RelayFaultTelemetryEvent RecordFault(RelayFaultDiagnostics diagnostics, RelayState relayState, Guid? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var evt = new RelayFaultTelemetryEvent(
            correlationId ?? Guid.NewGuid(),
            diagnostics.DetectedAt,
            RelayFaultTelemetryKind.Fault,
            relayState,
            diagnostics.Reason,
            diagnostics.Message);

        _events.Add(evt);
        return evt;
    }

    /// <summary>Records an explicit, deterministic recovery event (see #49's <see cref="RelayFaultCoordinator.Recover"/>).</summary>
    public RelayFaultTelemetryEvent RecordRecovery(DateTimeOffset occurredAt, RelayState relayState, Guid? correlationId = null)
    {
        var evt = new RelayFaultTelemetryEvent(
            correlationId ?? Guid.NewGuid(),
            occurredAt,
            RelayFaultTelemetryKind.Recovery,
            relayState,
            Reason: null,
            Message: null);

        _events.Add(evt);
        return evt;
    }

    private static string? DescribeSchedule(ReplacementSchedule? schedule)
    {
        if (schedule is null)
        {
            return null;
        }

        return $"{schedule.Outcome}: {schedule.SelectedClips.Count} clip(s), {schedule.TotalDuration} of {schedule.TargetDuration} target";
    }
}
