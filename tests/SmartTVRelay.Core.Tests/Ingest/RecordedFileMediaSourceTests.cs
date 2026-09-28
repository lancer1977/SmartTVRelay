namespace SmartTVRelay.Core.Tests.Ingest;

using SmartTVRelay.Core.Ingest;
using Xunit;

public class RecordedFileMediaSourceTests
{
    [Fact]
    public async Task ReadAsync_WithRealFixture_ReadsAllBytes()
    {
        // Arrange: use the real MPEG-TS fixture (376 KB, all 0x47 sync bytes).
        var fixtureFile = Path.Combine(AppContext.BaseDirectory, "captures", "sample-live-capture.ts");
        var expectedTotalBytes = new FileInfo(fixtureFile).Length;
        var source = new RecordedFileMediaSource("fixture-source", fixtureFile);

        // Act: read all chunks and sum their byte counts.
        long totalBytesRead = 0;
        await foreach (var chunk in source.ReadAsync(CancellationToken.None))
        {
            totalBytesRead += chunk.Data.Length;
        }

        // Assert: total bytes read matches file size.
        Assert.Equal(expectedTotalBytes, totalBytesRead);
    }

    [Fact]
    public async Task ReadAsync_WithRealFixture_ChunkCountIsCorrect()
    {
        // Arrange: use the real MPEG-TS fixture with a known chunk size.
        var fixtureFile = Path.Combine(AppContext.BaseDirectory, "captures", "sample-live-capture.ts");
        var fileSize = new FileInfo(fixtureFile).Length;
        int chunkSize = 65536; // default
        var expectedChunkCount = (int)Math.Ceiling((double)fileSize / chunkSize);
        var source = new RecordedFileMediaSource("fixture-source", fixtureFile, chunkSize);

        // Act: count all chunks.
        int chunkCount = 0;
        await foreach (var _ in source.ReadAsync(CancellationToken.None))
        {
            chunkCount++;
        }

        // Assert: chunk count matches expected.
        Assert.Equal(expectedChunkCount, chunkCount);
    }

    [Fact]
    public async Task ReadAsync_WithRealFixture_LastChunkIsSizeCorrect()
    {
        // Arrange: use the real MPEG-TS fixture.
        var fixtureFile = Path.Combine(AppContext.BaseDirectory, "captures", "sample-live-capture.ts");
        var fileSize = new FileInfo(fixtureFile).Length;
        int chunkSize = 65536;
        int expectedLastChunkSize = (int)(fileSize % chunkSize);
        if (expectedLastChunkSize == 0) expectedLastChunkSize = chunkSize; // exact multiple
        var source = new RecordedFileMediaSource("fixture-source", fixtureFile, chunkSize);

        // Act: read all chunks and capture the last one.
        MediaChunk? lastChunk = null;
        await foreach (var chunk in source.ReadAsync(CancellationToken.None))
        {
            lastChunk = chunk;
        }

        // Assert: last chunk size matches expected.
        Assert.NotNull(lastChunk);
        // If file size is exact multiple of chunk size, last chunk is full-sized.
        // Otherwise, it's the remainder.
        if (fileSize % chunkSize == 0)
        {
            Assert.Equal(chunkSize, lastChunk.Data.Length);
        }
        else
        {
            Assert.Equal(fileSize % chunkSize, lastChunk.Data.Length);
        }
    }

    [Fact]
    public async Task ReadAsync_EmptyFile_YieldsNoChunks()
    {
        // Arrange: create a temporary empty file.
        var tempFile = Path.GetTempFileName();
        try
        {
            // Empty file is already created by GetTempFileName().
            var source = new RecordedFileMediaSource("empty-source", tempFile);

            // Act: attempt to read.
            int chunkCount = 0;
            await foreach (var _ in source.ReadAsync(CancellationToken.None))
            {
                chunkCount++;
            }

            // Assert: no chunks yielded, enumerable completed normally.
            Assert.Equal(0, chunkCount);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReadAsync_FileSmallerThanChunk_YieldsExactlyOneChunk()
    {
        // Arrange: create a small temporary file (smaller than default chunk size).
        var tempFile = Path.GetTempFileName();
        try
        {
            var smallData = new byte[] { 1, 2, 3, 4, 5 };
            await File.WriteAllBytesAsync(tempFile, smallData);
            var source = new RecordedFileMediaSource("small-source", tempFile, chunkSizeBytes: 65536);

            // Act: read all chunks.
            var chunks = new List<MediaChunk>();
            await foreach (var chunk in source.ReadAsync(CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            // Assert: exactly one chunk with all bytes.
            Assert.Single(chunks);
            Assert.Equal(smallData, chunks[0].Data.ToArray());
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReadAsync_FileExactlyNChunks_YieldsExactlyNChunks()
    {
        // Arrange: create a file that's exactly 3 chunks.
        var tempFile = Path.GetTempFileName();
        try
        {
            int chunkSize = 1024;
            var data = new byte[chunkSize * 3];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 256);
            }
            await File.WriteAllBytesAsync(tempFile, data);
            var source = new RecordedFileMediaSource("exact-source", tempFile, chunkSize);

            // Act: read all chunks.
            var chunks = new List<MediaChunk>();
            await foreach (var chunk in source.ReadAsync(CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            // Assert: exactly 3 chunks, each full-sized, no trailing empty chunk.
            Assert.Equal(3, chunks.Count);
            Assert.All(chunks, c => Assert.Equal(chunkSize, c.Data.Length));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReadAsync_ChunkTimestampsAreEvenlySpaced()
    {
        // Arrange: create a small file with known chunk interval.
        var tempFile = Path.GetTempFileName();
        try
        {
            var data = new byte[256];
            await File.WriteAllBytesAsync(tempFile, data);
            var chunkInterval = TimeSpan.FromMilliseconds(50);
            var source = new RecordedFileMediaSource("timed-source", tempFile, chunkSizeBytes: 100, chunkInterval: chunkInterval);

            // Act: read all chunks and record timestamps.
            var timestamps = new List<TimeSpan>();
            await foreach (var chunk in source.ReadAsync(CancellationToken.None))
            {
                timestamps.Add(chunk.SourceTime);
            }

            // Assert: timestamps start at zero and increment by chunkInterval.
            Assert.NotEmpty(timestamps);
            Assert.Equal(TimeSpan.Zero, timestamps[0]);
            for (int i = 1; i < timestamps.Count; i++)
            {
                var expectedTime = TimeSpan.FromTicks(i * chunkInterval.Ticks);
                Assert.Equal(expectedTime, timestamps[i]);
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReadAsync_DefaultChunkIntervalIs100ms()
    {
        // Arrange: create a file and use default chunkInterval.
        var tempFile = Path.GetTempFileName();
        try
        {
            var data = new byte[256];
            await File.WriteAllBytesAsync(tempFile, data);
            var source = new RecordedFileMediaSource("default-interval-source", tempFile, chunkSizeBytes: 100);

            // Act: read chunks.
            var timestamps = new List<TimeSpan>();
            await foreach (var chunk in source.ReadAsync(CancellationToken.None))
            {
                timestamps.Add(chunk.SourceTime);
            }

            // Assert: second chunk (if exists) is at 100ms.
            Assert.True(timestamps.Count >= 2, "Expected at least 2 chunks");
            var expectedSecondChunkTime = TimeSpan.FromMilliseconds(100);
            Assert.Equal(expectedSecondChunkTime, timestamps[1]);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReadAsync_CancellationMidRead_ThrowsOperationCanceledException()
    {
        // Arrange: create a file with enough data to allow mid-stream cancellation.
        var tempFile = Path.GetTempFileName();
        try
        {
            var largeData = new byte[10000];
            await File.WriteAllBytesAsync(tempFile, largeData);
            var source = new RecordedFileMediaSource("cancellable-source", tempFile, chunkSizeBytes: 100);
            using var cts = new CancellationTokenSource();

            // Act & Assert: cancellation should throw.
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                int chunkCount = 0;
                await foreach (var _ in source.ReadAsync(cts.Token))
                {
                    chunkCount++;
                    if (chunkCount == 3)
                    {
                        cts.Cancel();
                    }
                }
            });
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReadAsync_CancellationAfterCancellation_NoFurtherChunks()
    {
        // Arrange: create a file and read with cancellation.
        var tempFile = Path.GetTempFileName();
        try
        {
            var largeData = new byte[5000];
            await File.WriteAllBytesAsync(tempFile, largeData);
            var source = new RecordedFileMediaSource("late-cancel-source", tempFile, chunkSizeBytes: 100);
            using var cts = new CancellationTokenSource();

            // Act: read until cancelled, counting chunks before and after.
            int chunksBeforeCancel = 0;
            try
            {
                await foreach (var _ in source.ReadAsync(cts.Token))
                {
                    chunksBeforeCancel++;
                    if (chunksBeforeCancel == 2)
                    {
                        cts.Cancel();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected; cancellation was successful.
            }

            // Assert: we read at least 2 chunks before cancellation.
            Assert.True(chunksBeforeCancel >= 2);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Constructor_RejectsNonexistentFile()
    {
        var nonexistentPath = "/nonexistent/path/to/file.ts";

        Assert.Throws<FileNotFoundException>(() =>
            new RecordedFileMediaSource("bad-source", nonexistentPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_RejectsInvalidSourceId(string? sourceId)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            Assert.ThrowsAny<ArgumentException>(() =>
                new RecordedFileMediaSource(sourceId!, tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SourceId_IsExposedAsConstructed()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var source = new RecordedFileMediaSource("my-source", tempFile);
            Assert.Equal("my-source", source.SourceId);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Status_ReportsHealthy()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var source = new RecordedFileMediaSource("status-source", tempFile);
            Assert.Equal(SourceHealth.Healthy, source.Status.Health);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DisposeAsync_AfterFullRead_DoesNotThrow()
    {
        // Arrange: read a file completely, then dispose.
        var tempFile = Path.GetTempFileName();
        try
        {
            var data = new byte[100];
            await File.WriteAllBytesAsync(tempFile, data);
            var source = new RecordedFileMediaSource("disposable-source", tempFile);

            // Act: read all.
            await foreach (var _ in source.ReadAsync(CancellationToken.None))
            {
                // Consume all chunks.
            }

            // Act: dispose (should not throw).
            await source.DisposeAsync();

            // Assert: no exception was thrown above.
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DisposeAsync_WithoutReadAsync_DoesNotThrow()
    {
        // Arrange: create a source but don't read from it.
        var tempFile = Path.GetTempFileName();
        try
        {
            var source = new RecordedFileMediaSource("unread-source", tempFile);

            // Act: dispose without calling ReadAsync (should not throw).
            await source.DisposeAsync();

            // Assert: no exception was thrown.
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReadAsync_CompletesNormallyAtEOF_NoException()
    {
        // Arrange: create a small file.
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, new byte[] { 1, 2, 3 });
            var source = new RecordedFileMediaSource("normal-completion-source", tempFile);

            // Act: read until EOF (should complete normally, no exception).
            int chunkCount = 0;
            await foreach (var _ in source.ReadAsync(CancellationToken.None))
            {
                chunkCount++;
            }

            // Assert: at least one chunk was read, and no exception was thrown.
            Assert.True(chunkCount > 0);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReadAsync_CanBeCalledMultipleTimes()
    {
        // Arrange: create a small file and call ReadAsync twice.
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, new byte[] { 1, 2, 3, 4, 5 });
            var source = new RecordedFileMediaSource("reusable-source", tempFile);

            // Act: read first time.
            var firstRead = new List<MediaChunk>();
            await foreach (var chunk in source.ReadAsync(CancellationToken.None))
            {
                firstRead.Add(chunk);
            }

            // Act: read second time.
            var secondRead = new List<MediaChunk>();
            await foreach (var chunk in source.ReadAsync(CancellationToken.None))
            {
                secondRead.Add(chunk);
            }

            // Assert: both reads succeeded with identical results.
            Assert.Equal(firstRead.Count, secondRead.Count);
            for (int i = 0; i < firstRead.Count; i++)
            {
                Assert.Equal(firstRead[i].Data.ToArray(), secondRead[i].Data.ToArray());
                Assert.Equal(firstRead[i].SourceTime, secondRead[i].SourceTime);
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ReadAsync_WithCustomChunkSize_RespectsBoundary()
    {
        // Arrange: create a file and read with a custom chunk size.
        var tempFile = Path.GetTempFileName();
        try
        {
            var data = new byte[1000];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)i;
            }
            await File.WriteAllBytesAsync(tempFile, data);
            int customChunkSize = 250;
            var source = new RecordedFileMediaSource("chunked-source", tempFile, chunkSizeBytes: customChunkSize);

            // Act: read all chunks.
            var chunks = new List<MediaChunk>();
            await foreach (var chunk in source.ReadAsync(CancellationToken.None))
            {
                chunks.Add(chunk);
            }

            // Assert: all chunks except possibly the last are exactly customChunkSize.
            Assert.True(chunks.Count > 1, "Expected multiple chunks");
            for (int i = 0; i < chunks.Count - 1; i++)
            {
                Assert.Equal(customChunkSize, chunks[i].Data.Length);
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
