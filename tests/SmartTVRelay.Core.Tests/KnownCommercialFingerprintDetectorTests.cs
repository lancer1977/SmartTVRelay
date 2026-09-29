using Observation.Core;
using SmartTVRelay.Core;
using Xunit;

namespace SmartTVRelay.Core.Tests;

public sealed class KnownCommercialFingerprintDetectorTests
{
    // 20-element vectors so that N differing positions map to a clean N/20 = N*5% distance ratio.
    private static readonly int[] KnownAd = { 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0 };

    private static InMemoryCommercialFingerprintStore SingleEntryStore()
    {
        return new InMemoryCommercialFingerprintStore(new[]
        {
            new KeyValuePair<string, IReadOnlyList<int>>("known-ad-1", KnownAd),
        });
    }

    private static int[] FlipFirstN(int[] source, int n)
    {
        var copy = (int[])source.Clone();
        for (var i = 0; i < n; i++)
        {
            copy[i] = copy[i] == 0 ? 1 : 0;
        }

        return copy;
    }

    [Fact]
    public void ExactMatchIsAKnownMatchWithHighConfidence()
    {
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());
        var sourceId = "test-source";
        var capturedAt = DateTimeOffset.UtcNow;
        var samples = new[] { new AudioFingerprintSample(KnownAd, capturedAt) };

        var observations = detector.Detect(sourceId, samples);

        var obs = Assert.Single(observations);
        Assert.Equal(BroadcastState.Commercial, obs.Value);
        Assert.Equal("commercial-fingerprint", obs.SourceKind);
        Assert.Equal(0.9, obs.Confidence);
        Assert.Equal(sourceId, obs.SourceId);
        Assert.Equal(BroadcastObservations.Subject, obs.Subject);
        Assert.Equal(BroadcastObservations.Predicate, obs.Predicate);
        Assert.Equal(ObservationScope.Of(sourceId), obs.Scope);
        Assert.Equal(capturedAt, obs.ObservedAt);
    }

    [Fact]
    public void WithinKnownMatchThresholdIsAKnownMatch()
    {
        // 2 of 20 positions differ => 0.10 distance ratio, exactly at the known-match boundary (<=).
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());
        var query = FlipFirstN(KnownAd, 2);
        var samples = new[] { new AudioFingerprintSample(query, DateTimeOffset.UtcNow) };

        var observations = detector.Detect("test-source", samples);

        var obs = Assert.Single(observations);
        Assert.Equal(BroadcastState.Commercial, obs.Value);
        Assert.Equal(0.9, obs.Confidence);
    }

    [Fact]
    public void NearMissIsEmittedWithLowerConfidence()
    {
        // 5 of 20 positions differ => 0.25 distance ratio, between the known-match and near-miss thresholds.
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());
        var query = FlipFirstN(KnownAd, 5);
        var samples = new[] { new AudioFingerprintSample(query, DateTimeOffset.UtcNow) };

        var observations = detector.Detect("test-source", samples);

        var obs = Assert.Single(observations);
        Assert.Equal(BroadcastState.Commercial, obs.Value);
        Assert.Equal(0.4, obs.Confidence);
        Assert.Equal("commercial-fingerprint", obs.SourceKind);
    }

    [Fact]
    public void AtNearMissThresholdIsStillEmitted()
    {
        // 7 of 20 positions differ => 0.35 distance ratio, exactly at the near-miss boundary (<=).
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());
        var query = FlipFirstN(KnownAd, 7);
        var samples = new[] { new AudioFingerprintSample(query, DateTimeOffset.UtcNow) };

        var observations = detector.Detect("test-source", samples);

        var obs = Assert.Single(observations);
        Assert.Equal(0.4, obs.Confidence);
    }

    [Fact]
    public void BeyondNearMissThresholdIsUnknownAndEmitsNothing()
    {
        // 8 of 20 positions differ => 0.40 distance ratio, just past the near-miss boundary.
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());
        var query = FlipFirstN(KnownAd, 8);
        var samples = new[] { new AudioFingerprintSample(query, DateTimeOffset.UtcNow) };

        var observations = detector.Detect("test-source", samples);

        Assert.Empty(observations);
    }

    [Fact]
    public void CompletelyDissimilarFingerprintIsUnknownAndEmitsNothing()
    {
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());
        var query = FlipFirstN(KnownAd, 20);
        var samples = new[] { new AudioFingerprintSample(query, DateTimeOffset.UtcNow) };

        var observations = detector.Detect("test-source", samples);

        Assert.Empty(observations);
    }

    [Fact]
    public void NoComparableEntryInStoreEmitsNothing()
    {
        // Store only has a 20-element entry; a 5-element query is not comparable to it.
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());
        var samples = new[] { new AudioFingerprintSample(new[] { 1, 1, 1, 1, 1 }, DateTimeOffset.UtcNow) };

        var observations = detector.Detect("test-source", samples);

        Assert.Empty(observations);
    }

    [Fact]
    public void EmptyStoreEmitsNothing()
    {
        var store = new InMemoryCommercialFingerprintStore(Array.Empty<KeyValuePair<string, IReadOnlyList<int>>>());
        var detector = new KnownCommercialFingerprintDetector(store);
        var samples = new[] { new AudioFingerprintSample(KnownAd, DateTimeOffset.UtcNow) };

        var observations = detector.Detect("test-source", samples);

        Assert.Empty(observations);
    }

    [Fact]
    public void EmptyFingerprintSampleIsSkipped()
    {
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());
        var samples = new[] { new AudioFingerprintSample(Array.Empty<int>(), DateTimeOffset.UtcNow) };

        var observations = detector.Detect("test-source", samples);

        Assert.Empty(observations);
    }

    [Fact]
    public void FixtureSequenceCoveringKnownNearMissAndUnknownProducesExpectedResults()
    {
        // A representative sequence: known match, near miss, unknown, known match again, interleaved.
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;

        var samples = new[]
        {
            new AudioFingerprintSample(KnownAd, now.AddSeconds(1)),                 // known match
            new AudioFingerprintSample(FlipFirstN(KnownAd, 5), now.AddSeconds(2)),  // near miss
            new AudioFingerprintSample(FlipFirstN(KnownAd, 15), now.AddSeconds(3)), // unknown
            new AudioFingerprintSample(FlipFirstN(KnownAd, 1), now.AddSeconds(4)),  // known match (5% differ)
        };

        var observations = detector.Detect(sourceId, samples);

        Assert.Equal(3, observations.Count);
        Assert.Equal(now.AddSeconds(1), observations[0].ObservedAt);
        Assert.Equal(0.9, observations[0].Confidence);
        Assert.Equal(now.AddSeconds(2), observations[1].ObservedAt);
        Assert.Equal(0.4, observations[1].Confidence);
        Assert.Equal(now.AddSeconds(4), observations[2].ObservedAt);
        Assert.Equal(0.9, observations[2].Confidence);
    }

    [Fact]
    public void DetectIsDeterministicAcrossRepeatedCalls()
    {
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());
        var sourceId = "test-source";
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new AudioFingerprintSample(KnownAd, now.AddSeconds(1)),
            new AudioFingerprintSample(FlipFirstN(KnownAd, 5), now.AddSeconds(2)),
            new AudioFingerprintSample(FlipFirstN(KnownAd, 15), now.AddSeconds(3)),
        };

        var first = detector.Detect(sourceId, samples);
        var second = detector.Detect(sourceId, samples);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Value, second[i].Value);
            Assert.Equal(first[i].Confidence, second[i].Confidence);
            Assert.Equal(first[i].SourceKind, second[i].SourceKind);
            Assert.Equal(first[i].SourceId, second[i].SourceId);
            Assert.Equal(first[i].ObservedAt, second[i].ObservedAt);
            Assert.Equal(first[i].Scope, second[i].Scope);
            // Each observation gets its own identity even when the underlying evidence repeats.
            Assert.NotEqual(first[i].Id, second[i].Id);
        }
    }

    [Fact]
    public void DetectCompletesWithinAGenerousLatencyBoundForABoundedSampleList()
    {
        // A representative small-but-nontrivial corpus/query size for a single Detect() call.
        var random = new Random(42);
        var knownEntries = new List<KeyValuePair<string, IReadOnlyList<int>>>();
        for (var i = 0; i < 50; i++)
        {
            var vector = new int[32];
            for (var j = 0; j < vector.Length; j++)
            {
                vector[j] = random.Next(0, 2);
            }

            knownEntries.Add(new KeyValuePair<string, IReadOnlyList<int>>($"known-ad-{i}", vector));
        }

        var store = new InMemoryCommercialFingerprintStore(knownEntries);
        var detector = new KnownCommercialFingerprintDetector(store);

        var samples = new List<AudioFingerprintSample>();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 200; i++)
        {
            var vector = new int[32];
            for (var j = 0; j < vector.Length; j++)
            {
                vector[j] = random.Next(0, 2);
            }

            samples.Add(new AudioFingerprintSample(vector, now.AddMilliseconds(i)));
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var observations = detector.Detect("test-source", samples);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"Detect() took {stopwatch.ElapsedMilliseconds}ms, expected under 1000ms for a bounded sample list.");
        Assert.NotNull(observations);
    }

    [Fact]
    public void ConstructorRejectsNullStore()
    {
        Assert.Throws<ArgumentNullException>(() => new KnownCommercialFingerprintDetector(null!));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void ConstructorRejectsInvalidKnownMatchThreshold(double invalidThreshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new KnownCommercialFingerprintDetector(SingleEntryStore(), knownMatchMaxDistanceRatio: invalidThreshold));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void ConstructorRejectsInvalidNearMissThreshold(double invalidThreshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new KnownCommercialFingerprintDetector(SingleEntryStore(), nearMissMaxDistanceRatio: invalidThreshold));
    }

    [Fact]
    public void ConstructorRejectsNearMissThresholdBelowKnownMatchThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new KnownCommercialFingerprintDetector(SingleEntryStore(), knownMatchMaxDistanceRatio: 0.5, nearMissMaxDistanceRatio: 0.2));
    }

    [Fact]
    public void DetectRejectsNullSourceId()
    {
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());
        var samples = new[] { new AudioFingerprintSample(KnownAd, DateTimeOffset.UtcNow) };

        Assert.Throws<ArgumentNullException>(() => detector.Detect(null!, samples));
    }

    [Fact]
    public void DetectRejectsWhitespaceSourceId()
    {
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());
        var samples = new[] { new AudioFingerprintSample(KnownAd, DateTimeOffset.UtcNow) };

        Assert.Throws<ArgumentException>(() => detector.Detect("   ", samples));
    }

    [Fact]
    public void DetectRejectsNullSamples()
    {
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());

        Assert.Throws<ArgumentNullException>(() => detector.Detect("test-source", null!));
    }

    [Fact]
    public void DetectAcceptsEmptySamplesList()
    {
        var detector = new KnownCommercialFingerprintDetector(SingleEntryStore());

        var observations = detector.Detect("test-source", Array.Empty<AudioFingerprintSample>());

        Assert.Empty(observations);
    }
}
