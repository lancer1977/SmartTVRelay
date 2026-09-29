namespace SmartTVRelay.Core.Relay;

/// <summary>
/// Switches between delayed original programming and scheduled replacement, using
/// already-authorized <see cref="ReplacementDecision"/> values from
/// <see cref="BroadcastReplacementPolicy"/> -- this class makes no detection or fusion decisions
/// of its own, per the issue's agent boundary. <see cref="BroadcastReplacementPolicy"/> has
/// already collapsed disputed, unknown, stale, or low-confidence evidence into
/// <see cref="ReplacementDecision.PreserveOriginal"/>, so this state machine's contract --
/// only <see cref="ReplacementDecision.AuthorizeReplacement"/> can ever move it toward
/// <see cref="RelayState.Replacement"/> -- already guarantees disputed/unknown input cannot enter
/// replacement.
/// </summary>
/// <remarks>
/// <b>Asymmetric hysteresis (return-to-original is prioritized):</b> entering replacement requires
/// <see cref="ConfirmationsRequiredToEnterReplacement"/> consecutive authorize decisions (default
/// 2), to avoid committing to replacement on a single flickering tick. Leaving replacement takes
/// no such debounce -- a single non-authorize tick immediately starts returning to original, and
/// a single further tick completes the return. This matches the domain's prime safety rule: a
/// false replacement is a more severe failure than a missed commercial, so the state machine is
/// deliberately slow to enter and quick to leave.
/// <para>
/// <b>Faults:</b> <see cref="ReportFault"/> forces <see cref="RelayState.FailOpen"/> from any
/// state, unconditionally -- faults prefer original/fail-open. Recovering out of
/// <see cref="RelayState.FailOpen"/> is deliberately minimal here (<see cref="ClearFault"/> simply
/// returns to <see cref="RelayState.Original"/>); richer recovery timing/backoff is issue #49's
/// responsibility, not this state machine's.
/// </para>
/// </remarks>
public sealed class RelaySwitchingStateMachine
{
    private readonly int _confirmationsRequiredToEnterReplacement;
    private int _consecutiveAuthorizeCount;

    public RelaySwitchingStateMachine(int confirmationsRequiredToEnterReplacement = 2)
    {
        if (confirmationsRequiredToEnterReplacement < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confirmationsRequiredToEnterReplacement),
                confirmationsRequiredToEnterReplacement,
                "Must be at least 1.");
        }

        _confirmationsRequiredToEnterReplacement = confirmationsRequiredToEnterReplacement;
    }

    /// <summary>Number of consecutive <see cref="ReplacementDecision.AuthorizeReplacement"/> decisions required before committing to <see cref="RelayState.Replacement"/>.</summary>
    public int ConfirmationsRequiredToEnterReplacement => _confirmationsRequiredToEnterReplacement;

    /// <summary>Current state. Starts at <see cref="RelayState.Original"/>.</summary>
    public RelayState State { get; private set; } = RelayState.Original;

    /// <summary>
    /// Advances the state machine by one already-authorized policy decision and returns the
    /// resulting state. Has no effect while in <see cref="RelayState.FailOpen"/> -- a decision
    /// alone cannot recover from a fault; see <see cref="ClearFault"/>.
    /// </summary>
    public RelayState Advance(ReplacementDecision decision)
    {
        switch (State)
        {
            case RelayState.FailOpen:
                break;

            case RelayState.Original:
                if (decision == ReplacementDecision.AuthorizeReplacement)
                {
                    _consecutiveAuthorizeCount = 1;
                    State = _confirmationsRequiredToEnterReplacement <= 1
                        ? RelayState.Replacement
                        : RelayState.EnteringReplacement;
                }

                break;

            case RelayState.EnteringReplacement:
                if (decision == ReplacementDecision.AuthorizeReplacement)
                {
                    _consecutiveAuthorizeCount++;
                    if (_consecutiveAuthorizeCount >= _confirmationsRequiredToEnterReplacement)
                    {
                        State = RelayState.Replacement;
                    }
                }
                else
                {
                    // No debounce aborting entry -- return-to-original is prioritized.
                    _consecutiveAuthorizeCount = 0;
                    State = RelayState.Original;
                }

                break;

            case RelayState.Replacement:
                if (decision != ReplacementDecision.AuthorizeReplacement)
                {
                    State = RelayState.ReturningToOriginal;
                }

                break;

            case RelayState.ReturningToOriginal:
                if (decision == ReplacementDecision.AuthorizeReplacement)
                {
                    // Content resumed being commercial before the return completed -- go straight
                    // back to Replacement, which was already confirmed moments ago.
                    State = RelayState.Replacement;
                }
                else
                {
                    State = RelayState.Original;
                    _consecutiveAuthorizeCount = 0;
                }

                break;
        }

        return State;
    }

    /// <summary>Forces <see cref="RelayState.FailOpen"/> from any state. Faults prefer original/fail-open.</summary>
    public RelayState ReportFault()
    {
        State = RelayState.FailOpen;
        _consecutiveAuthorizeCount = 0;
        return State;
    }

    /// <summary>
    /// Clears a fault, returning to <see cref="RelayState.Original"/>. A no-op when not currently
    /// in <see cref="RelayState.FailOpen"/>. Deliberately minimal -- see this class's remarks.
    /// </summary>
    public RelayState ClearFault()
    {
        if (State == RelayState.FailOpen)
        {
            State = RelayState.Original;
        }

        return State;
    }
}
