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

    // --- Silence / dead-air detection (#74) ---

    [Fact]
    public void SustainedNearSilenceEmitsDistinctDeadAirSignal()
    {
        var detector = new AudioLoudnessDetector();
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new AudioLoudnessSample(0.0, now, -20.0),                        // normal program level
            new AudioLoudnessSample(-4.0, now.AddMilliseconds(100), -50.0),  // drops into near-silence; run starts
            new AudioLoudnessSample(0.0, now.AddMilliseconds(200), -50.0),   // still silent; 100ms elapsed, not yet 250ms
            new AudioLoudnessSample(0.0, now.AddMilliseconds(300), -50.0),   // still silent; 200ms elapsed, not yet
            new AudioLoudnessSample(0.0, now.AddMilliseconds(400), -50.0),   // still silent; 300ms elapsed >= 250ms -> reported here
        };

        var observations = detector.Detect(sourceId, samples);

        var obs = Assert.Single(observations);
        Assert.Equal(BroadcastState.Unknown, obs.Value);
        Assert.Equal("audio-loudness", obs.SourceKind);
        Assert.InRange(obs.Confidence, 0.0, 1.0);
        Assert.Equal(now.AddMilliseconds(400), obs.ObservedAt);
    }

    [Fact]
    public void SilenceIsNeverReportedAsCommercial()
    {
        var detector = new AudioLoudnessDetector();
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new AudioLoudnessSample(0.0, now, -20.0),
            new AudioLoudnessSample(-4.0, now.AddMilliseconds(100), -50.0),
            new AudioLoudnessSample(0.0, now.AddMilliseconds(400), -50.0),
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.NotEmpty(observations);
        Assert.All(observations, o => Assert.NotEqual(BroadcastState.Commercial, o.Value));
    }

    [Fact]
    public void BriefDipBelowSilenceThresholdDoesNotEmitDeadAir()
    {
        var detector = new AudioLoudnessDetector();
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new AudioLoudnessSample(0.0, now, -20.0),
            new AudioLoudnessSample(-4.0, now.AddMilliseconds(100), -50.0),  // brief dip below silence threshold
            new AudioLoudnessSample(4.0, now.AddMilliseconds(150), -20.0),   // recovers after only 50ms
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.DoesNotContain(observations, o => o.Value == BroadcastState.Unknown);
    }

    [Fact]
    public void SamplesWithDefaultAbsoluteLevelNeverTriggerSilence()
    {
        // Legacy/compatibility guard: callers that never populate AbsoluteLevelDb (all default to 0.0, a
        // normal/loud level) must never see a dead-air signal, no matter how long the sequence runs.
        var detector = new AudioLoudnessDetector();
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 20)
            .Select(i => new AudioLoudnessSample(0.0, now.AddMilliseconds(i * 100)))
            .ToArray();

        var observations = detector.Detect(sourceId, samples);

        Assert.Empty(observations);
    }

    // --- Music/action false-positive guard (#74) ---

    [Fact]
    public void OscillatingMusicOrActionPassageDoesNotFalsePositiveAsTransition()
    {
        // Every delta below is >= the 6.0 dB default threshold, so a naive threshold-only detector (the original
        // #60 logic in isolation) would flag all five as transitions. None of them actually hold at their new
        // level for the 500ms sustain window, though -- the level keeps swinging back, which is the shape of a
        // loud music swell / action-sequence hit rather than a stable commercial-boundary loudness change.
        var detector = new AudioLoudnessDetector();
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new AudioLoudnessSample(0.0, now, -20.0),                        // quiet dialogue baseline
            new AudioLoudnessSample(15.0, now.AddMilliseconds(100), -5.0),   // action hit / music swell (naive: flag)
            new AudioLoudnessSample(-13.0, now.AddMilliseconds(200), -18.0), // swings back near baseline (naive: flag)
            new AudioLoudnessSample(14.0, now.AddMilliseconds(300), -4.0),   // swells again (naive: flag)
            new AudioLoudnessSample(-15.0, now.AddMilliseconds(400), -19.0), // drops back (naive: flag)
            new AudioLoudnessSample(13.0, now.AddMilliseconds(500), -6.0),   // swells again (naive: flag)
            new AudioLoudnessSample(-2.0, now.AddMilliseconds(600), -8.0),   // settling, below threshold
            new AudioLoudnessSample(-2.0, now.AddMilliseconds(700), -10.0),  // settling, below threshold
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Empty(observations);
    }

    [Fact]
    public void LoudnessJumpThatHoldsStillEmitsTransition()
    {
        // Positive control for the guard above: a jump that genuinely holds at its new level must still be
        // reported. The guard suppresses transient swings, not every large delta.
        var detector = new AudioLoudnessDetector();
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new AudioLoudnessSample(0.0, now, -24.0),                       // typical program loudness
            new AudioLoudnessSample(18.0, now.AddMilliseconds(100), -6.0),  // jump into a sustained loud passage
            new AudioLoudnessSample(0.0, now.AddMilliseconds(200), -6.0),   // holds
            new AudioLoudnessSample(0.0, now.AddMilliseconds(300), -6.0),   // holds
            new AudioLoudnessSample(0.0, now.AddMilliseconds(400), -6.0),   // holds
        };

        var observations = detector.Detect(sourceId, samples);

        var obs = Assert.Single(observations);
        Assert.Equal(BroadcastState.Transition, obs.Value);
        Assert.Equal(now.AddMilliseconds(100), obs.ObservedAt);
    }

    [Fact]
    public void SamplesWithDefaultAbsoluteLevelAlwaysTreatedAsSustained()
    {
        // Legacy/compatibility guard: when AbsoluteLevelDb is never populated (defaults to a constant 0.0 for
        // every sample), the sustain check can never disprove a hold, so pre-existing delta-only behavior is
        // completely unaffected by the new guard.
        var detector = new AudioLoudnessDetector();
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new AudioLoudnessSample(7.0, now.AddMilliseconds(100)),
            new AudioLoudnessSample(-9.0, now.AddMilliseconds(150)),
            new AudioLoudnessSample(8.0, now.AddMilliseconds(200)),
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Equal(3, observations.Count);
        Assert.All(observations, o => Assert.Equal(BroadcastState.Transition, o.Value));
    }

    // --- New constructor parameter validation (#74) ---

    [Fact]
    public void ConstructorRejectsNaNSilenceThresholdDb()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AudioLoudnessDetector(silenceThresholdDb: double.NaN));
        Assert.Equal("silenceThresholdDb", ex.ParamName);
    }

    [Fact]
    public void ConstructorRejectsNegativeSilenceMinDurationMilliseconds()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AudioLoudnessDetector(silenceMinDurationMilliseconds: -1.0));
        Assert.Equal("silenceMinDurationMilliseconds", ex.ParamName);
    }

    [Fact]
    public void ConstructorRejectsNegativeSustainedToleranceDb()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AudioLoudnessDetector(sustainedToleranceDb: -1.0));
        Assert.Equal("sustainedToleranceDb", ex.ParamName);
    }

    [Fact]
    public void ConstructorRejectsNegativeSustainedDurationMilliseconds()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AudioLoudnessDetector(sustainedDurationMilliseconds: -1.0));
        Assert.Equal("sustainedDurationMilliseconds", ex.ParamName);
    }

    [Fact]
    public void ConstructorRejectsInfiniteSustainedDurationMilliseconds()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AudioLoudnessDetector(sustainedDurationMilliseconds: double.PositiveInfinity));
        Assert.Equal("sustainedDurationMilliseconds", ex.ParamName);
    }
}
