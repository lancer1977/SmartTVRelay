namespace SmartTVRelay.Core.Tests.Evaluation;

using System.Net;
using System.Text;
using SmartTVRelay.Evaluation;
using Xunit;

[Collection("Subprocess")]
public class LiveSmokeTests
{
    private const string Lineup = """[{"GuideNumber":"2.1","GuideName":"Test HD","URL":"http://tuner.test:5004/auto/v2.1"}]""";

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private static EvaluationCliOptions Options(string channel = "2.1", int seconds = 5) =>
        EvaluationCliOptions.Parse(["live-smoke", "--host", "http://tuner.test", "--channel", channel, "--seconds", seconds.ToString(), "--output", Path.Combine(Path.GetTempPath(), "live-smoke-test-" + Guid.NewGuid().ToString("N"))]);

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) => new(new FakeHandler(responder));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task RunAsync_WithRealCapture_PassesAndReportsVideo()
    {
        var capture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "captures", "sample-live-capture.ts"));
        using var client = Client(r => r.RequestUri!.AbsolutePath.EndsWith("lineup.json", StringComparison.Ordinal)
            ? Json(Lineup)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(capture) });

        var result = await new LiveSmokeRunner().RunAsync(Options(), client);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.VideoFound);
        Assert.True(result.Diagnostics.BytesProcessed > 0);
        Assert.Contains("Live smoke: PASS", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_EmptyStream_FailsWithoutThrowing()
    {
        using var client = Client(r => r.RequestUri!.AbsolutePath.EndsWith("lineup.json", StringComparison.Ordinal)
            ? Json(Lineup)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });

        var result = await new LiveSmokeRunner().RunAsync(Options(), client);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("FAIL", result.Summary, StringComparison.Ordinal);
        Assert.False(result.VideoFound);
    }

    [Fact]
    public async Task RunAsync_UnreachableHost_FailsWithReason()
    {
        using var client = Client(_ => throw new HttpRequestException("connection refused"));

        var result = await new LiveSmokeRunner().RunAsync(Options(), client);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("connection refused", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ChannelNotInLineup_FailsWithReason()
    {
        using var client = Client(_ => Json(Lineup));

        var result = await new LiveSmokeRunner().RunAsync(Options(channel: "99.9"), client);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("99.9", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_LiveSmoke_ReadsOptions()
    {
        var o = EvaluationCliOptions.Parse(["live-smoke", "--host", "http://h", "--channel", "2.1", "--seconds", "20"]);

        Assert.Equal("live-smoke", o.Mode);
        Assert.Equal("http://h", o.Host);
        Assert.Equal("2.1", o.Channel);
        Assert.Equal(20, o.Seconds);
    }

    [Theory]
    [InlineData("--channel", "2.1", "--seconds", "5")]
    [InlineData("--host", "http://h", "--seconds", "5")]
    [InlineData("--host", "http://h", "--channel", "2.1")]
    [InlineData("--host", "http://h", "--channel", "2.1", "--seconds", "0")]
    public void Parse_LiveSmoke_MissingOrInvalidOptions_Throws(params string[] args)
        => Assert.Throws<ArgumentException>(() => EvaluationCliOptions.Parse(["live-smoke", .. args]));

    [Fact]
    public void Parse_DefaultMode_IsEvaluation()
        => Assert.Equal("evaluation", EvaluationCliOptions.Parse([]).Mode);
}
