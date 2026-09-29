namespace SmartTVRelay.Core.Tests;

using System.Diagnostics;
using Observation.Core;
using SmartTVRelay.Core.Ingest;
using Xunit;

public sealed class CaptionOcrDetectorTests
{
    private readonly CaptionOcrDetector _detector = new();
    private const string SourceId = "test-source";

    // ---- Positive phrases ----

    [Fact]
    public void Detect_WithSingleCommercialPhrase_EmitsCommercialObservationWithMatchedFeature()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new CaptionSample("Call now, operators are standing by!", observedAt, observedAt.AddSeconds(2)),
        };

        var results = _detector.Detect(SourceId, samples);

        var result = Assert.Single(results);
        Assert.Equal(BroadcastState.Commercial, result.Observation.Value);
        Assert.Equal("caption-ocr", result.Observation.SourceKind);
        Assert.Equal(SourceId, result.Observation.SourceId);
        Assert.Equal(BroadcastObservations.Subject, result.Observation.Subject);
        Assert.Equal(BroadcastObservations.Predicate, result.Observation.Predicate);
        Assert.Equal(ObservationScope.Of(SourceId), result.Observation.Scope);
        Assert.Equal(observedAt, result.Observation.ObservedAt);
        Assert.Contains("call now", result.MatchedFeatures);
        Assert.Contains("operators are standing by", result.MatchedFeatures);
    }

    [Fact]
    public void Detect_MatchIsCaseInsensitive()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new CaptionSample("CALL NOW for this LIMITED TIME OFFER", observedAt, observedAt.AddSeconds(1)),
        };

        var results = _detector.Detect(SourceId, samples);

        var result = Assert.Single(results);
        Assert.Equal(BroadcastState.Commercial, result.Observation.Value);
        Assert.Contains("call now", result.MatchedFeatures);
        Assert.Contains("limited time offer", result.MatchedFeatures);
    }

    [Fact]
    public void Detect_MoreMatchedPhrasesYieldsHigherConfidenceCappedAtMax()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var oneMatch = new[] { new CaptionSample("Call now to order.", observedAt, observedAt.AddSeconds(1)) };
        var manyMatches = new[]
        {
            new CaptionSample(
                "Call now, act now, order now while supplies are limited. Terms and conditions apply. " +
                "Operators are standing by. Visit our website. Money back guarantee. As seen on TV. " +
                "Click here to order. Shipping and handling extra. While supplies last. Call this number now.",
                observedAt,
                observedAt.AddSeconds(5)),
        };

        var oneResult = Assert.Single(_detector.Detect(SourceId, oneMatch));
        var manyResult = Assert.Single(_detector.Detect(SourceId, manyMatches));

        Assert.Equal(0.5, oneResult.Observation.Confidence);
        Assert.True(manyResult.Observation.Confidence > oneResult.Observation.Confidence);
        Assert.True(manyResult.Observation.Confidence <= 0.75);
        Assert.Equal(0.75, manyResult.Observation.Confidence);
    }

    [Fact]
    public void Detect_CustomPhraseListIsRespected()
    {
        var detector = new CaptionOcrDetector(new[] { "totally unique brand slogan" });
        var observedAt = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            // Would match a default phrase, but this detector only knows its custom list.
            new CaptionSample("Call now, operators are standing by!", observedAt, observedAt.AddSeconds(1)),
            new CaptionSample("Remember our totally unique brand slogan today.", observedAt.AddSeconds(2), observedAt.AddSeconds(3)),
        };

        var results = detector.Detect(SourceId, samples);

        var result = Assert.Single(results);
        Assert.Equal(observedAt.AddSeconds(2), result.Observation.ObservedAt);
        Assert.Contains("totally unique brand slogan", result.MatchedFeatures);
    }

    [Fact]
    public void Detect_HigherMinimumMatchCountSuppressesSingleMatchSamples()
    {
        var detector = new CaptionOcrDetector(minimumMatchCount: 2);
        var observedAt = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new CaptionSample("Call now to save.", observedAt, observedAt.AddSeconds(1)),
            new CaptionSample("Call now, operators are standing by.", observedAt.AddSeconds(2), observedAt.AddSeconds(3)),
        };

        var results = detector.Detect(SourceId, samples);

        var result = Assert.Single(results);
        Assert.Equal(observedAt.AddSeconds(2), result.Observation.ObservedAt);
    }

    // ---- Neutral dialogue ----

    [Fact]
    public void Detect_WithNeutralDialogueOnly_EmitsNoObservations()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new CaptionSample("I can't believe you did that.", observedAt, observedAt.AddSeconds(2)),
            new CaptionSample("We need to talk about the case.", observedAt.AddSeconds(2), observedAt.AddSeconds(4)),
            new CaptionSample("Meet me at the usual place tonight.", observedAt.AddSeconds(4), observedAt.AddSeconds(6)),
        };

        var results = _detector.Detect(SourceId, samples);

        Assert.Empty(results);
    }

    [Fact]
    public void Detect_MixedNeutralAndCommercialOnlyEmitsForMatchingSamples()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new CaptionSample("Let's get out of here before it's too late.", observedAt, observedAt.AddSeconds(2)),
            new CaptionSample("Call now, limited time offer ends soon!", observedAt.AddSeconds(2), observedAt.AddSeconds(4)),
            new CaptionSample("She looked out over the valley in silence.", observedAt.AddSeconds(4), observedAt.AddSeconds(6)),
        };

        var results = _detector.Detect(SourceId, samples);

        var result = Assert.Single(results);
        Assert.Equal(observedAt.AddSeconds(2), result.Observation.ObservedAt);
    }

    // ---- Sparse / no captions ----

    [Fact]
    public void Detect_WithEmptySampleList_EmitsNoObservations()
    {
        var results = _detector.Detect(SourceId, Array.Empty<CaptionSample>());

        Assert.Empty(results);
    }

    [Fact]
    public void Detect_WithSparseWhitespaceOnlyCaptions_EmitsNoObservations()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new CaptionSample("", observedAt, observedAt.AddSeconds(1)),
            new CaptionSample("   ", observedAt.AddSeconds(1), observedAt.AddSeconds(2)),
        };

        var results = _detector.Detect(SourceId, samples);

        Assert.Empty(results);
    }

    // ---- Argument validation ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Detect_WithInvalidSourceId_ThrowsArgumentException(string? sourceId)
    {
        var samples = new[] { new CaptionSample("Call now!", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(1)) };

        Assert.ThrowsAny<ArgumentException>(() => _detector.Detect(sourceId!, samples));
    }

    [Fact]
    public void Detect_RejectsNullSamples()
    {
        Assert.Throws<ArgumentNullException>(() => _detector.Detect(SourceId, null!));
    }

    [Fact]
    public void Constructor_RejectsMinimumMatchCountBelowOne()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new CaptionOcrDetector(minimumMatchCount: 0));
        Assert.Equal("minimumMatchCount", ex.ParamName);
    }

    [Fact]
    public void Detect_EachCallGeneratesUniqueObservationId()
    {
        var samples = new[] { new CaptionSample("Call now!", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(1)) };

        var first = _detector.Detect(SourceId, samples);
        var second = _detector.Detect(SourceId, samples);

        Assert.NotEqual(Assert.Single(first).Observation.Id, Assert.Single(second).Observation.Id);
    }

    // ---- Evaluation metrics: bounded execution cost ----

    [Fact]
    public void Detect_OnSmallBoundedSampleList_CompletesWithinGenerousTimeBound()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var samples = new List<CaptionSample>();
        for (int i = 0; i < 200; i++)
        {
            var text = i % 5 == 0
                ? "Call now, limited time offer!"
                : "Just an ordinary line of dialogue in the scene.";
            samples.Add(new CaptionSample(text, observedAt.AddSeconds(i), observedAt.AddSeconds(i + 1)));
        }

        var stopwatch = Stopwatch.StartNew();
        var results = _detector.Detect(SourceId, samples);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Detect took {stopwatch.Elapsed}, expected under 1 second.");
        Assert.NotEmpty(results);
    }
}
