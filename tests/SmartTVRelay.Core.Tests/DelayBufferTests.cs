namespace SmartTVRelay.Core.Tests;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SmartTVRelay.Core.Ingest;
using SmartTVRelay.Core.Relay;
using Xunit;

public class DelayBufferTests
{
    private static MediaChunk Chunk(int seconds, int bytes = 100) =>
        new(new byte[bytes], TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Constructor_NegativeDelay_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DelayBuffer(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(10), 1_000));
    }

    [Fact]
    public void Constructor_NonPositiveMaxDuration_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DelayBuffer(TimeSpan.Zero, TimeSpan.Zero, 1_000));
    }

    [Fact]
    public void Constructor_NonPositiveMaxBytes_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DelayBuffer(TimeSpan.Zero, TimeSpan.FromSeconds(10), 0));
    }

    [Fact]
    public void Enqueue_OutOfOrderSourceTime_Throws()
    {
        var buffer = new DelayBuffer(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), 10_000);
        buffer.Enqueue(Chunk(5));

        Assert.Throws<ArgumentException>(() => buffer.Enqueue(Chunk(4)));
    }

    [Fact]
    public void Enqueue_EqualSourceTime_IsAllowed()
    {
        var buffer = new DelayBuffer(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), 10_000);
        buffer.Enqueue(Chunk(5));
        buffer.Enqueue(Chunk(5));

        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void TryDequeue_BeforeDelayElapsed_ReturnsFalseWithoutRemoving()
    {
        var buffer = new DelayBuffer(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), 10_000);
        buffer.Enqueue(Chunk(0));
        buffer.Enqueue(Chunk(4));

        var dequeued = buffer.TryDequeue(out _);

        Assert.False(dequeued);
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void TryDequeue_OnceDelayElapsed_ReturnsOldestChunk()
    {
        var buffer = new DelayBuffer(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), 10_000);
        buffer.Enqueue(Chunk(0));
        buffer.Enqueue(Chunk(5));

        var dequeued = buffer.TryDequeue(out var chunk);

        Assert.True(dequeued);
        Assert.Equal(TimeSpan.FromSeconds(0), chunk.SourceTime);
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void TryDequeue_ExactlyAtDelayBoundary_ReturnsTrue()
    {
        var buffer = new DelayBuffer(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), 10_000);
        buffer.Enqueue(Chunk(0));
        buffer.Enqueue(Chunk(5));

        Assert.True(buffer.TryDequeue(out _));
    }

    [Fact]
    public void TryDequeue_EmptyBuffer_ReturnsFalse()
    {
        var buffer = new DelayBuffer(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), 10_000);

        Assert.False(buffer.TryDequeue(out _));
    }

    [Fact]
    public void Enqueue_ExceedsMaxDuration_EvictsOldestChunks()
    {
        var buffer = new DelayBuffer(TimeSpan.Zero, TimeSpan.FromSeconds(10), 10_000);
        buffer.Enqueue(Chunk(0));
        buffer.Enqueue(Chunk(5));
        buffer.Enqueue(Chunk(11));

        Assert.Equal(2, buffer.Count);
        Assert.False(buffer.TryDequeue(out var first) && first.SourceTime == TimeSpan.Zero);
    }

    [Fact]
    public void Enqueue_ExceedsMaxBytes_EvictsOldestChunks()
    {
        var buffer = new DelayBuffer(TimeSpan.Zero, TimeSpan.FromSeconds(60), maxBufferedBytes: 250);
        buffer.Enqueue(Chunk(0, bytes: 100));
        buffer.Enqueue(Chunk(1, bytes: 100));
        buffer.Enqueue(Chunk(2, bytes: 100));

        Assert.Equal(200, buffer.BufferedBytes);
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void Enqueue_SingleChunkExceedsBounds_NeverEvictsBelowOne()
    {
        var buffer = new DelayBuffer(TimeSpan.Zero, TimeSpan.FromSeconds(1), maxBufferedBytes: 10);
        buffer.Enqueue(Chunk(0, bytes: 1_000));

        Assert.Equal(1, buffer.Count);
        Assert.Equal(1_000, buffer.BufferedBytes);
    }

    [Fact]
    public void Enqueue_WithinBounds_DoesNotEvict()
    {
        var buffer = new DelayBuffer(TimeSpan.Zero, TimeSpan.FromSeconds(60), 10_000);
        buffer.Enqueue(Chunk(0));
        buffer.Enqueue(Chunk(1));
        buffer.Enqueue(Chunk(2));

        Assert.Equal(3, buffer.Count);
        Assert.Equal(300, buffer.BufferedBytes);
    }

    [Fact]
    public async Task FillFromAsync_EnqueuesEveryChunkFromSource()
    {
        var buffer = new DelayBuffer(TimeSpan.Zero, TimeSpan.FromSeconds(60), 10_000);
        var channel = Channel.CreateUnbounded<MediaChunk>();
        channel.Writer.TryWrite(Chunk(0));
        channel.Writer.TryWrite(Chunk(1));
        channel.Writer.TryWrite(Chunk(2));
        channel.Writer.Complete();

        await buffer.FillFromAsync(channel.Reader.ReadAllAsync());

        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public async Task FillFromAsync_Cancelled_StopsWithoutThrowingUnexpectedException()
    {
        var buffer = new DelayBuffer(TimeSpan.Zero, TimeSpan.FromSeconds(60), 10_000);
        using var cts = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<MediaChunk>();
        channel.Writer.TryWrite(Chunk(0));

        async IAsyncEnumerable<MediaChunk> InfiniteAsync([EnumeratorCancellation] CancellationToken token = default)
        {
            var seconds = 0L;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                yield return new MediaChunk(new byte[100], TimeSpan.FromSeconds(seconds++));
                await Task.Yield();
            }
        }

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => buffer.FillFromAsync(InfiniteAsync(), cts.Token));
    }

    [Fact]
    public void Dispose_ThenEnqueue_ThrowsObjectDisposedException()
    {
        var buffer = new DelayBuffer(TimeSpan.Zero, TimeSpan.FromSeconds(60), 10_000);
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer.Enqueue(Chunk(0)));
    }

    [Fact]
    public void Dispose_ThenTryDequeue_ThrowsObjectDisposedException()
    {
        var buffer = new DelayBuffer(TimeSpan.Zero, TimeSpan.FromSeconds(60), 10_000);
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer.TryDequeue(out _));
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var buffer = new DelayBuffer(TimeSpan.Zero, TimeSpan.FromSeconds(60), 10_000);
        buffer.Dispose();
        buffer.Dispose();
    }
}
