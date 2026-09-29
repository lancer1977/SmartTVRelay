using System.Diagnostics;
using Observation.Core;
using SmartTVRelay.Core;
using Xunit;

namespace SmartTVRelay.Core.Tests;

public sealed class LogoPresenceDetectorTests
{
    private const string LogoReferenceId = "wxyz-bug-top-right";

    [Fact]
    public void PresentLogoSequenceEmitsProgramLeaningObservations()
    {
        var detector = new LogoPresenceDetector(LogoReferenceId);
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new LogoMatchSample(0.85, now.AddMilliseconds(100)),
            new LogoMatchSample(0.95, now.AddMilliseconds(200)),
            new LogoMatchSample(1.0, now.AddMilliseconds(300)),
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Equal(3, observations.Count);
        for (int i = 0; i < observations.Count; i++)
        {
            var obs = observations[i];
            Assert.Equal(BroadcastState.Program, obs.Value);
            Assert.Equal("logo-presence", obs.SourceKind);
            Assert.InRange(obs.Confidence, 0.5, 0.95);
            Assert.Equal(BroadcastObservations.Subject, obs.Subject);
            Assert.Equal(BroadcastObservations.Predicate, obs.Predicate);
            Assert.Equal(ObservationScope.Of(sourceId), obs.Scope);
            Assert.Equal(samples[i].CapturedAt, obs.ObservedAt);
        }

        // Higher match score => higher confidence.
        Assert.True(observations[1].Confidence > observations[0].Confidence);
        Assert.Equal(0.95, observations[2].Confidence);
    }

    [Fact]
    public void AbsentLogoSequenceEmitsCommercialLeaningObservations()
    {
        var detector = new LogoPresenceDetector(LogoReferenceId);
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new LogoMatchSample(0.15, now.AddMilliseconds(100)),
            new LogoMatchSample(0.05, now.AddMilliseconds(200)),
            new LogoMatchSample(0.0, now.AddMilliseconds(300)),
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Equal(3, observations.Count);
        foreach (var obs in observations)
        {
            Assert.Equal(BroadcastState.Commercial, obs.Value);
            Assert.Equal("logo-presence", obs.SourceKind);
            Assert.InRange(obs.Confidence, 0.5, 0.95);
        }

        // Lower match score (further from having a logo) => higher confidence of absence.
        Assert.True(observations[1].Confidence > observations[0].Confidence);
        Assert.Equal(0.95, observations[2].Confidence);
    }

    [Fact]
    public void OccludedMidRangeScoresEmitNoObservations()
    {
        // Occlusion (something briefly covering the logo ROI) produces a mid-range match score
        // that is neither a confident presence nor a confident absence.
        var detector = new LogoPresenceDetector(LogoReferenceId);
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new LogoMatchSample(0.55, now.AddMilliseconds(100)),
            new LogoMatchSample(0.60, now.AddMilliseconds(200)),
            new LogoMatchSample(0.53, now.AddMilliseconds(300)),
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Empty(observations);
    }

    [Fact]
    public void NoisyMidRangeScoresEmitNoObservations()
    {
        // Noise (compression artifacts, transient lighting changes) jitters the match score around
        // the threshold without settling clearly either way.
        var detector = new LogoPresenceDetector(LogoReferenceId);
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new LogoMatchSample(0.52, now.AddMilliseconds(100)),
            new LogoMatchSample(0.78, now.AddMilliseconds(200)),
            new LogoMatchSample(0.51, now.AddMilliseconds(300)),
            new LogoMatchSample(0.79, now.AddMilliseconds(400)),
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Empty(observations);
    }

    [Fact]
    public void MixedSequenceProducesOnlyConfidentObservationsInOrder()
    {
        var detector = new LogoPresenceDetector(LogoReferenceId);
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new LogoMatchSample(0.90, now.AddMilliseconds(100)), // present
            new LogoMatchSample(0.60, now.AddMilliseconds(200)), // ambiguous
            new LogoMatchSample(0.10, now.AddMilliseconds(300)), // absent
            new LogoMatchSample(0.55, now.AddMilliseconds(400)), // ambiguous
            new LogoMatchSample(0.95, now.AddMilliseconds(500)), // present
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Equal(3, observations.Count);
        Assert.Equal(BroadcastState.Program, observations[0].Value);
        Assert.Equal(now.AddMilliseconds(100), observations[0].ObservedAt);
        Assert.Equal(BroadcastState.Commercial, observations[1].Value);
        Assert.Equal(now.AddMilliseconds(300), observations[1].ObservedAt);
        Assert.Equal(BroadcastState.Program, observations[2].Value);
        Assert.Equal(now.AddMilliseconds(500), observations[2].ObservedAt);
    }

    [Fact]
    public void BandEdgesAreInclusiveJustInsideIsAmbiguous()
    {
        // threshold 0.65, margin 0.15 -> ambiguous OPEN interval is (0.50, 0.80); the edges
        // themselves (0.50, 0.80) already qualify as confident (at minimum emitted confidence).
        var detector = new LogoPresenceDetector(LogoReferenceId);
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new LogoMatchSample(0.80, now.AddMilliseconds(100)), // exactly at upper edge -> present
            new LogoMatchSample(0.79, now.AddMilliseconds(200)), // just inside band -> ambiguous
            new LogoMatchSample(0.50, now.AddMilliseconds(300)), // exactly at lower edge -> absent
            new LogoMatchSample(0.51, now.AddMilliseconds(400)), // just inside band -> ambiguous
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Equal(2, observations.Count);
        Assert.Equal(BroadcastState.Program, observations[0].Value);
        Assert.Equal(now.AddMilliseconds(100), observations[0].ObservedAt);
        Assert.Equal(MinConfidenceForAssertions, observations[0].Confidence);
        Assert.Equal(BroadcastState.Commercial, observations[1].Value);
        Assert.Equal(now.AddMilliseconds(300), observations[1].ObservedAt);
        Assert.Equal(MinConfidenceForAssertions, observations[1].Confidence);
    }

    private const double MinConfidenceForAssertions = 0.5;

    [Fact]
    public void CustomThresholdAndMarginAreRespected()
    {
        var detector = new LogoPresenceDetector(LogoReferenceId, presenceThreshold: 0.5, ambiguityMargin: 0.05);
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new LogoMatchSample(0.56, now.AddMilliseconds(100)), // present under custom config
            new LogoMatchSample(0.44, now.AddMilliseconds(200)), // absent under custom config
            new LogoMatchSample(0.50, now.AddMilliseconds(300)), // ambiguous under custom config
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Equal(2, observations.Count);
        Assert.Equal(BroadcastState.Program, observations[0].Value);
        Assert.Equal(BroadcastState.Commercial, observations[1].Value);
    }

    [Fact]
    public void ConstructorStoresConfiguredIdentityAndThresholds()
    {
        var detector = new LogoPresenceDetector(LogoReferenceId, presenceThreshold: 0.7, ambiguityMargin: 0.1);

        Assert.Equal(LogoReferenceId, detector.LogoReferenceId);
        Assert.Equal(0.7, detector.PresenceThreshold);
        Assert.Equal(0.1, detector.AmbiguityMargin);
    }

    [Fact]
    public void ConstructorRejectsNullLogoReferenceId()
    {
        Assert.Throws<ArgumentNullException>(() => new LogoPresenceDetector(null!));
    }

    [Fact]
    public void ConstructorRejectsWhitespaceLogoReferenceId()
    {
        Assert.Throws<ArgumentException>(() => new LogoPresenceDetector("   "));
    }

    [Fact]
    public void ConstructorRejectsOutOfRangePresenceThreshold()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new LogoPresenceDetector(LogoReferenceId, presenceThreshold: 1.5));
        Assert.Equal("presenceThreshold", ex.ParamName);
    }

    [Fact]
    public void ConstructorRejectsNegativePresenceThreshold()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new LogoPresenceDetector(LogoReferenceId, presenceThreshold: -0.1));
        Assert.Equal("presenceThreshold", ex.ParamName);
    }

    [Fact]
    public void ConstructorRejectsNegativeAmbiguityMargin()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new LogoPresenceDetector(LogoReferenceId, ambiguityMargin: -0.01));
        Assert.Equal("ambiguityMargin", ex.ParamName);
    }

    [Fact]
    public void ConstructorRejectsNaNPresenceThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogoPresenceDetector(LogoReferenceId, presenceThreshold: double.NaN));
    }

    [Fact]
    public void DetectRejectsNullSourceId()
    {
        var detector = new LogoPresenceDetector(LogoReferenceId);
        var samples = new[] { new LogoMatchSample(0.9, DateTimeOffset.UtcNow) };

        Assert.Throws<ArgumentNullException>(() => detector.Detect(null!, samples));
    }

    [Fact]
    public void DetectRejectsEmptySourceId()
    {
        var detector = new LogoPresenceDetector(LogoReferenceId);
        var samples = new[] { new LogoMatchSample(0.9, DateTimeOffset.UtcNow) };

        Assert.Throws<ArgumentException>(() => detector.Detect(string.Empty, samples));
    }

    [Fact]
    public void DetectRejectsWhitespaceSourceId()
    {
        var detector = new LogoPresenceDetector(LogoReferenceId);
        var samples = new[] { new LogoMatchSample(0.9, DateTimeOffset.UtcNow) };

        Assert.Throws<ArgumentException>(() => detector.Detect("   ", samples));
    }

    [Fact]
    public void DetectRejectsNullSamples()
    {
        var detector = new LogoPresenceDetector(LogoReferenceId);

        Assert.Throws<ArgumentNullException>(() => detector.Detect("test-source", null!));
    }

    [Fact]
    public void DetectAcceptsEmptySamplesList()
    {
        var detector = new LogoPresenceDetector(LogoReferenceId);
        var samples = Array.Empty<LogoMatchSample>();

        var observations = detector.Detect("test-source", samples);

        Assert.Empty(observations);
    }

    [Fact]
    public void DetectCompletesWithinBoundedTimeForSmallSampleList()
    {
        // "Evaluation metrics captured" for a detector this size: assert the Detect() call stays
        // well within a generous bound for a small bounded sample list, rather than building a
        // separate metrics-reporting subsystem.
        var detector = new LogoPresenceDetector(LogoReferenceId);
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new List<LogoMatchSample>();
        for (int i = 0; i < 500; i++)
        {
            var score = (i % 10) / 10.0;
            samples.Add(new LogoMatchSample(score, now.AddMilliseconds(i)));
        }

        var stopwatch = Stopwatch.StartNew();
        var observations = detector.Detect(sourceId, samples);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Detect took {stopwatch.Elapsed}, expected under 1 second.");
        Assert.NotNull(observations);
    }
}
