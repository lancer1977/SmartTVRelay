namespace SmartTVRelay.Core;

using Observation.Core;

public sealed class BroadcastReplacementPolicy
{
    private readonly double confidenceThreshold;

    public BroadcastReplacementPolicy(double confidenceThreshold = 0.75)
    {
        if (!double.IsFinite(confidenceThreshold) || confidenceThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidenceThreshold));
        }

        this.confidenceThreshold = confidenceThreshold;
    }

    public ReplacementPolicyResult Evaluate(FusionResult<BroadcastState> fusion)
    {
        // Fail-open: anything other than a clean Agreed/Resolved result
        // preserves the original broadcast.
        if (fusion.Status is not (FusionStatus.Agreed or FusionStatus.Resolved))
        {
            return new ReplacementPolicyResult(ReplacementDecision.PreserveOriginal, BroadcastState.Unknown, fusion);
        }

        var state = fusion.Value;

        if (fusion.Confidence < confidenceThreshold)
        {
            return new ReplacementPolicyResult(ReplacementDecision.PreserveOriginal, state, fusion);
        }

        var decision = state == BroadcastState.Commercial
            ? ReplacementDecision.AuthorizeReplacement
            : ReplacementDecision.PreserveOriginal;

        return new ReplacementPolicyResult(decision, state, fusion);
    }
}
