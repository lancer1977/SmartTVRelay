namespace SmartTVRelay.Core.Tests;

using Observation.Core;
using Xunit;

public class ExplicitMarkerDetectorTests
{
    private readonly ExplicitMarkerDetector _detector = new();
    private readonly string _sourceId = "test-source";

    [Fact]
    public void Detect_WithCueOutMarker_ReturnsObservationWithCommercialState()
    {
        // Arrange
        var observedAt = DateTimeOffset.UtcNow;
        var signal = new ExplicitMarkerSignal(ExplicitMarkerKind.CueOut, observedAt);

        // Act
        var observations = _detector.Detect(_sourceId, signal);

        // Assert
        Assert.NotEmpty(observations);
        Assert.Single(observations);
        var observation = observations[0];
        Assert.Equal(BroadcastState.Commercial, observation.Value);
        Assert.Equal("explicit-marker", observation.SourceKind);
        Assert.Equal(0.98, observation.Confidence);
        Assert.Equal(ObservationScope.Of(_sourceId), observation.Scope);
        Assert.Equal(BroadcastObservations.Subject, observation.Subject);
        Assert.Equal(BroadcastObservations.Predicate, observation.Predicate);
        Assert.Equal(observedAt, observation.ObservedAt);
    }

    [Fact]
    public void Detect_WithCueInMarker_ReturnsObservationWithProgramState()
    {
        // Arrange
        var observedAt = DateTimeOffset.UtcNow;
        var signal = new ExplicitMarkerSignal(ExplicitMarkerKind.CueIn, observedAt);

        // Act
        var observations = _detector.Detect(_sourceId, signal);

        // Assert
        Assert.NotEmpty(observations);
        Assert.Single(observations);
        var observation = observations[0];
        Assert.Equal(BroadcastState.Program, observation.Value);
        Assert.Equal("explicit-marker", observation.SourceKind);
        Assert.Equal(0.98, observation.Confidence);
        Assert.Equal(ObservationScope.Of(_sourceId), observation.Scope);
        Assert.Equal(BroadcastObservations.Subject, observation.Subject);
        Assert.Equal(BroadcastObservations.Predicate, observation.Predicate);
        Assert.Equal(observedAt, observation.ObservedAt);
    }

    [Fact]
    public void Detect_WithNoneMarker_ReturnsEmptyList()
    {
        // Arrange
        var signal = new ExplicitMarkerSignal(ExplicitMarkerKind.None, DateTimeOffset.UtcNow);

        // Act
        var observations = _detector.Detect(_sourceId, signal);

        // Assert
        Assert.Empty(observations);
    }

    [Fact]
    public void Detect_WithUnrecognizedEnumValue_ReturnsEmptyList()
    {
        // Arrange
        var unrecognizedKind = (ExplicitMarkerKind)99;
        var signal = new ExplicitMarkerSignal(unrecognizedKind, DateTimeOffset.UtcNow);

        // Act
        var observations = _detector.Detect(_sourceId, signal);

        // Assert
        Assert.Empty(observations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Detect_WithInvalidSourceId_ThrowsArgumentException(string? sourceId)
    {
        // Arrange
        var signal = new ExplicitMarkerSignal(ExplicitMarkerKind.CueOut, DateTimeOffset.UtcNow);

        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => _detector.Detect(sourceId!, signal));
    }

    [Fact]
    public void Detect_EachCallGeneratesUniqueObservationId()
    {
        // Arrange
        var signal = new ExplicitMarkerSignal(ExplicitMarkerKind.CueOut, DateTimeOffset.UtcNow);

        // Act
        var observations1 = _detector.Detect(_sourceId, signal);
        var observations2 = _detector.Detect(_sourceId, signal);

        // Assert
        Assert.Single(observations1);
        Assert.Single(observations2);
        Assert.NotEqual(observations1[0].Id, observations2[0].Id);
    }
}
