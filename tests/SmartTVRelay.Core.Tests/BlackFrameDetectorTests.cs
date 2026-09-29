namespace SmartTVRelay.Core.Tests;

using Observation.Core;
using Xunit;

public class BlackFrameDetectorTests
{
    private const string TestSourceId = "test-source";

    [Fact]
    public void Detect_ClearBlackFrameSequence_EmitsTransitionObservation()
    {
        // Arrange
        var detector = new BlackFrameDetector();
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.0, now.AddMilliseconds(0)),
            new FrameLuminanceSample(0.03, now.AddMilliseconds(33)),
            new FrameLuminanceSample(0.02, now.AddMilliseconds(66)),
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Equal(3, observations.Count);
        foreach (var (obs, sample) in observations.Zip(samples))
        {
            Assert.Equal(BroadcastState.Transition, obs.Value);
            Assert.Equal("black-frame", obs.SourceKind);
            Assert.Equal(0.6, obs.Confidence);
            Assert.Equal(BroadcastObservations.Subject, obs.Subject);
            Assert.Equal(BroadcastObservations.Predicate, obs.Predicate);
            Assert.Equal(sample.CapturedAt, obs.ObservedAt);
            Assert.NotEqual(Guid.Empty, obs.Id);
        }
    }

    [Fact]
    public void Detect_NormalFrameSequence_ReturnsEmptyList()
    {
        // Arrange
        var detector = new BlackFrameDetector();
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.5, now.AddMilliseconds(0)),
            new FrameLuminanceSample(0.9, now.AddMilliseconds(33)),
            new FrameLuminanceSample(0.7, now.AddMilliseconds(66)),
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Empty(observations);
    }

    [Fact]
    public void Detect_ExactlyAtThreshold_Included()
    {
        // Arrange
        var detector = new BlackFrameDetector();
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.05, now.AddMilliseconds(0)), // exactly at threshold
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Single(observations);
        Assert.Equal(BroadcastState.Transition, observations[0].Value);
    }

    [Fact]
    public void Detect_JustAboveThreshold_Excluded()
    {
        // Arrange
        var detector = new BlackFrameDetector();
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.0501, now.AddMilliseconds(0)), // just above threshold
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Empty(observations);
    }

    [Fact]
    public void Detect_MixedSequence_OnlyBlackFramesProduceObservations()
    {
        // Arrange
        var detector = new BlackFrameDetector();
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.5, now.AddMilliseconds(0)),     // normal
            new FrameLuminanceSample(0.03, now.AddMilliseconds(33)),   // black
            new FrameLuminanceSample(0.7, now.AddMilliseconds(66)),    // normal
            new FrameLuminanceSample(0.01, now.AddMilliseconds(99)),   // black
            new FrameLuminanceSample(0.8, now.AddMilliseconds(132)),   // normal
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Equal(2, observations.Count);
        Assert.Equal(0.03, samples[1].AverageLuminance);
        Assert.Equal(0.01, samples[3].AverageLuminance);
        Assert.Equal(samples[1].CapturedAt, observations[0].ObservedAt);
        Assert.Equal(samples[3].CapturedAt, observations[1].ObservedAt);
    }

    [Fact]
    public void Constructor_NegativeThreshold_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackFrameDetector(-0.1));
    }

    [Fact]
    public void Constructor_ThresholdGreaterThanOne_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackFrameDetector(1.1));
    }

    [Fact]
    public void Constructor_NaNThreshold_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackFrameDetector(double.NaN));
    }

    [Fact]
    public void Constructor_PositiveInfinityThreshold_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlackFrameDetector(double.PositiveInfinity));
    }

    [Fact]
    public void Detect_NullSourceId_ThrowsArgumentNullException()
    {
        // Arrange
        var detector = new BlackFrameDetector();
        var samples = new[] { new FrameLuminanceSample(0.5, DateTimeOffset.UtcNow) };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => detector.Detect(null!, samples));
    }

    [Fact]
    public void Detect_EmptySourceId_ThrowsArgumentException()
    {
        // Arrange
        var detector = new BlackFrameDetector();
        var samples = new[] { new FrameLuminanceSample(0.5, DateTimeOffset.UtcNow) };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => detector.Detect(string.Empty, samples));
    }

    [Fact]
    public void Detect_WhitespaceSourceId_ThrowsArgumentException()
    {
        // Arrange
        var detector = new BlackFrameDetector();
        var samples = new[] { new FrameLuminanceSample(0.5, DateTimeOffset.UtcNow) };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => detector.Detect("   ", samples));
    }

    [Fact]
    public void Detect_NullSamples_ThrowsArgumentNullException()
    {
        // Arrange
        var detector = new BlackFrameDetector();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => detector.Detect(TestSourceId, null!));
    }

    [Fact]
    public void Detect_EmptySamplesList_ReturnsEmptyList()
    {
        // Arrange
        var detector = new BlackFrameDetector();
        var samples = new List<FrameLuminanceSample>();

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Empty(observations);
    }

    [Fact]
    public void Detect_CustomThreshold_AppliedCorrectly()
    {
        // Arrange
        var customThreshold = 0.2;
        var detector = new BlackFrameDetector(customThreshold);
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.1, now.AddMilliseconds(0)),   // below custom threshold
            new FrameLuminanceSample(0.2, now.AddMilliseconds(33)),  // at custom threshold
            new FrameLuminanceSample(0.3, now.AddMilliseconds(66)),  // above custom threshold
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Equal(2, observations.Count);
        Assert.Equal(samples[0].CapturedAt, observations[0].ObservedAt);
        Assert.Equal(samples[1].CapturedAt, observations[1].ObservedAt);
    }

    // --- #73: minimum transition duration ---

    [Fact]
    public void Constructor_NegativeMinimumTransitionDuration_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BlackFrameDetector(minimumTransitionDuration: TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Detect_NoMinimumDurationConfigured_SingleFrameFlashStillFlagged()
    {
        // Arrange: default detector (no minimum duration) preserves #59 behavior of flagging a
        // single qualifying frame on its own.
        var detector = new BlackFrameDetector();
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.6, now.AddMilliseconds(0)),
            new FrameLuminanceSample(0.0, now.AddMilliseconds(33)),  // isolated single-frame flash
            new FrameLuminanceSample(0.6, now.AddMilliseconds(66)),
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Single(observations);
        Assert.Equal(samples[1].CapturedAt, observations[0].ObservedAt);
    }

    [Fact]
    public void Detect_SceneCutFlashShorterThanMinimumDuration_IsSuppressed()
    {
        // Arrange: a single black frame surrounded by normal frames -- a scene-cut flash -- must
        // not register as a transition once a minimum duration is required.
        var detector = new BlackFrameDetector(minimumTransitionDuration: TimeSpan.FromMilliseconds(100));
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.6, now.AddMilliseconds(0)),
            new FrameLuminanceSample(0.0, now.AddMilliseconds(33)),  // single-frame flash: 0ms span
            new FrameLuminanceSample(0.6, now.AddMilliseconds(66)),
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Empty(observations);
    }

    [Fact]
    public void Detect_RunShorterThanMinimumDuration_IsSuppressed()
    {
        // Arrange: a short black run (33ms span) that does not reach a 100ms minimum.
        var detector = new BlackFrameDetector(minimumTransitionDuration: TimeSpan.FromMilliseconds(100));
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.6, now.AddMilliseconds(0)),
            new FrameLuminanceSample(0.0, now.AddMilliseconds(33)),
            new FrameLuminanceSample(0.0, now.AddMilliseconds(66)),
            new FrameLuminanceSample(0.6, now.AddMilliseconds(99)),
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Empty(observations);
    }

    [Fact]
    public void Detect_RunAtLeastMinimumDuration_EmitsEveryFrameInRun()
    {
        // Arrange: a black run spanning exactly the configured minimum duration must still be
        // flagged, and every qualifying frame in the run gets its own observation (unchanged
        // per-frame emission shape from #59).
        var detector = new BlackFrameDetector(minimumTransitionDuration: TimeSpan.FromMilliseconds(90));
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.6, now.AddMilliseconds(0)),
            new FrameLuminanceSample(0.0, now.AddMilliseconds(33)),
            new FrameLuminanceSample(0.0, now.AddMilliseconds(66)),
            new FrameLuminanceSample(0.0, now.AddMilliseconds(99)),
            new FrameLuminanceSample(0.0, now.AddMilliseconds(132)), // run span 33..132 = 99ms >= 90ms
            new FrameLuminanceSample(0.6, now.AddMilliseconds(165)),
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Equal(4, observations.Count);
        Assert.Equal(samples[1].CapturedAt, observations[0].ObservedAt);
        Assert.Equal(samples[2].CapturedAt, observations[1].ObservedAt);
        Assert.Equal(samples[3].CapturedAt, observations[2].ObservedAt);
        Assert.Equal(samples[4].CapturedAt, observations[3].ObservedAt);
        Assert.All(observations, obs => Assert.Equal(BroadcastState.Transition, obs.Value));
    }

    [Fact]
    public void Detect_DarkSceneTrap_BriefDipBelowThresholdIsSuppressedByMinimumDuration()
    {
        // Arrange: a genuinely dark (but not black) scene in real programming occasionally dips
        // for a frame or two below the black-luminance threshold -- a naive per-frame check would
        // misfire here exactly like a scene-cut flash. With a minimum duration configured, the
        // brief dip (33ms span) is correctly rejected while the surrounding dark-but-not-black
        // frames (above threshold) were never candidates in the first place.
        var detector = new BlackFrameDetector(luminanceThreshold: 0.05, minimumTransitionDuration: TimeSpan.FromMilliseconds(200));
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.12, now.AddMilliseconds(0)),   // dark scene, above threshold
            new FrameLuminanceSample(0.09, now.AddMilliseconds(33)),  // dark scene, above threshold
            new FrameLuminanceSample(0.02, now.AddMilliseconds(66)),  // brief dip below threshold
            new FrameLuminanceSample(0.03, now.AddMilliseconds(99)),  // brief dip below threshold
            new FrameLuminanceSample(0.10, now.AddMilliseconds(132)), // dark scene resumes
            new FrameLuminanceSample(0.11, now.AddMilliseconds(165)), // dark scene, above threshold
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Empty(observations);
    }

    // --- #73: letterboxing / pillarboxing robustness ---

    [Fact]
    public void Detect_PersistentLetterboxing_DoesNotEmitTransition()
    {
        // Arrange: full-frame average luminance is low throughout because of black letterbox
        // bars, but the content area (the actual picture) stays at normal program luminance the
        // whole time. This is a long, continuous run under the full-frame average alone -- a
        // duration threshold by itself cannot tell it apart from a real long transition, which is
        // exactly why this needs the content-area signal rather than timing. Because the content
        // area is supplied and stays bright, none of these frames qualify as black at all.
        var detector = new BlackFrameDetector(minimumTransitionDuration: TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 50)
            .Select(i => new FrameLuminanceSample(
                AverageLuminance: 0.03,           // low: bars dominate the full-frame average
                CapturedAt: now.AddMilliseconds(i * 33),
                ContentAreaLuminance: 0.55))       // normal: the picture itself is not black
            .ToArray();

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Empty(observations);
    }

    [Fact]
    public void Detect_ContentAreaLuminanceOmitted_FallsBackToAverageLuminance()
    {
        // Arrange: when no content-area reading is supplied, behavior is identical to #59 --
        // full-frame average alone decides. This is the explicit compatibility fallback.
        var detector = new BlackFrameDetector();
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.02, now.AddMilliseconds(0)), // no ContentAreaLuminance supplied
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Single(observations);
    }

    [Fact]
    public void Detect_ContentAreaAlsoBlack_StillEmitsTransition_NotJustBars()
    {
        // Arrange: when the content area itself genuinely goes black too (a real transition, not
        // just letterbox bars), it must still be flagged even though a content-area reading is
        // present.
        var detector = new BlackFrameDetector(minimumTransitionDuration: TimeSpan.FromMilliseconds(30));
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.55, now.AddMilliseconds(0), ContentAreaLuminance: 0.55),
            new FrameLuminanceSample(0.01, now.AddMilliseconds(33), ContentAreaLuminance: 0.01),
            new FrameLuminanceSample(0.01, now.AddMilliseconds(66), ContentAreaLuminance: 0.01),
            new FrameLuminanceSample(0.55, now.AddMilliseconds(99), ContentAreaLuminance: 0.55),
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Equal(2, observations.Count);
        Assert.Equal(samples[1].CapturedAt, observations[0].ObservedAt);
        Assert.Equal(samples[2].CapturedAt, observations[1].ObservedAt);
    }

    [Fact]
    public void Detect_LetterboxingWithBriefRealTransitionInside_FlagsOnlyTheRealTransition()
    {
        // Arrange: letterboxed content (content area bright) plays for a while, then a real
        // black-frame transition occurs (content area also goes black) for long enough to clear
        // the minimum duration, then letterboxed content resumes. Only the genuine transition
        // frames should be flagged.
        var detector = new BlackFrameDetector(minimumTransitionDuration: TimeSpan.FromMilliseconds(30));
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new FrameLuminanceSample(0.03, now.AddMilliseconds(0), ContentAreaLuminance: 0.5),   // letterboxed
            new FrameLuminanceSample(0.03, now.AddMilliseconds(33), ContentAreaLuminance: 0.5),  // letterboxed
            new FrameLuminanceSample(0.0, now.AddMilliseconds(66), ContentAreaLuminance: 0.0),   // real transition
            new FrameLuminanceSample(0.0, now.AddMilliseconds(99), ContentAreaLuminance: 0.0),   // real transition
            new FrameLuminanceSample(0.03, now.AddMilliseconds(132), ContentAreaLuminance: 0.5), // letterboxed resumes
        };

        // Act
        var observations = detector.Detect(TestSourceId, samples);

        // Assert
        Assert.Equal(2, observations.Count);
        Assert.Equal(samples[2].CapturedAt, observations[0].ObservedAt);
        Assert.Equal(samples[3].CapturedAt, observations[1].ObservedAt);
    }
}
