using Observation.Core;
using SmartTVRelay.Core;
using Xunit;

namespace SmartTVRelay.Core.Tests;

public sealed class BroadcastStateFusionTests
{
    [Fact]
    public void AgreeingDetectorsFuseToAgreedState()
    {
        var scope = ObservationScope.Of("feed-1");
        var observations = new[]
        {
            CreateObservation(scope, BroadcastState.Program, "detector-a", "commercial-fingerprint", 0.8),
            CreateObservation(scope, BroadcastState.Program, "detector-b", "black-frame", 0.7),
        };

        var result = Assert.Single(new BroadcastStateFusion().Fuse(observations));

        Assert.Equal(scope, result.Scope);
        Assert.Equal(BroadcastObservations.Subject, result.Subject);
        Assert.Equal(BroadcastObservations.Predicate, result.Predicate);
        Assert.Equal(BroadcastState.Program, result.Value);
        Assert.Equal(FusionStatus.Agreed, result.Status);
    }

    [Fact]
    public void DisagreementWithinConfiguredConfidenceMarginIsDisputedAndUnknown()
    {
        var scope = ObservationScope.Of("feed-1");
        var options = new ObservationFusionOptions<BroadcastState>(minimumConfidenceMargin: 0.2);
        var observations = new[]
        {
            CreateObservation(scope, BroadcastState.Program, "detector-a", "scene-cut", 0.8),
            CreateObservation(scope, BroadcastState.Commercial, "detector-b", "logo-presence", 0.7),
        };

        var result = Assert.Single(new BroadcastStateFusion(options).Fuse(observations));

        Assert.Equal(FusionStatus.Disputed, result.Status);
        Assert.Equal(BroadcastState.Unknown, result.Value);
    }

    [Fact]
    public void SingleLowConfidenceObservationRemainsAgreed()
    {
        var observations = new[]
        {
            CreateObservation(ObservationScope.Of("feed-1"), BroadcastState.Transition, "detector-a", "audio-loudness", 0.05),
        };

        var result = Assert.Single(new BroadcastStateFusion().Fuse(observations));

        Assert.Equal(FusionStatus.Agreed, result.Status);
        Assert.Equal(BroadcastState.Transition, result.Value);
        Assert.Equal(0.05, result.Confidence);
    }

    [Fact]
    public void DistinctScopesFuseIndependently()
    {
        var feedOne = ObservationScope.Of("feed-1");
        var feedTwo = ObservationScope.Of("feed-2");
        var observations = new[]
        {
            CreateObservation(feedOne, BroadcastState.Program, "detector-a", "explicit-marker", 0.9),
            CreateObservation(feedTwo, BroadcastState.Commercial, "detector-b", "caption-ocr", 0.9),
        };

        var results = new BroadcastStateFusion().Fuse(observations);

        Assert.Equal(2, results.Count);
        Assert.Equal(BroadcastState.Program, Assert.Single(results, result => result.Scope == feedOne).Value);
        Assert.Equal(BroadcastState.Commercial, Assert.Single(results, result => result.Scope == feedTwo).Value);
    }

    private static Observation<BroadcastState> CreateObservation(
        ObservationScope scope,
        BroadcastState value,
        string sourceId,
        string sourceKind,
        double confidence)
        => new(
            Guid.NewGuid(),
            scope,
            BroadcastObservations.Subject,
            BroadcastObservations.Predicate,
            value,
            sourceId,
            sourceKind,
            confidence,
            DateTimeOffset.UtcNow);
}
