namespace SmartTVRelay.Core.Relay;

/// <summary>
/// State of the relay switching state machine (#48). At minimum per the issue: Original,
/// EnteringReplacement, Replacement, ReturningToOriginal, and a combined Faulted/FailOpen state.
/// </summary>
public enum RelayState
{
    /// <summary>Relaying the original broadcast; the default and safest state.</summary>
    Original,

    /// <summary>Replacement has been authorized but not yet confirmed for long enough to commit to it.</summary>
    EnteringReplacement,

    /// <summary>Actively relaying replacement content.</summary>
    Replacement,

    /// <summary>Replacement is no longer authorized; transitioning back to <see cref="Original"/>.</summary>
    ReturningToOriginal,

    /// <summary>
    /// A fault occurred (e.g. the relay output faulted, or the caller otherwise reports a
    /// condition that makes continuing unsafe). Fail-open: behaves as if relaying original
    /// content. Recovering out of this state is deliberately minimal here -- richer
    /// backoff/recovery timing is issue #49's responsibility, not this state machine's.
    /// </summary>
    FailOpen,
}
