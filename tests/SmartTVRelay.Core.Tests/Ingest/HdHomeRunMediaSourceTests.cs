namespace SmartTVRelay.Core.Tests.Ingest;

using SmartTVRelay.Core.Ingest;
using Xunit;

public class HdHomeRunMediaSourceTests
{
    [Fact]
    public async Task ReadAsync_EarlyEnumerationDisposalReleasesIncompleteAvailabilityCapture()
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = req.RequestUri!.AbsolutePath.EndsWith("lineup.json", StringComparison.Ordinal)
                ? new StringContent("[{\"GuideNumber\":\"2.1\",\"URL\":\"http://192.168.0.66:5004/auto/v2.1\"}]")
                : new ByteArrayContent(packet),
        });
        using var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-availability", "http://192.168.0.66", "2.1");

        await using (var enumerator = source.ReadAsync(CancellationToken.None).GetAsyncEnumerator())
            Assert.True(await enumerator.MoveNextAsync());

        var window = typeof(HdHomeRunMediaSource)
            .GetField("availabilityWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(source)!;
        var capture = window.GetType()
            .GetField("capture", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(window);
        Assert.Null(capture);
        Assert.Null(source.Diagnostics.CaptionsAvailable);
        Assert.Null(source.Diagnostics.MarkersAvailable);
    }

    [Theory]
    [InlineData("synthetic/scte35-sample.ts", true)]
    [InlineData("captures/sample-live-capture.ts", false)]
    public async Task Diagnostics_InspectsCompletedLiveTransportWindow(string fixture, bool markersPresent)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, fixture));
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = req.RequestUri!.AbsolutePath.EndsWith("lineup.json", StringComparison.Ordinal)
                ? new StringContent("[{\"GuideNumber\":\"2.1\",\"URL\":\"http://192.168.0.66:5004/auto/v2.1\"}]")
                : new ByteArrayContent(bytes),
        });
        using var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-availability", "http://192.168.0.66", "2.1");
        Assert.Null(source.Diagnostics.MarkersAvailable);

        await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }

        Assert.Equal(markersPresent, source.Diagnostics.MarkersAvailable);
    }

    [Fact]
    public async Task Diagnostics_IncompleteLiveTransportLeavesAvailabilityUnknown()
    {
        var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = req.RequestUri!.AbsolutePath.EndsWith("lineup.json", StringComparison.Ordinal)
                ? new StringContent("[{\"GuideNumber\":\"2.1\",\"URL\":\"http://192.168.0.66:5004/auto/v2.1\"}]")
                : new ByteArrayContent([0x47, 0x00, 0x01]),
        });
        using var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-availability", "http://192.168.0.66", "2.1");

        await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }

        Assert.Null(source.Diagnostics.CaptionsAvailable);
        Assert.Null(source.Diagnostics.MarkersAvailable);
    }

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
    public void Diagnostics_BeforeAnyRead_IsZeroed()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage());
        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        var diagnostics = source.Diagnostics;

        Assert.Equal("source-1", diagnostics.SourceId);
        Assert.Equal(SourceHealth.Unknown, diagnostics.Health);
        Assert.Equal(0, diagnostics.BytesProcessed);
        Assert.Equal(0, diagnostics.ChunksProcessed);
        Assert.Null(diagnostics.LastMediaTimestamp);
        Assert.Equal(0, diagnostics.ProbeErrorCount);
        Assert.Equal(0, diagnostics.DecodeErrorCount);
        Assert.Equal(0, diagnostics.ReconnectAttempts);
    }

    [Fact]
    public async Task Diagnostics_AfterSuccessfulRead_ReflectsBytesAndChunks()
    {
        var lineupJson = @"[{""GuideNumber"":""2.1"",""URL"":""http://192.168.0.66:5004/auto/v2.1""}]";
        var streamData = new byte[] { 0x47, 0x00, 0x01, 0x02, 0x03 };

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

        await foreach (var _ in source.ReadAsync(CancellationToken.None))
        {
        }

        var diagnostics = source.Diagnostics;

        Assert.Equal(streamData.Length, diagnostics.BytesProcessed);
        Assert.Equal(1, diagnostics.ChunksProcessed);
        Assert.NotNull(diagnostics.LastMediaTimestamp);
        Assert.Equal(0, diagnostics.ProbeErrorCount);
        Assert.Equal(0, diagnostics.DecodeErrorCount);
    }

    [Fact]
    public async Task Diagnostics_LineupFetchFailure_IncrementsProbeErrorCountAndIncludesSourceIdInDetail()
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
        var source = new HdHomeRunMediaSource(client, "correlation-source", "http://192.168.0.66", "2.1");

        async Task Act()
        {
            await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(Act);

        var diagnostics = source.Diagnostics;
        Assert.Equal(1, diagnostics.ProbeErrorCount);
        Assert.Equal(0, diagnostics.DecodeErrorCount);
        Assert.NotNull(diagnostics.Detail);
        Assert.Contains("correlation-source", diagnostics.Detail);
    }

    [Fact]
    public async Task ReadAsync_LineupTransportFailureDoesNotExposeEndpointSecret()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new HttpRequestException("Bearer test-secret"));
        var source = new HdHomeRunMediaSource(
            new HttpClient(handler), "source-1", "http://192.168.0.66/private?token=secret", "2.1");

        async Task Act()
        {
            await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(Act);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("?token=", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", source.Diagnostics.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_LineupReasonPhraseDoesNotExposeEndpointSecret()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway)
        {
            ReasonPhrase = "Bearer test-secret at http://device/stream?token=secret",
        });
        var source = new HdHomeRunMediaSource(new HttpClient(handler), "source-1", "http://192.168.0.66", "2.1");

        async Task Act()
        {
            await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(Act);
        Assert.Contains("HTTP 502", error.Message);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", source.Diagnostics.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadAsync_DisposesLineupResponseContent(bool success)
    {
        var content = new TrackingContent(success
            ? "[{\"GuideNumber\":\"2.1\",\"URL\":\"http://192.168.0.66:5004/auto/v2.1\"}]"
            : "unavailable");
        var handler = new FakeHttpMessageHandler(req =>
            req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true
                ? new HttpResponseMessage(success ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.BadGateway) { Content = content }
                : new HttpResponseMessage { Content = new ByteArrayContent(new byte[] { 0x47 }) });
        var source = new HdHomeRunMediaSource(new HttpClient(handler), "source-1", "http://192.168.0.66", "2.1");

        if (success)
        {
            await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }
            });
        }

        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Diagnostics_ChannelNotFound_IncrementsProbeErrorCount()
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

        await Assert.ThrowsAsync<InvalidOperationException>(Act);

        Assert.Equal(1, source.Diagnostics.ProbeErrorCount);
    }

    [Fact]
    public async Task Diagnostics_StreamOpenFailure_IncrementsProbeErrorCount()
    {
        var lineupJson = @"[{""GuideNumber"":""2.1"",""URL"":""http://192.168.0.66:5004/auto/v2.1""}]";

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true)
            {
                return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(lineupJson) };
            }

            throw new HttpRequestException("Connection refused for http://192.168.0.66:5004/auto/v2.1?token=secret");
        });

        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        async Task Act()
        {
            await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(Act);

        Assert.Equal(4, source.Diagnostics.ProbeErrorCount);
        Assert.Equal(0, source.Diagnostics.DecodeErrorCount);
    }

    [Fact]
    public async Task Diagnostics_StreamReadFailure_IncrementsDecodeErrorCountNotProbeErrorCount()
    {
        var lineupJson = @"[{""GuideNumber"":""2.1"",""URL"":""http://192.168.0.66:5004/auto/v2.1""}]";

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true)
            {
                return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(lineupJson) };
            }

            // A response whose content stream throws once read, simulating a mid-stream failure
            // after the connection was already successfully established.
            return new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new ThrowingContent(),
            };
        });

        var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        async Task Act()
        {
            await foreach (var _ in source.ReadAsync(CancellationToken.None)) { }
        }

        var terminal = await Assert.ThrowsAsync<InvalidOperationException>(Act);
        Assert.DoesNotContain("http", terminal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", terminal.Message, StringComparison.OrdinalIgnoreCase);

        var diagnostics = source.Diagnostics;
        Assert.Equal(0, diagnostics.ProbeErrorCount);
        Assert.Equal(4, diagnostics.DecodeErrorCount);
        Assert.Equal(3, diagnostics.ReconnectAttempts);
    }

    [Fact]
    public async Task ReadAsync_ReconnectsAfterInterruptionAndYieldsRecoveryData()
    {
        var lineupJson = "[{\"GuideNumber\":\"2.1\",\"URL\":\"http://192.168.0.66:5004/auto/v2.1?token=secret\"}]";
        var streamCalls = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true)
            {
                return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(lineupJson) };
            }

            streamCalls++;
            return new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = streamCalls == 1 ? new ThrowingContent() : new ByteArrayContent(new byte[] { 0x47, 0x01 }),
            };
        });

        var source = new HdHomeRunMediaSource(new HttpClient(handler), "source-1", "http://192.168.0.66", "2.1");
        var chunks = new List<MediaChunk>();
        await foreach (var chunk in source.ReadAsync(CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, streamCalls);
        Assert.Single(chunks);
        Assert.Equal(new byte[] { 0x47, 0x01 }, chunks[0].Data.ToArray());
        Assert.Equal(1, source.Diagnostics.ReconnectAttempts);
        Assert.DoesNotContain("token", source.Diagnostics.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", source.Diagnostics.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_ReconnectKeepsSourceTimelineMonotonic()
    {
        var lineupJson = "[{\"GuideNumber\":\"2.1\",\"URL\":\"http://192.168.0.66:5004/auto/v2.1\"}]";
        var streamCalls = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true)
                return new HttpResponseMessage { Content = new StringContent(lineupJson) };
            streamCalls++;
            return new HttpResponseMessage { Content = new ScriptedContent(new byte[] { (byte)streamCalls }, throwAfterData: streamCalls == 1) };
        });

        var source = new HdHomeRunMediaSource(new HttpClient(handler), "source-1", "http://192.168.0.66", "2.1");
        var chunks = new List<MediaChunk>();
        await foreach (var chunk in source.ReadAsync(CancellationToken.None))
        {
            chunks.Add(chunk);
            if (chunks.Count == 2) break;
        }

        Assert.Equal(2, chunks.Count);
        Assert.True(chunks[1].SourceTime > chunks[0].SourceTime);
    }

    [Fact]
    public async Task ReadAsync_CancellationStopsReconnectsWithoutAdditionalAttempt()
    {
        var lineupJson = "[{\"GuideNumber\":\"2.1\",\"URL\":\"http://192.168.0.66:5004/auto/v2.1\"}]";
        var streamCalls = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsoluteUri.Contains("lineup.json") == true)
            {
                return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(lineupJson) };
            }

            streamCalls++;
            return new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new ThrowingContent() };
        });

        var source = new HdHomeRunMediaSource(new HttpClient(handler), "source-1", "http://192.168.0.66", "2.1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in source.ReadAsync(cts.Token)) { }
        });

        Assert.Equal(0, streamCalls);
        Assert.Equal(0, source.Diagnostics.ReconnectAttempts);
    }

    [Fact]
    public async Task ReadAsync_CancellationDuringRetryDelayDoesNotCountUnstartedReconnect()
    {
        using var cts = new CancellationTokenSource();
        var streamCalls = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath.EndsWith("lineup.json", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("[{\"GuideNumber\":\"2.1\",\"URL\":\"http://192.168.0.66:5004/auto/v2.1\"}]"),
                };

            streamCalls++;
            cts.CancelAfter(TimeSpan.FromMilliseconds(10));
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        });
        using var client = new HttpClient(handler);
        var source = new HdHomeRunMediaSource(client, "source-1", "http://192.168.0.66", "2.1");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in source.ReadAsync(cts.Token)) { }
        });

        Assert.Equal(1, streamCalls);
        Assert.Equal(0, source.Diagnostics.ReconnectAttempts);
    }

    /// <summary>HttpContent whose stream throws on the first read, after headers/connection succeed --
    /// simulating a decode-time failure distinct from a probe/connect-time failure.</summary>
    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new IOException("Simulated mid-stream read failure");

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ThrowingStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class ThrowingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Simulated mid-stream read failure");
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                throw new IOException("Simulated mid-stream read failure");
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class ScriptedContent(byte[] data, bool throwAfterData) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ScriptedStream(data, throwAfterData));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }

        private sealed class ScriptedStream(byte[] data, bool throwAfterData) : MemoryStream(data)
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await base.ReadAsync(buffer, cancellationToken);
                if (read == 0 && throwAfterData) throw new IOException("interrupted");
                return read;
            }
        }
    }

    private sealed class TrackingContent(string value) : StringContent(value)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
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
