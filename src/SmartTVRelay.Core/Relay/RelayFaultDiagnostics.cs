namespace SmartTVRelay.Core.Relay;

/// <summary>
/// Records why a <see cref="RelayFaultCoordinator"/> forced <see cref="RelayState.FailOpen"/>, and
/// the evidence/decision correlation ids involved where the triggering condition has any (some
/// fault sources -- e.g. scheduler/catalog outcomes -- have no per-item Guid correlation id to
/// carry, so this list is legitimately empty for those).
/// </summary>
public sealed record RelayFaultDiagnostics(
    RelayFaultReason Reason,
    DateTimeOffset DetectedAt,
    string Message,
    IReadOnlyList<Guid> CorrelationIds);
