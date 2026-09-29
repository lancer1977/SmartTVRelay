namespace SmartTVRelay.Core.Tests.Relay;

using System.Runtime.CompilerServices;
using SmartTVRelay.Core.Ingest;
using SmartTVRelay.Core.Relay;
using Xunit;

public class ContinuousRelayOutputTests
{
    private static string GetFixturePath(string filename) => Path.Combine(AppContext.BaseDirectory, "synthetic", filename);

    [Fact]
    public async Task Subscribe_RecordedFixture_YieldsChunksInNonDecreasingOrder()
    {
        var source = new RecordedFileMediaSource(
            "test-source", GetFixturePath("black-with-tone.ts"), chunkSizeBytes: 4096, chunkInterval: TimeSpan.FromMilliseconds(20));
        await using var output = new ContinuousRelayOutput(
            source, delay: TimeSpan.FromMilliseconds(50), maxBufferedDuration: TimeSpan.FromSeconds(10),
            maxBufferedBytes: 1_000_000, pollInterval: TimeSpan.FromMilliseconds(5));
        output.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<MediaChunk>();
        await foreach (var chunk in output.Subscribe(cts.Token))
        {
            received.Add(chunk);
        }

        Assert.NotEmpty(received);
        Assert.Equal(RelayOutputStatus.Completed, output.Status);
        for (var i = 1; i < received.Count; i++)
        {
            Assert.True(received[i].SourceTime >= received[i - 1].SourceTime);
        }
    }

    [Fact]
    public async Task Subscribe_TotalBytesMatchSourceFile()
    {
        var filePath = GetFixturePath("black-with-tone.ts");
        var expectedBytes = new FileInfo(filePath).Length;
        var source = new RecordedFileMediaSource("test-source", filePath, chunkSizeBytes: 4096, chunkInterval: TimeSpan.FromMilliseconds(20));
        await using var output = new ContinuousRelayOutput(
            source, delay: TimeSpan.FromMilliseconds(50), maxBufferedDuration: TimeSpan.FromSeconds(10),
            maxBufferedBytes: 1_000_000, pollInterval: TimeSpan.FromMilliseconds(5));
        output.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        long totalBytes = 0;
        await foreach (var chunk in output.Subscribe(cts.Token))
        {
            totalBytes += chunk.Data.Length;
        }

        Assert.Equal(expectedBytes, totalBytes);
    }

    [Fact]
    public async Task Subscribe_NothingYieldedBeforeDelayElapsesWhileSourceStillRunning()
    {
        // A delay far longer than our observation window means nothing should be dequeued yet,
        // as long as the source is still actively running (more chunks could still arrive that
        // would make this one "age" further before it's actually due).
        var source = new SlowMediaSource();
        await using var output = new ContinuousRelayOutput(
            source, delay: TimeSpan.FromSeconds(30), maxBufferedDuration: TimeSpan.FromSeconds(60),
            maxBufferedBytes: 1_000_000, pollInterval: TimeSpan.FromMilliseconds(5));
        output.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var received = new List<MediaChunk>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in output.Subscribe(cts.Token))
            {
                received.Add(chunk);
            }
        });

        Assert.Empty(received);
        Assert.Equal(RelayOutputStatus.Running, output.Status);
    }

    [Fact]
    public async Task Subscribe_DrainsRemainingChunksOnceSourceCompletesEvenIfNotFullyAged()
    {
        // A delay far longer than the whole recording's span means individual chunks would never
        // "naturally" age enough on their own. Once the source is known to have ended, nothing
        // will ever arrive to advance that aging further, so the remainder must still be
        // delivered rather than stranded in the buffer forever.
        var source = new RecordedFileMediaSource(
            "test-source", GetFixturePath("black-with-tone.ts"), chunkSizeBytes: 4096, chunkInterval: TimeSpan.FromMilliseconds(20));
        await using var output = new ContinuousRelayOutput(
            source, delay: TimeSpan.FromSeconds(30), maxBufferedDuration: TimeSpan.FromSeconds(60),
            maxBufferedBytes: 1_000_000, pollInterval: TimeSpan.FromMilliseconds(5));
        output.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<MediaChunk>();
        await foreach (var chunk in output.Subscribe(cts.Token))
        {
            received.Add(chunk);
        }

        Assert.NotEmpty(received);
        Assert.Equal(RelayOutputStatus.Completed, output.Status);
    }

    [Fact]
    public async Task Subscribe_DisconnectThenReconnect_ContinuesReceivingRemainingChunks()
    {
        var source = new RecordedFileMediaSource(
            "test-source", GetFixturePath("black-with-tone.ts"), chunkSizeBytes: 4096, chunkInterval: TimeSpan.FromMilliseconds(5));
        await using var output = new ContinuousRelayOutput(
            source, delay: TimeSpan.FromMilliseconds(10), maxBufferedDuration: TimeSpan.FromSeconds(10),
            maxBufferedBytes: 1_000_000, pollInterval: TimeSpan.FromMilliseconds(5));
        output.Start();

        var firstBatch = new List<MediaChunk>();
        using (var firstCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try
            {
                await foreach (var chunk in output.Subscribe(firstCts.Token))
                {
                    firstBatch.Add(chunk);
                    if (firstBatch.Count == 2)
                    {
                        firstCts.Cancel(); // simulate a client disconnecting mid-stream
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: the disconnect itself surfaces as cancellation of this enumeration.
            }
        }

        Assert.True(firstBatch.Count >= 2);

        // Reconnect: a brand-new Subscribe call must not throw despite the previous one having
        // been cancelled, and should keep draining whatever the pump produces afterward.
        using var secondCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var secondBatch = new List<MediaChunk>();
        await foreach (var chunk in output.Subscribe(secondCts.Token))
        {
            secondBatch.Add(chunk);
        }

        Assert.NotEmpty(secondBatch);
        Assert.Equal(RelayOutputStatus.Completed, output.Status);
    }

    [Fact]
    public void Start_CalledTwice_Throws()
    {
        var source = new FakeBroadcastMediaSource("fake", []);
        var output = new ContinuousRelayOutput(source, TimeSpan.Zero, TimeSpan.FromSeconds(10), 1_000_000);

        output.Start();

        Assert.Throws<InvalidOperationException>(output.Start);
    }

    [Fact]
    public async Task DisposeAsync_StopsPumpAndDisposesSource()
    {
        var chunks = new[] { new MediaChunk(new byte[10], TimeSpan.Zero) };
        var source = new FakeBroadcastMediaSource("fake", chunks);
        var output = new ContinuousRelayOutput(
            source, TimeSpan.Zero, TimeSpan.FromSeconds(10), 1_000_000, pollInterval: TimeSpan.FromMilliseconds(5));
        output.Start();

        await output.DisposeAsync();

        Assert.True(source.DisposeCalled);
    }

    [Fact]
    public async Task PumpFault_ReflectedInStatusAndFault()
    {
        var source = new ThrowingMediaSource();
        await using var output = new ContinuousRelayOutput(
            source, TimeSpan.Zero, TimeSpan.FromSeconds(10), 1_000_000, pollInterval: TimeSpan.FromMilliseconds(5));
        output.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<MediaChunk>();
        await foreach (var chunk in output.Subscribe(cts.Token))
        {
            received.Add(chunk);
        }

        Assert.Empty(received);
        Assert.Equal(RelayOutputStatus.Faulted, output.Status);
        Assert.NotNull(output.Fault);
    }

    private sealed class SlowMediaSource : IBroadcastMediaSource
    {
        public string SourceId => "slow";

        public SourceStatus Status => new(SourceHealth.Healthy);

        public IngestDiagnostics Diagnostics => new(SourceId, SourceHealth.Healthy, null, 0, 0, null, 0, 0, 0);

        public async IAsyncEnumerable<MediaChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new MediaChunk(new byte[10], TimeSpan.Zero);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingMediaSource : IBroadcastMediaSource
    {
        public string SourceId => "throwing";

        public SourceStatus Status => new(SourceHealth.Healthy);

        public IngestDiagnostics Diagnostics => new(SourceId, SourceHealth.Healthy, null, 0, 0, null, 0, 0, 0);

#pragma warning disable CS1998 // intentionally synchronous throw inside an async iterator
        public async IAsyncEnumerable<MediaChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
#pragma warning restore CS1998
        {
            throw new InvalidOperationException("simulated source failure");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
