namespace SmartTVRelay.Core.Relay;

/// <summary>Specific relay-pipeline failure conditions #49 requires fail-open handling for.</summary>
public enum RelayFaultReason
{
    /// <summary>No enabled replacement media was available to schedule (catalog empty/all-disabled, or all candidates missing/corrupt).</summary>
    ReplacementMediaUnavailable,

    /// <summary>Enabled candidates existed, but none fit the target break duration.</summary>
    SchedulerFailure,

    /// <summary>No fresh replacement decision has been observed within the configured max age.</summary>
    StaleDecisionData,

    /// <summary>The continuous relay output pump faulted while reading from its source.</summary>
    OutputFailure,

    /// <summary>The broadcast source disconnected.</summary>
    SourceDisconnected,

    /// <summary>An observed lifecycle transition violated the expected sequence -- treated as unsafe to continue past.</summary>
    UnexpectedStateTransition,
}
