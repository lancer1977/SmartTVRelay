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
}
