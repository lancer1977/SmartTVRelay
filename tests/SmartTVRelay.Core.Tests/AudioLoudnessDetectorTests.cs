using Observation.Core;
using SmartTVRelay.Core;
using Xunit;

namespace SmartTVRelay.Core.Tests;

public sealed class AudioLoudnessDetectorTests
{
    [Fact]
    public void ClearLoudnessJumpSequenceEmitsObservations()
    {
        var detector = new AudioLoudnessDetector();
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new AudioLoudnessSample(6.0, now.AddMilliseconds(100)),
            new AudioLoudnessSample(-8.0, now.AddMilliseconds(200)),
            new AudioLoudnessSample(12.5, now.AddMilliseconds(300)),
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Equal(3, observations.Count);
        for (int i = 0; i < observations.Count; i++)
        {
            var obs = observations[i];
            Assert.Equal(BroadcastState.Transition, obs.Value);
            Assert.Equal("audio-loudness", obs.SourceKind);
            Assert.Equal(0.55, obs.Confidence);
            Assert.Equal(BroadcastObservations.Subject, obs.Subject);
            Assert.Equal(BroadcastObservations.Predicate, obs.Predicate);
            Assert.Equal(ObservationScope.Of(sourceId), obs.Scope);
            Assert.Equal(samples[i].CapturedAt, obs.ObservedAt);
        }
    }

    [Fact]
    public void StableLoudnessSequenceBelowThresholdEmitsNothing()
    {
        var detector = new AudioLoudnessDetector();
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new AudioLoudnessSample(1.0, now.AddMilliseconds(100)),
            new AudioLoudnessSample(-2.0, now.AddMilliseconds(200)),
            new AudioLoudnessSample(0.5, now.AddMilliseconds(300)),
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Empty(observations);
    }

    [Fact]
    public void BorderlineAtThresholdIsIncludedJustBelowIsExcluded()
    {
        var detector = new AudioLoudnessDetector();
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new AudioLoudnessSample(6.0, now.AddMilliseconds(100)),
            new AudioLoudnessSample(5.99, now.AddMilliseconds(200)),
        };

        var observations = detector.Detect(sourceId, samples);

        var obs = Assert.Single(observations);
        Assert.Equal(BroadcastState.Transition, obs.Value);
        Assert.Equal(now.AddMilliseconds(100), obs.ObservedAt);
    }

    [Fact]
    public void MixedSequenceWithInterleavedSamplesProducesObservationsInOrder()
    {
        var detector = new AudioLoudnessDetector();
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new AudioLoudnessSample(7.0, now.AddMilliseconds(100)),     // included
            new AudioLoudnessSample(1.5, now.AddMilliseconds(200)),     // excluded
            new AudioLoudnessSample(2.0, now.AddMilliseconds(300)),     // excluded
            new AudioLoudnessSample(-6.5, now.AddMilliseconds(400)),    // included (magnitude >= 6.0)
            new AudioLoudnessSample(0.3, now.AddMilliseconds(500)),     // excluded
            new AudioLoudnessSample(10.0, now.AddMilliseconds(600)),    // included
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Equal(3, observations.Count);
        Assert.Equal(now.AddMilliseconds(100), observations[0].ObservedAt);
        Assert.Equal(now.AddMilliseconds(400), observations[1].ObservedAt);
        Assert.Equal(now.AddMilliseconds(600), observations[2].ObservedAt);
    }

    [Fact]
    public void ConstructorRejectsNegativeThreshold()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AudioLoudnessDetector(-1.0));
        Assert.Equal("deltaThresholdDb", ex.ParamName);
    }

    [Fact]
    public void ConstructorRejectsNaN()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AudioLoudnessDetector(double.NaN));
        Assert.Equal("deltaThresholdDb", ex.ParamName);
    }

    [Fact]
    public void ConstructorRejectsPositiveInfinity()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AudioLoudnessDetector(double.PositiveInfinity));
        Assert.Equal("deltaThresholdDb", ex.ParamName);
    }

    [Fact]
    public void DetectRejectsNullSourceId()
    {
        var detector = new AudioLoudnessDetector();
        var samples = new[] { new AudioLoudnessSample(10.0, DateTimeOffset.UtcNow) };

        Assert.Throws<ArgumentNullException>(() => detector.Detect(null!, samples));
    }

    [Fact]
    public void DetectRejectsEmptySourceId()
    {
        var detector = new AudioLoudnessDetector();
        var samples = new[] { new AudioLoudnessSample(10.0, DateTimeOffset.UtcNow) };

        Assert.Throws<ArgumentException>(() => detector.Detect(string.Empty, samples));
    }

    [Fact]
    public void DetectRejectsWhitespaceSourceId()
    {
        var detector = new AudioLoudnessDetector();
        var samples = new[] { new AudioLoudnessSample(10.0, DateTimeOffset.UtcNow) };

        Assert.Throws<ArgumentException>(() => detector.Detect("   ", samples));
    }

    [Fact]
    public void DetectRejectsNullSamples()
    {
        var detector = new AudioLoudnessDetector();

        Assert.Throws<ArgumentNullException>(() => detector.Detect("test-source", null!));
    }

    [Fact]
    public void DetectAcceptsEmptySamplesList()
    {
        var detector = new AudioLoudnessDetector();
        var samples = Array.Empty<AudioLoudnessSample>();

        var observations = detector.Detect("test-source", samples);

        Assert.Empty(observations);
    }

    [Fact]
    public void CustomThresholdIsRespected()
    {
        var detector = new AudioLoudnessDetector(10.0);
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new AudioLoudnessSample(8.0, now.AddMilliseconds(100)),    // below custom threshold
            new AudioLoudnessSample(10.0, now.AddMilliseconds(200)),   // at custom threshold
            new AudioLoudnessSample(11.0, now.AddMilliseconds(300)),   // above custom threshold
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Equal(2, observations.Count);
        Assert.Equal(now.AddMilliseconds(200), observations[0].ObservedAt);
        Assert.Equal(now.AddMilliseconds(300), observations[1].ObservedAt);
    }
}
