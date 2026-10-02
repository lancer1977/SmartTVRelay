using Observation.Core;
using SmartTVRelay.Core;
using Xunit;

namespace SmartTVRelay.Core.Tests;

public sealed class BroadcastReplacementPolicyTests
{
    [Fact]
    public void ConfidentCommercialAuthorizesReplacement()
    {
        var scope = ObservationScope.Of("feed-1");
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Commercial,
            0.9,
            FusionStatus.Agreed,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy();
        var result = policy.Evaluate(fusion);

        Assert.Equal(ReplacementDecision.AuthorizeReplacement, result.Decision);
        Assert.Equal(BroadcastState.Commercial, result.EffectiveState);
    }

    [Fact]
    public void ConfidentProgramPreservesOriginal()
    {
        var scope = ObservationScope.Of("feed-1");
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Program,
            0.9,
            FusionStatus.Agreed,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy();
        var result = policy.Evaluate(fusion);

        Assert.Equal(ReplacementDecision.PreserveOriginal, result.Decision);
        Assert.Equal(BroadcastState.Program, result.EffectiveState);
    }

    [Fact]
    public void DisputedStatusPreservesOriginal()
    {
        var scope = ObservationScope.Of("feed-1");
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Unknown,
            0.5,
            FusionStatus.Disputed,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy();
        var result = policy.Evaluate(fusion);

        Assert.Equal(ReplacementDecision.PreserveOriginal, result.Decision);
        Assert.Equal(BroadcastState.Unknown, result.EffectiveState);
    }

    [Fact]
    public void ConfidenceBelowThresholdPreservesOriginal()
    {
        var scope = ObservationScope.Of("feed-1");
        const double threshold = 0.75;
        const double belowThreshold = 0.74;
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Commercial,
            belowThreshold,
            FusionStatus.Agreed,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy(threshold);
        var result = policy.Evaluate(fusion);

        Assert.Equal(ReplacementDecision.PreserveOriginal, result.Decision);
        Assert.Equal(BroadcastState.Commercial, result.EffectiveState);
    }

    [Fact]
    public void ConfidenceAtThresholdAuthorizesReplacement()
    {
        var scope = ObservationScope.Of("feed-1");
        const double threshold = 0.75;
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Commercial,
            threshold,
            FusionStatus.Agreed,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy(threshold);
        var result = policy.Evaluate(fusion);

        Assert.Equal(ReplacementDecision.AuthorizeReplacement, result.Decision);
        Assert.Equal(BroadcastState.Commercial, result.EffectiveState);
    }

    [Fact]
    public void ConfidenceAboveThresholdAuthorizesReplacement()
    {
        var scope = ObservationScope.Of("feed-1");
        const double threshold = 0.75;
        const double aboveThreshold = 0.76;
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Commercial,
            aboveThreshold,
            FusionStatus.Agreed,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy(threshold);
        var result = policy.Evaluate(fusion);

        Assert.Equal(ReplacementDecision.AuthorizeReplacement, result.Decision);
        Assert.Equal(BroadcastState.Commercial, result.EffectiveState);
    }

    [Fact]
    public void ConflictingEvidencePreservesOriginal()
    {
        var scope = ObservationScope.Of("feed-1");
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Unknown,
            0.6,
            FusionStatus.Disputed,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy();
        var result = policy.Evaluate(fusion);

        Assert.Equal(ReplacementDecision.PreserveOriginal, result.Decision);
        Assert.Equal(BroadcastState.Unknown, result.EffectiveState);
    }

    [Fact]
    public void ResultFusionExposesEvidenceForDiagnostics()
    {
        var scope = ObservationScope.Of("feed-1");
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Commercial,
            0.9,
            FusionStatus.Agreed,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy();
        var result = policy.Evaluate(fusion);

        Assert.Equal(fusion, result.Fusion);
    }

    [Fact]
    public void UnknownStatusPreservesOriginal()
    {
        var scope = ObservationScope.Of("feed-1");
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Unknown,
            0.5,
            FusionStatus.Unknown,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy();
        var result = policy.Evaluate(fusion);

        Assert.Equal(ReplacementDecision.PreserveOriginal, result.Decision);
        Assert.Equal(BroadcastState.Unknown, result.EffectiveState);
    }

    [Fact]
    public void ResolvedStatusWithCommercialAuthorizesReplacement()
    {
        var scope = ObservationScope.Of("feed-1");
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Commercial,
            0.9,
            FusionStatus.Resolved,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy();
        var result = policy.Evaluate(fusion);

        Assert.Equal(ReplacementDecision.AuthorizeReplacement, result.Decision);
        Assert.Equal(BroadcastState.Commercial, result.EffectiveState);
    }

    [Fact]
    public void TransitionStateNeverAuthorizesReplacement()
    {
        var scope = ObservationScope.Of("feed-1");
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Transition,
            0.95,
            FusionStatus.Agreed,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy();
        var result = policy.Evaluate(fusion);

        Assert.Equal(ReplacementDecision.PreserveOriginal, result.Decision);
        Assert.Equal(BroadcastState.Transition, result.EffectiveState);
    }

    [Fact]
    public void CustomThresholdIsRespected()
    {
        var scope = ObservationScope.Of("feed-1");
        const double customThreshold = 0.9;
        const double justBelowCustom = 0.89;
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Commercial,
            justBelowCustom,
            FusionStatus.Agreed,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy(customThreshold);
        var result = policy.Evaluate(fusion);

        Assert.Equal(ReplacementDecision.PreserveOriginal, result.Decision);
    }

    [Fact]
    public void DefaultConfidenceThresholdIsPointSeventyFive()
    {
        var scope = ObservationScope.Of("feed-1");
        const double atDefaultThreshold = 0.75;
        var fusion = new FusionResult<BroadcastState>(
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            BroadcastState.Commercial,
            atDefaultThreshold,
            FusionStatus.Agreed,
            Array.Empty<Guid>(),
            Array.Empty<FusionCandidate<BroadcastState>>(),
            Array.Empty<Guid>());

        var policy = new BroadcastReplacementPolicy();
        var result = policy.Evaluate(fusion);

        Assert.Equal(ReplacementDecision.AuthorizeReplacement, result.Decision);
    }

    [Fact]
    public void InvalidThresholdThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastReplacementPolicy(-0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastReplacementPolicy(1.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastReplacementPolicy(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastReplacementPolicy(double.PositiveInfinity));
    }
}
