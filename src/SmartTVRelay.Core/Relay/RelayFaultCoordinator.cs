using SmartTVRelay.Core.Scheduling;

namespace SmartTVRelay.Core.Relay;

/// <summary>
/// Translates specific relay-pipeline failure conditions (#49) into
/// <see cref="RelaySwitchingStateMachine.ReportFault"/> calls, and recovery back out of
/// <see cref="RelayState.FailOpen"/> via <see cref="Recover"/>. Every fault path here funnels
/// through the same underlying call, whose only effect is forcing fail-open/original programming
/// (per #48, decisions have no effect while <see cref="RelayState.FailOpen"/>) -- so per AGENTS.md's
/// prime safety rule, no case this class handles can ever increase replacement authority, only
/// reduce it.
/// </summary>
/// <remarks>
/// Recovery boundary only (this class's own agent boundary, per the issue): it does not decide
/// *when* it is safe to recover (retry/backoff timing, reconnection policy) -- callers observe the
/// underlying condition has actually cleared (a successful reconnect, a corrected schedule) and
/// then call <see cref="Recover"/> explicitly. Recovery is deterministic because it is never
/// inferred or time-based; it only ever happens on an explicit call.
/// </remarks>
public sealed class RelayFaultCoordinator
{
    private readonly RelaySwitchingStateMachine _stateMachine;
    private readonly TimeSpan _maxDecisionAge;
    private DateTimeOffset? _lastDecisionAt;
    private RelayOutputStatus _lastObservedOutputStatus = RelayOutputStatus.Idle;

    public RelayFaultCoordinator(RelaySwitchingStateMachine stateMachine, TimeSpan? maxDecisionAge = null)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);

        if (maxDecisionAge is { } age && age <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDecisionAge), age, "Must be positive.");
        }

        _stateMachine = stateMachine;
        _maxDecisionAge = maxDecisionAge ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Current relay state, passed through from the wrapped state machine.</summary>
    public RelayState State => _stateMachine.State;

    /// <summary>The most recently recorded fault, or null if none has occurred since construction or the last <see cref="Recover"/>.</summary>
    public RelayFaultDiagnostics? LastFault { get; private set; }

    /// <summary>
    /// Feeds an already-authorized policy decision through as normal operation, and records `now`
    /// as the most recent time fresh decision data was observed (see <see cref="CheckDecisionFreshness"/>).
    /// </summary>
    public RelayState Advance(ReplacementDecision decision, DateTimeOffset now)
    {
        _lastDecisionAt = now;
        return _stateMachine.Advance(decision);
    }

    /// <summary>
    /// Case: stale detector/policy data. If no decision has been observed within the configured
    /// max age (including never having received one), continuing to rely on it is exactly the
    /// stale-evidence case AGENTS.md's prime safety rule says must not carry authority.
    /// </summary>
    public RelayState CheckDecisionFreshness(DateTimeOffset now, IReadOnlyList<Guid>? correlationIds = null)
    {
        var age = _lastDecisionAt is { } last ? now - last : TimeSpan.MaxValue;
        if (age > _maxDecisionAge)
        {
            return Fault(
                RelayFaultReason.StaleDecisionData,
                now,
                $"No fresh replacement decision observed within {_maxDecisionAge}.",
                correlationIds ?? []);
        }

        return State;
    }

    /// <summary>
    /// Case: relay output failure, and (defensively) unexpected output lifecycle transitions.
    /// A transition outside the expected Idle -> Running -> (Completed | Faulted) sequence means
    /// something in the pipeline behaved outside its documented contract, which is unsafe to
    /// continue past -- per the prime safety rule, unexpected conditions preserve original
    /// programming rather than being assumed benign.
    /// </summary>
    public RelayState ObserveOutputStatus(RelayOutputStatus status, DateTimeOffset now)
    {
        var previous = _lastObservedOutputStatus;
        _lastObservedOutputStatus = status;

        if (!IsExpectedOutputTransition(previous, status))
        {
            return Fault(
                RelayFaultReason.UnexpectedStateTransition,
                now,
                $"Unexpected relay output transition {previous} -> {status}.",
                []);
        }

        if (status == RelayOutputStatus.Faulted)
        {
            return Fault(RelayFaultReason.OutputFailure, now, "Relay output pump faulted.", []);
        }

        return State;
    }

    /// <summary>Case: replacement media missing/corrupt (no usable candidates) or scheduler failure (no combination fits).</summary>
    public RelayState ObserveSchedule(ReplacementSchedule schedule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return schedule.Outcome switch
        {
            SchedulingOutcome.NoMedia => Fault(
                RelayFaultReason.ReplacementMediaUnavailable, now, "No enabled replacement media available.", []),
            SchedulingOutcome.NoFit => Fault(
                RelayFaultReason.SchedulerFailure, now, "No combination of candidates fits the target duration.", []),
            _ => State,
        };
    }

    /// <summary>
    /// Case: source reconnect. Reported explicitly by the caller when the broadcast source itself
    /// disconnects -- distinct from <see cref="ObserveOutputStatus"/>'s <see cref="RelayOutputStatus.Faulted"/>,
    /// which reflects the output pump giving up on a source failure rather than the source
    /// connection state itself.
    /// </summary>
    public RelayState ReportSourceDisconnected(DateTimeOffset now, string? detail = null)
        => Fault(RelayFaultReason.SourceDisconnected, now, detail ?? "Broadcast source disconnected.", []);

    /// <summary>
    /// Recovers deterministically: clears the underlying state machine's fault (returning to
    /// <see cref="RelayState.Original"/>), clears recorded diagnostics, and resets decision
    /// freshness tracking to `now` so a stale-data fault cannot immediately refire before any new
    /// decision has had a chance to arrive. Only ever happens on this explicit call -- never
    /// inferred or time-based -- which is what makes recovery deterministic.
    /// </summary>
    public RelayState Recover(DateTimeOffset now)
    {
        var result = _stateMachine.ClearFault();
        LastFault = null;
        _lastDecisionAt = now;
        _lastObservedOutputStatus = RelayOutputStatus.Idle;
        return result;
    }

    private RelayState Fault(RelayFaultReason reason, DateTimeOffset now, string message, IReadOnlyList<Guid> correlationIds)
    {
        LastFault = new RelayFaultDiagnostics(reason, now, message, correlationIds);
        return _stateMachine.ReportFault();
    }

    private static bool IsExpectedOutputTransition(RelayOutputStatus from, RelayOutputStatus to)
    {
        if (from == to)
        {
            return true;
        }

        return from switch
        {
            RelayOutputStatus.Idle => to == RelayOutputStatus.Running,
            RelayOutputStatus.Running => to is RelayOutputStatus.Completed or RelayOutputStatus.Faulted,
            _ => false, // Completed/Faulted are terminal -- any further transition is unexpected.
        };
    }
}
