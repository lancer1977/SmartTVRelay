namespace SmartTVRelay.Core.Tests.Vlm;

using SmartTVRelay.Core;
using SmartTVRelay.Core.Vlm;
using Xunit;

public class LocalVlmCommercialObserverTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_InvalidMinConfidence_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalVlmCommercialObserver(minConfidence: 1.5));
    }

    [Fact]
    public void Detect_SuccessfulHighConfidenceCommercial_EmitsObservation()
    {
        var observer = new LocalVlmCommercialObserver(minConfidence: 0.6);
        var classification = new LocalVlmClassification(BroadcastState.Commercial, 0.9, TimeSpan.FromMilliseconds(200), false, null);
        var samples = new[] { new LocalVlmSample(classification, Epoch) };

        var result = observer.Detect("cam-1", samples);

        var observation = Assert.Single(result);
        Assert.Equal(BroadcastState.Commercial, observation.Value);
        Assert.Equal(0.9, observation.Confidence);
        Assert.Equal(LocalVlmCommercialObserver.SourceKind, observation.SourceKind);
        Assert.Equal(Epoch, observation.ObservedAt);
    }

    [Fact]
    public void Detect_TimedOut_EmitsNothing()
    {
        var observer = new LocalVlmCommercialObserver();
        var classification = LocalVlmClassification.TimedOutResult(TimeSpan.FromSeconds(5));
        var samples = new[] { new LocalVlmSample(classification, Epoch) };

        var result = observer.Detect("cam-1", samples);

        Assert.Empty(result);
    }

    [Fact]
    public void Detect_Errored_EmitsNothing()
    {
        var observer = new LocalVlmCommercialObserver();
        var classification = LocalVlmClassification.Failed(TimeSpan.FromMilliseconds(50), "boom");
        var samples = new[] { new LocalVlmSample(classification, Epoch) };

        var result = observer.Detect("cam-1", samples);

        Assert.Empty(result);
    }

    [Fact]
    public void Detect_UnknownState_EmitsNothing()
    {
        var observer = new LocalVlmCommercialObserver();
        var classification = new LocalVlmClassification(BroadcastState.Unknown, 0.99, TimeSpan.FromMilliseconds(200), false, null);
        var samples = new[] { new LocalVlmSample(classification, Epoch) };

        var result = observer.Detect("cam-1", samples);

        Assert.Empty(result);
    }

    [Fact]
    public void Detect_BelowMinConfidence_EmitsNothing()
    {
        var observer = new LocalVlmCommercialObserver(minConfidence: 0.6);
        var classification = new LocalVlmClassification(BroadcastState.Commercial, 0.59, TimeSpan.FromMilliseconds(200), false, null);
        var samples = new[] { new LocalVlmSample(classification, Epoch) };

        var result = observer.Detect("cam-1", samples);

        Assert.Empty(result);
    }

    [Fact]
    public void Detect_AtExactMinConfidence_EmitsObservation()
    {
        var observer = new LocalVlmCommercialObserver(minConfidence: 0.6);
        var classification = new LocalVlmClassification(BroadcastState.Program, 0.6, TimeSpan.FromMilliseconds(200), false, null);
        var samples = new[] { new LocalVlmSample(classification, Epoch) };

        var result = observer.Detect("cam-1", samples);

        Assert.Single(result);
    }

    [Fact]
    public void Detect_MultipleSamples_OnlyQualifyingOnesEmitted()
    {
        var observer = new LocalVlmCommercialObserver(minConfidence: 0.6);
        var samples = new[]
        {
            new LocalVlmSample(new LocalVlmClassification(BroadcastState.Commercial, 0.9, TimeSpan.Zero, false, null), Epoch),
            new LocalVlmSample(LocalVlmClassification.TimedOutResult(TimeSpan.Zero), Epoch + TimeSpan.FromSeconds(1)),
            new LocalVlmSample(new LocalVlmClassification(BroadcastState.Program, 0.8, TimeSpan.Zero, false, null), Epoch + TimeSpan.FromSeconds(2)),
        };

        var result = observer.Detect("cam-1", samples);

        Assert.Equal(2, result.Count);
        Assert.Equal(BroadcastState.Commercial, result[0].Value);
        Assert.Equal(BroadcastState.Program, result[1].Value);
    }
}
