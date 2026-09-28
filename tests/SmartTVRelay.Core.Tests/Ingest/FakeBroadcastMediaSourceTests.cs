namespace SmartTVRelay.Core.Tests.Ingest;

using SmartTVRelay.Core.Ingest;
using Xunit;

public class FakeBroadcastMediaSourceTests
{
    [Fact]
    public async Task ReadAsync_ReplaysChunksInOrder()
    {
        var chunks = new[]
        {
            new MediaChunk(new byte[] { 1, 2 }, TimeSpan.Zero),
            new MediaChunk(new byte[] { 3, 4 }, TimeSpan.FromSeconds(1)),
        };
        var source = new FakeBroadcastMediaSource("test-source", chunks);

        var read = new List<MediaChunk>();
        await foreach (var chunk in source.ReadAsync(CancellationToken.None))
        {
            read.Add(chunk);
        }

        Assert.Equal(2, read.Count);
        Assert.Equal(chunks[0].SourceTime, read[0].SourceTime);
        Assert.Equal(chunks[1].SourceTime, read[1].SourceTime);
    }

    [Fact]
    public async Task ReadAsync_CompletesNormallyAtEndOfStream()
    {
        var source = new FakeBroadcastMediaSource(
            "test-source",
            new[] { new MediaChunk(new byte[] { 1 }, TimeSpan.Zero) });

        var count = 0;
        await foreach (var _ in source.ReadAsync(CancellationToken.None))
        {
            count++;
        }

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ReadAsync_HonorsCancellation()
    {
        var chunks = Enumerable.Range(0, 100)
            .Select(i => new MediaChunk(new byte[] { (byte)i }, TimeSpan.FromSeconds(i)))
            .ToArray();
        var source = new FakeBroadcastMediaSource("test-source", chunks);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            var seen = 0;
            await foreach (var _ in source.ReadAsync(cts.Token))
            {
                seen++;
                if (seen == 3)
                {
                    cts.Cancel();
                }
            }
        });
    }

    [Fact]
    public void Status_DefaultsToHealthy()
    {
        var source = new FakeBroadcastMediaSource("test-source", Array.Empty<MediaChunk>());

        Assert.Equal(SourceHealth.Healthy, source.Status.Health);
    }

    [Fact]
    public void Status_CanBeOverridden()
    {
        var status = new SourceStatus(SourceHealth.Degraded, "signal weak");
        var source = new FakeBroadcastMediaSource("test-source", Array.Empty<MediaChunk>(), status);

        Assert.Equal(SourceHealth.Degraded, source.Status.Health);
        Assert.Equal("signal weak", source.Status.Detail);
    }

    [Fact]
    public async Task DisposeAsync_MarksDisposed()
    {
        var source = new FakeBroadcastMediaSource("test-source", Array.Empty<MediaChunk>());

        await source.DisposeAsync();

        Assert.True(source.DisposeCalled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_RejectsInvalidSourceId(string? sourceId)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new FakeBroadcastMediaSource(sourceId!, Array.Empty<MediaChunk>()));
    }

    [Fact]
    public void Constructor_RejectsNullChunks()
    {
        Assert.Throws<ArgumentNullException>(() => new FakeBroadcastMediaSource("test-source", null!));
    }

    [Fact]
    public void SourceId_IsExposedAsConstructed()
    {
        var source = new FakeBroadcastMediaSource("my-source", Array.Empty<MediaChunk>());

        Assert.Equal("my-source", source.SourceId);
    }
}
