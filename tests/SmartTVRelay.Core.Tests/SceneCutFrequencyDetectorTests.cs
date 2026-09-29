namespace SmartTVRelay.Core.Tests;

using System.Diagnostics;
using Observation.Core;
using Xunit;

public class SceneCutFrequencyDetectorTests
{
    private const string TestSourceId = "test-source";

    private static SceneCutSample[] CutsAtInterval(DateTimeOffset start, TimeSpan interval, int count)
    {
        var samples = new SceneCutSample[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = new SceneCutSample(start + (interval * i));
        }

        return samples;
    }

    [Fact]
    public void Detect_FastCutAds_ExceedsThresholdAndEmitsTransitionObservations()
    {
        // Arrange: a fast-cut ad break -- one cut every 400ms (~2.5 cuts/sec).
        // Default window=5s, threshold=8: the rolling count first reaches 9 (index 8, t=3.2s).
        var detector = new SceneCutFrequencyDetector();
        var now = DateTimeOffset.UtcNow;
        var samples = CutsAtInterval(now, TimeSpan.FromMilliseconds(400), 20);

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert: fires from index 8 onward (12 of the 20 samples), never before.
        Assert.Equal(12, observations.Count);
        Assert.Equal(samples[8].CapturedAt, observations[0].ObservedAt);
        foreach (var obs in observations)
        {
            Assert.Equal(BroadcastState.Transition, obs.Value);
            Assert.Equal("scene-cut", obs.SourceKind);
            Assert.Equal(0.45, obs.Confidence);
            Assert.Equal(BroadcastObservations.Subject, obs.Subject);
            Assert.Equal(BroadcastObservations.Predicate, obs.Predicate);
            Assert.Equal(ObservationScope.Of(TestSourceId), obs.Scope);
            Assert.NotEqual(Guid.Empty, obs.Id);
        }
    }

    [Fact]
    public void Detect_SlowProgramming_StaysWellBelowThresholdAndEmitsNothing()
    {
        // Arrange: ordinary dialogue-paced programming -- one cut every 3s.
        // Any 5s window holds at most 2 cuts, far below the threshold of 8.
        var detector = new SceneCutFrequencyDetector();
        var now = DateTimeOffset.UtcNow;
        var samples = CutsAtInterval(now, TimeSpan.FromSeconds(3), 6);

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Empty(observations);
    }

    [Fact]
    public void Detect_ActionSceneCadence_SubThresholdDespiteElevatedRate_EmitsNothing()
    {
        // Arrange: an action scene cutting every 650ms (~1.5 cuts/sec) -- genuinely elevated
        // versus slow programming, and a rate a careless/unwindowed implementation (or one with
        // too-low a threshold) might flag as a commercial. With window=5s, at most 8 cuts land
        // in any trailing window (floor(5000/650) + 1 = 8), which is exactly the threshold and
        // therefore does NOT exceed it -- proving the chosen threshold discriminates this from
        // the 400ms fast-cut-ad cadence above rather than merely separating "fast" from "slow".
        var detector = new SceneCutFrequencyDetector();
        var now = DateTimeOffset.UtcNow;
        var samples = CutsAtInterval(now, TimeSpan.FromMilliseconds(650), 15);

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Empty(observations);
    }

    [Fact]
    public void Detect_ExactlyAtThreshold_NotIncluded()
    {
        // Arrange: window=1s, threshold=2. Cuts at t=0, 0.5s, 1.0s.
        // At t=0.5s the rolling window [-.5s, 0.5s] holds 2 cuts -- exactly the threshold, excluded.
        var detector = new SceneCutFrequencyDetector(TimeSpan.FromSeconds(1), 2);
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new SceneCutSample(now),
            new SceneCutSample(now.AddMilliseconds(500)),
            new SceneCutSample(now.AddSeconds(1)),
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert: only t=1.0s (3rd cut, rolling count 3 > 2) fires.
        var obs = Assert.Single(observations);
        Assert.Equal(samples[2].CapturedAt, obs.ObservedAt);
    }

    [Fact]
    public void Detect_OldCutsAgeOutOfRollingWindow_DoesNotAccumulateAcrossGap()
    {
        // Arrange: window=1s, threshold=1. Two cuts close together trip it, then a gap
        // longer than the window means the next cut starts counting from zero again.
        var detector = new SceneCutFrequencyDetector(TimeSpan.FromSeconds(1), 1);
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new SceneCutSample(now),                        // count=1, not > 1
            new SceneCutSample(now.AddMilliseconds(500)),    // count=2, > 1 -> fires
            new SceneCutSample(now.AddSeconds(2)),           // gap ages out both prior cuts; count=1, not > 1
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        var obs = Assert.Single(observations);
        Assert.Equal(samples[1].CapturedAt, obs.ObservedAt);
    }

    [Fact]
    public void Detect_Latency_CompletesWithinGenerousBound()
    {
        // Arrange: a modest bounded sample list.
        var detector = new SceneCutFrequencyDetector();
        var now = DateTimeOffset.UtcNow;
        var samples = CutsAtInterval(now, TimeSpan.FromMilliseconds(10), 1000);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var observations = detector.Detect(TestSourceId, samples);
        stopwatch.Stop();

        // Assert
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Detect took {stopwatch.Elapsed}, expected under 1 second.");
        Assert.NotEmpty(observations);
    }

    [Fact]
    public void Constructor_ZeroWindow_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneCutFrequencyDetector(TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_NegativeWindow_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneCutFrequencyDetector(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Constructor_ZeroThreshold_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneCutFrequencyDetector(cutCountThreshold: 0));
    }

    [Fact]
    public void Constructor_NegativeThreshold_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneCutFrequencyDetector(cutCountThreshold: -1));
    }

    [Fact]
    public void Constructor_CustomWindowAndThreshold_Respected()
    {
        // Arrange: threshold=3 within a 2s window; 4 cuts 500ms apart all land in one window.
        var detector = new SceneCutFrequencyDetector(TimeSpan.FromSeconds(2), 3);
        var now = DateTimeOffset.UtcNow;
        var samples = CutsAtInterval(now, TimeSpan.FromMilliseconds(500), 4);

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert: rolling count reaches 4 (> 3) only at the last sample.
        var obs = Assert.Single(observations);
        Assert.Equal(samples[3].CapturedAt, obs.ObservedAt);
    }

    [Fact]
    public void Detect_NullSourceId_ThrowsArgumentNullException()
    {
        var detector = new SceneCutFrequencyDetector();
        var samples = new[] { new SceneCutSample(DateTimeOffset.UtcNow) };

        Assert.Throws<ArgumentNullException>(() => detector.Detect(null!, samples));
    }

    [Fact]
    public void Detect_EmptySourceId_ThrowsArgumentException()
    {
        var detector = new SceneCutFrequencyDetector();
        var samples = new[] { new SceneCutSample(DateTimeOffset.UtcNow) };

        Assert.Throws<ArgumentException>(() => detector.Detect(string.Empty, samples));
    }

    [Fact]
    public void Detect_WhitespaceSourceId_ThrowsArgumentException()
    {
        var detector = new SceneCutFrequencyDetector();
        var samples = new[] { new SceneCutSample(DateTimeOffset.UtcNow) };

        Assert.Throws<ArgumentException>(() => detector.Detect("   ", samples));
    }

    [Fact]
    public void Detect_NullSamples_ThrowsArgumentNullException()
    {
        var detector = new SceneCutFrequencyDetector();

        Assert.Throws<ArgumentNullException>(() => detector.Detect(TestSourceId, null!));
    }

    [Fact]
    public void Detect_EmptySamplesList_ReturnsEmptyList()
    {
        var detector = new SceneCutFrequencyDetector();

        var observations = detector.Detect(TestSourceId, Array.Empty<SceneCutSample>());

        Assert.Empty(observations);
    }
}
