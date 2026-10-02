namespace SmartTVRelay.Core;

using Observation.Core;

public sealed record ReplacementPolicyResult(
    ReplacementDecision Decision,
    BroadcastState EffectiveState,
    FusionResult<BroadcastState> Fusion);
