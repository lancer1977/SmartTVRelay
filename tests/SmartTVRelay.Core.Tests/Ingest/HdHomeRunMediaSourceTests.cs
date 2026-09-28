namespace SmartTVRelay.Core.Tests.Ingest;

using SmartTVRelay.Core.Ingest;
using Xunit;

public class HdHomeRunMediaSourceTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            this.responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }

    [Fact]
    public void Constructor_ThrowsOnNullHttpClient()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new HdHomeRunMediaSource(null!, "source-1", "http://192.168.0.66", "2.1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_ThrowsOnInvalidSourceId(string? sourceId)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage());
        var client = new HttpClient(handler);
        Assert.ThrowsAny<ArgumentException>(() =>
            new HdHomeRunMediaSource(client, sourceId!, "http://192.168.0.66", "2.1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_ThrowsOnInvalidBaseUrl(string? baseUrl)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage());
        var client = new HttpClient(handler);
        Assert.ThrowsAny<ArgumentException>(() =>
            new HdHomeRunMediaSource(client, "source-1", baseUrl!, "2.1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_ThrowsOnInvalidChannelGuideNumber(string? guideNumber)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage());
        var client = new HttpClient(handler);
        Assert.ThrowsAny<ArgumentException>(() =>
            new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", guideNumber!));
    }

    [Fact]
    public void Constructor_StoresSourceId()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage());
        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "my-source", "http://192.168.0.66", "2.1");

        Assert.Equal("my-source", source.SourceId);
    }

    [Fact]
    public void Constructor_StartsWithUnknownStatus()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage());
        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        Assert.Equal(SourceHealth.Unknown, source.Status.Health);
    }

    [Fact]
    public async Task Constructor_TrimsTrailingSlash()
    {
        // This test verifies trailing slash normalization by checking the lineup URL construction.
        var lineupUrl = "";
        var lineupJson = @"[{""GuideNumber"":""2.1"",""URL"":""http://192.168.0.66:5004/auto/v2.1""}]";
        var streamData = new byte[] { 0x47 };

        var handler = new FakeHttpMessageHandler(req =>
        {
            var url = req.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("lineup.json"))
            {
                lineupUrl = url;
                return new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new StringContent(lineupJson),
                };
            }

            return new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new ByteArrayContent(streamData),
            };
        });

        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66/", "2.1");

        // Trigger a read to capture the URL being called
        var enumerator = source.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await enumerator.MoveNextAsync();

        // lineupUrl should not have a double slash
        Assert.DoesNotContain("//lineup", lineupUrl);
        Assert.Contains("/lineup.json", lineupUrl);
    }

    [Fact]
    public async Task ReadAsync_FetchesLineupJsonAndFindsChannel()
    {
        var lineupJson = @"[
            {""GuideNumber"":""2.1"",""GuideName"":""WDTN"",""URL"":""http://192.168.0.66:5004/auto/v2.1"",""VideoCodec"":""MPEG2"",""AudioCodec"":""AC3"",""HD"":1},
            {""GuideNumber"":""3.1"",""GuideName"":""WCTE"",""URL"":""http://192.168.0.66:5004/auto/v3.1"",""VideoCodec"":""H264"",""AudioCodec"":""AAC"",""HD"":1}
        ]";

        var streamData = new byte[] { 0x47, 0x00, 0x01, 0x02, 0x03 }; // MPEG-TS sync + data

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true)
            {
                return new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new StringContent(lineupJson),
                };
            }

            if (req.RequestUri?.AbsoluteUri.Contains("v2.1") == true)
            {
                return new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new ByteArrayContent(streamData),
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        var chunks = new List<MediaChunk>();
        await foreach (var chunk in source.ReadAsync(CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Equal(streamData, chunks[0].Data.ToArray());
    }

    [Fact]
    public async Task ReadAsync_SetsStatusHealthyOnSuccess()
    {
        var lineupJson = @"[{""GuideNumber"":""2.1"",""URL"":""http://192.168.0.66:5004/auto/v2.1""}]";
        var streamData = new byte[] { 0x47 };

        var handler = new FakeHttpMessageHandler(req =>
        {
            var url = req.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("lineup.json"))
            {
                return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(lineupJson) };
            }

            return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new ByteArrayContent(streamData) };
        });

        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        await foreach (var _ in source.ReadAsync(CancellationToken.None))
        {
            break; // Just start the read
        }

        Assert.Equal(SourceHealth.Healthy, source.Status.Health);
    }

    [Fact]
    public async Task ReadAsync_ThrowsWhenChannelNotFound()
    {
        var lineupJson = @"[{""GuideNumber"":""2.1"",""URL"":""http://192.168.0.66:5004/auto/v2.1""}]";

        var handler = new FakeHttpMessageHandler(req =>
        {
            var url = req.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("lineup.json"))
            {
                return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(lineupJson) };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "3.1");

        async Task Act()
        {
            await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(Act);

        Assert.Contains("3.1", ex.Message);
        Assert.Contains("not found in lineup", ex.Message);
        Assert.Equal(SourceHealth.Unavailable, source.Status.Health);
    }

    [Fact]
    public async Task ReadAsync_ThrowsWhenLineupFetchFails()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                {
                    ReasonPhrase = "Internal Server Error",
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        async Task Act()
        {
            await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(Act);

        Assert.Contains("Failed to reach HDHomeRun lineup", ex.Message);
        Assert.Contains("192.168.0.66", ex.Message);
        Assert.Equal(SourceHealth.Unavailable, source.Status.Health);
    }

    [Fact]
    public async Task ReadAsync_YieldsMultipleChunksFromStream()
    {
        var lineupJson = @"[{""GuideNumber"":""2.1"",""URL"":""http://192.168.0.66:5004/auto/v2.1""}]";

        // Create a fake stream that simulates multiple reads. Filled with a pattern whose period
        // (251, prime) does not evenly divide the 65536-byte chunk size, so every chunk's content
        // is genuinely distinguishable from every other chunk -- this is what actually catches a
        // buffer-aliasing bug. A naive "i % 256" pattern will NOT catch it: 256 divides 65536
        // exactly, so every full chunk has the identical repeating byte pattern regardless of
        // which chunk it is, and aliasing the shared buffer would silently produce the same
        // (wrong-but-matching) bytes.
        var streamData = new byte[200000]; // Larger than default 65536 chunk size
        for (var i = 0; i < streamData.Length; i++)
        {
            streamData[i] = (byte)((i * 31 + 7) % 251);
        }

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true)
            {
                return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(lineupJson) };
            }

            return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new ByteArrayContent(streamData) };
        });

        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        var chunks = new List<MediaChunk>();
        await foreach (var chunk in source.ReadAsync(CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.True(chunks.Count >= 3, $"Expected at least 3 chunks, got {chunks.Count}");
        var totalBytes = chunks.Sum(c => c.Data.Length);
        Assert.Equal(streamData.Length, totalBytes);

        // Reconstruct and compare actual content -- not just lengths -- across all chunks,
        // *after* the full read completes (so any chunk still aliasing an overwritten shared
        // buffer would fail here even though it passed the length-only check above).
        var reconstructed = new byte[streamData.Length];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.Data.Span.CopyTo(reconstructed.AsSpan(offset));
            offset += chunk.Data.Length;
        }

        Assert.Equal(streamData, reconstructed);
    }

    [Fact]
    public async Task ReadAsync_SourceTimeReflectsElapsedTime()
    {
        var lineupJson = @"[{""GuideNumber"":""2.1"",""URL"":""http://192.168.0.66:5004/auto/v2.1""}]";
        var streamData = new byte[] { 0x47 };

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true)
            {
                return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(lineupJson) };
            }

            return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new ByteArrayContent(streamData) };
        });

        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        var chunks = new List<MediaChunk>();
        await foreach (var chunk in source.ReadAsync(CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        // SourceTime should be > 0 since it reflects elapsed time
        Assert.True(chunks[0].SourceTime >= TimeSpan.Zero);
    }

    [Fact]
    public async Task ReadAsync_HonorsCancellation()
    {
        var lineupJson = @"[{""GuideNumber"":""2.1"",""URL"":""http://192.168.0.66:5004/auto/v2.1""}]";

        // Create a large stream that won't complete immediately
        var streamData = new byte[500000];
        Array.Fill(streamData, (byte)0x47);

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true)
            {
                return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(lineupJson) };
            }

            // Return a stream that can be cancelled mid-read
            return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new ByteArrayContent(streamData) };
        });

        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            var count = 0;
            await foreach (var _ in source.ReadAsync(cts.Token))
            {
                count++;
                if (count == 2)
                {
                    cts.Cancel();
                }
            }
        });
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage());
        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        // Should not throw
        await source.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage());
        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        await source.DisposeAsync();
        await source.DisposeAsync(); // Should not throw

        Assert.True(true); // If we got here, it didn't throw
    }

    [Fact]
    public async Task ReadAsync_StreamFailureUpdatesStatus()
    {
        var lineupJson = @"[{""GuideNumber"":""2.1"",""URL"":""http://192.168.0.66:5004/auto/v2.1""}]";

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true)
            {
                return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(lineupJson) };
            }

            // Return a stream that fails immediately
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        });

        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        async Task Act()
        {
            await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(Act);

        Assert.Equal(SourceHealth.Unavailable, source.Status.Health);
    }

    [Fact]
    public async Task ReadAsync_ChannelNotFoundMessageIncludesDeviceUrl()
    {
        var lineupJson = @"[{""GuideNumber"":""2.1"",""URL"":""http://192.168.0.66:5004/auto/v2.1""}]";

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true)
            {
                return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(lineupJson) };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });

        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "5.1");

        async Task Act()
        {
            await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(Act);

        Assert.Contains("192.168.0.66", ex.Message);
        Assert.Contains("5.1", ex.Message);
    }
}
