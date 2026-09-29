namespace SmartTVRelay.Core.Tests.Vlm;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SmartTVRelay.Core;
using SmartTVRelay.Core.Vlm;
using Xunit;

/// <summary>
/// Exercises <see cref="OllamaLocalVlmClient"/> entirely against a fake <see cref="HttpMessageHandler"/>
/// -- no live Ollama server or network access, per AGENTS.md's hardware/live-dependency rule.
/// </summary>
public class OllamaLocalVlmClientTests
{
    private static readonly byte[] TinyImage = [0xFF, 0xD8, 0xFF, 0xD9];

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await _respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage OllamaEnvelope(string modelJson) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { response = modelJson }), Encoding.UTF8, "application/json"),
        };

    [Fact]
    public void Constructor_NullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new OllamaLocalVlmClient(null!));
    }

    [Fact]
    public void Constructor_NonPositiveTimeout_Throws()
    {
        using var httpClient = new HttpClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaLocalVlmClient(httpClient, timeout: TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_NonPositiveMaxImageBytes_Throws()
    {
        using var httpClient = new HttpClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaLocalVlmClient(httpClient, maxImageBytes: 0));
    }

    [Fact]
    public async Task ClassifyAsync_ValidResponse_ReturnsParsedClassification()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(
            OllamaEnvelope("""{"state":"Commercial","confidence":0.87}""")));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var client = new OllamaLocalVlmClient(httpClient);

        var result = await client.ClassifyAsync(TinyImage, CancellationToken.None);

        Assert.Equal(BroadcastState.Commercial, result.PredictedState);
        Assert.Equal(0.87, result.Confidence);
        Assert.False(result.TimedOut);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ClassifyAsync_MalformedEnvelope_ReturnsUnknownWithError()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json", Encoding.UTF8, "application/json") }));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var client = new OllamaLocalVlmClient(httpClient);

        var result = await client.ClassifyAsync(TinyImage, CancellationToken.None);

        Assert.Equal(BroadcastState.Unknown, result.PredictedState);
        Assert.Equal(0.0, result.Confidence);
        Assert.False(result.TimedOut);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ClassifyAsync_MalformedModelJson_ReturnsUnknownWithError()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(OllamaEnvelope("not valid json at all")));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var client = new OllamaLocalVlmClient(httpClient);

        var result = await client.ClassifyAsync(TinyImage, CancellationToken.None);

        Assert.Equal(BroadcastState.Unknown, result.PredictedState);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ClassifyAsync_UnrecognizedStateValue_ReturnsUnknownWithError()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(
            OllamaEnvelope("""{"state":"Advertisement","confidence":0.9}""")));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var client = new OllamaLocalVlmClient(httpClient);

        var result = await client.ClassifyAsync(TinyImage, CancellationToken.None);

        Assert.Equal(BroadcastState.Unknown, result.PredictedState);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public async Task ClassifyAsync_ConfidenceOutOfRange_ReturnsUnknownWithError(double confidence)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(
            OllamaEnvelope($$"""{"state":"Program","confidence":{{confidence}}}""")));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var client = new OllamaLocalVlmClient(httpClient);

        var result = await client.ClassifyAsync(TinyImage, CancellationToken.None);

        Assert.Equal(BroadcastState.Unknown, result.PredictedState);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ClassifyAsync_NonSuccessStatus_ReturnsUnknownWithError()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var client = new OllamaLocalVlmClient(httpClient);

        var result = await client.ClassifyAsync(TinyImage, CancellationToken.None);

        Assert.Equal(BroadcastState.Unknown, result.PredictedState);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ClassifyAsync_HandlerThrows_ReturnsErrorInsteadOfPropagating()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("connection refused"));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var client = new OllamaLocalVlmClient(httpClient);

        var result = await client.ClassifyAsync(TinyImage, CancellationToken.None);

        Assert.Equal(BroadcastState.Unknown, result.PredictedState);
        Assert.False(result.TimedOut);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ClassifyAsync_ExceedsStrictTimeout_ReturnsTimedOutNotError()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return OllamaEnvelope("""{"state":"Program","confidence":0.9}""");
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var client = new OllamaLocalVlmClient(httpClient, timeout: TimeSpan.FromMilliseconds(50));

        var result = await client.ClassifyAsync(TinyImage, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(BroadcastState.Unknown, result.PredictedState);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ClassifyAsync_CallerCancellation_PropagatesAsOperationCanceled()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return OllamaEnvelope("""{"state":"Program","confidence":0.9}""");
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var client = new OllamaLocalVlmClient(httpClient, timeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ClassifyAsync(TinyImage, cts.Token));
    }

    [Fact]
    public async Task ClassifyAsync_ImageExceedsMaxSize_ReturnsErrorWithoutCallingNetwork()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(OllamaEnvelope("""{"state":"Program","confidence":0.9}""")));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var client = new OllamaLocalVlmClient(httpClient, maxImageBytes: 2);

        var result = await client.ClassifyAsync(TinyImage, CancellationToken.None);

        Assert.Equal(BroadcastState.Unknown, result.PredictedState);
        Assert.NotNull(result.Error);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ClassifyAsync_SendsBoundedRequestShape()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(OllamaEnvelope("""{"state":"Program","confidence":0.9}""")));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        var client = new OllamaLocalVlmClient(httpClient, model: "qwen2.5vl:7b");

        await client.ClassifyAsync(TinyImage, CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("qwen2.5vl:7b", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("json", doc.RootElement.GetProperty("format").GetString());
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32() <= 64);
        Assert.Single(doc.RootElement.GetProperty("images").EnumerateArray());
        Assert.Equal("/api/generate", handler.LastRequest!.RequestUri!.AbsolutePath);
    }
}
