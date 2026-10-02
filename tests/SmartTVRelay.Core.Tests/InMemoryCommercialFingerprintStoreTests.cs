using SmartTVRelay.Core;
using Xunit;

namespace SmartTVRelay.Core.Tests;

public sealed class InMemoryCommercialFingerprintStoreTests
{
    [Fact]
    public void ConstructorRejectsNullEntries()
    {
        Assert.Throws<ArgumentNullException>(() => new InMemoryCommercialFingerprintStore(null!));
    }

    [Fact]
    public void ConstructorRejectsWhitespaceCommercialId()
    {
        var entries = new[] { new KeyValuePair<string, IReadOnlyList<int>>("   ", new[] { 1, 0, 1 }) };

        Assert.Throws<ArgumentException>(() => new InMemoryCommercialFingerprintStore(entries));
    }

    [Fact]
    public void ConstructorRejectsEmptyHashVector()
    {
        var entries = new[] { new KeyValuePair<string, IReadOnlyList<int>>("ad-1", Array.Empty<int>()) };

        Assert.Throws<ArgumentException>(() => new InMemoryCommercialFingerprintStore(entries));
    }

    [Fact]
    public void FindClosestMatchRejectsNullQuery()
    {
        var store = new InMemoryCommercialFingerprintStore(Array.Empty<KeyValuePair<string, IReadOnlyList<int>>>());

        Assert.Throws<ArgumentNullException>(() => store.FindClosestMatch(null!));
    }

    [Fact]
    public void FindClosestMatchRejectsEmptyQuery()
    {
        var store = new InMemoryCommercialFingerprintStore(Array.Empty<KeyValuePair<string, IReadOnlyList<int>>>());

        Assert.Throws<ArgumentException>(() => store.FindClosestMatch(Array.Empty<int>()));
    }

    [Fact]
    public void FindClosestMatchReturnsNullWhenStoreIsEmpty()
    {
        var store = new InMemoryCommercialFingerprintStore(Array.Empty<KeyValuePair<string, IReadOnlyList<int>>>());

        var match = store.FindClosestMatch(new[] { 1, 0, 1 });

        Assert.Null(match);
    }

    [Fact]
    public void FindClosestMatchReturnsNullWhenNoEntryHasTheSameLength()
    {
        var store = new InMemoryCommercialFingerprintStore(new[]
        {
            new KeyValuePair<string, IReadOnlyList<int>>("ad-1", new[] { 1, 0, 1, 0 }),
        });

        var match = store.FindClosestMatch(new[] { 1, 0, 1 });

        Assert.Null(match);
    }

    [Fact]
    public void FindClosestMatchReturnsZeroDistanceForAnExactMatch()
    {
        var store = new InMemoryCommercialFingerprintStore(new[]
        {
            new KeyValuePair<string, IReadOnlyList<int>>("ad-1", new[] { 1, 0, 1, 0 }),
        });

        var match = store.FindClosestMatch(new[] { 1, 0, 1, 0 });

        Assert.NotNull(match);
        Assert.Equal("ad-1", match!.CommercialId);
        Assert.Equal(0.0, match.DistanceRatio);
    }

    [Fact]
    public void FindClosestMatchComputesHammingDistanceRatio()
    {
        var store = new InMemoryCommercialFingerprintStore(new[]
        {
            new KeyValuePair<string, IReadOnlyList<int>>("ad-1", new[] { 1, 1, 1, 1 }),
        });

        // 1 of 4 positions differs.
        var match = store.FindClosestMatch(new[] { 1, 1, 1, 0 });

        Assert.NotNull(match);
        Assert.Equal(0.25, match!.DistanceRatio);
    }

    [Fact]
    public void FindClosestMatchPicksTheClosestOfMultipleEntries()
    {
        var store = new InMemoryCommercialFingerprintStore(new[]
        {
            new KeyValuePair<string, IReadOnlyList<int>>("far", new[] { 0, 0, 0, 0 }),
            new KeyValuePair<string, IReadOnlyList<int>>("close", new[] { 1, 1, 1, 0 }),
        });

        var match = store.FindClosestMatch(new[] { 1, 1, 1, 1 });

        Assert.NotNull(match);
        Assert.Equal("close", match!.CommercialId);
        Assert.Equal(0.25, match.DistanceRatio);
    }

    [Fact]
    public void FindClosestMatchOnlyComparesEntriesOfTheSameLength()
    {
        var store = new InMemoryCommercialFingerprintStore(new[]
        {
            new KeyValuePair<string, IReadOnlyList<int>>("wrong-length", new[] { 1, 1, 1 }),
            new KeyValuePair<string, IReadOnlyList<int>>("right-length", new[] { 0, 0, 0, 0 }),
        });

        var match = store.FindClosestMatch(new[] { 1, 1, 1, 1 });

        Assert.NotNull(match);
        Assert.Equal("right-length", match!.CommercialId);
    }

    [Fact]
    public void FindClosestMatchIsDeterministicForTiesByKeepingConstructionOrder()
    {
        var store = new InMemoryCommercialFingerprintStore(new[]
        {
            new KeyValuePair<string, IReadOnlyList<int>>("first", new[] { 1, 1, 0, 0 }),
            new KeyValuePair<string, IReadOnlyList<int>>("second", new[] { 0, 0, 1, 1 }),
        });

        // Equidistant (2 of 4 positions differ) from both entries.
        var match = store.FindClosestMatch(new[] { 1, 0, 1, 0 });

        Assert.NotNull(match);
        Assert.Equal("first", match!.CommercialId);
        Assert.Equal(0.5, match.DistanceRatio);
    }
}
