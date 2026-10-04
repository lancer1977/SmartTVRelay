using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartTVRelay.Viewer;

namespace SmartTVRelay.Viewer.Tests;

public sealed class ViewerTests : IDisposable
{
    private readonly ViewerFactory _f = new();
    private readonly HttpClient _c;

    public ViewerTests() => _c = _f.CreateClient();

    public void Dispose() { _c.Dispose(); _f.Dispose(); }

    [Fact]
    public void Options_DefaultsBind()
    {
        var o = new ViewerOptions();
        Assert.Equal(45, o.StartupTimeoutSeconds);
        Assert.Equal("5M", o.ProbeSize);
        Assert.Equal(2_000_000, o.AnalyzeDurationUs);
        Assert.Equal(1, _f.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ViewerOptions>>().Value.StartupTimeoutSeconds);
    }

    [Fact]
    public async Task Healthz_IsProcessLivenessWithoutStartingTuner()
    {
        var response = await _c.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Empty(_f.Tuner.Requests);
        Assert.Empty(_f.Runner.Started);
    }

    [Fact]
    public async Task Channels_MapsLineupToCamelCaseContract()
    {
        var res = await _c.GetAsync("/api/channels");
        res.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3, json.GetArrayLength());
        var first = json[0];
        Assert.Equal("2.1", first.GetProperty("guideNumber").GetString());
        Assert.Equal("WDTN HD", first.GetProperty("name").GetString());
        Assert.Equal("MPEG2", first.GetProperty("videoCodec").GetString());
        Assert.Equal("http://tuner.test/lineup.json", _f.Tuner.Requests[0].ToString());
    }

    [Fact]
    public async Task FirstPlaylistRequest_StartsPipelineWithTunerStreamUrl()
    {
        var res = await _c.GetAsync("/hls/2.1/index.m3u8");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("#EXTM3U", await res.Content.ReadAsStringAsync());
        var started = Assert.Single(_f.Runner.Started);
        Assert.Equal("http://tuner.test:5004/auto/v2.1", started.Info.InputUrl);
        Assert.StartsWith(_f.WorkDir, started.Info.OutputDirectory);
        var registry = _f.Services.GetRequiredService<IChannelPipelineRegistry>();
        Assert.Equal(["2.1"], registry.RunningChannels);
        Assert.Equal(started.Info.OutputDirectory, registry.GetWorkDirectory("2.1"));
    }

    [Fact]
    public async Task SecondRequest_ReusesPipelineAndServesSegments()
    {
        await _c.GetAsync("/hls/2.1/index.m3u8");
        var again = await _c.GetAsync("/hls/2.1/index.m3u8");
        var seg = await _c.GetAsync("/hls/2.1/seg00000.ts");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.OK, seg.StatusCode);
        Assert.Single(_f.Runner.Started);
    }

    [Fact]
    public async Task RawCaptureFiles_AreNotServedAsHlsSegments()
    {
        await _c.GetAsync("/hls/2.1/index.m3u8");
        var dir = Assert.Single(_f.Runner.Started).Info.OutputDirectory;
        File.WriteAllBytes(Path.Combine(dir, "raw-000001.ts"), [0x47]);

        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync("/hls/2.1/raw-000001.ts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/hls/2.1/seg00000.ts")).StatusCode);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(dir));
    }

    [Fact]
    public async Task ThirdChannel_Returns503WithMessage()
    {
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/hls/2.1/index.m3u8")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/hls/7.1/index.m3u8")).StatusCode);
        var res = await _c.GetAsync("/hls/22.1/index.m3u8");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains("in use", await res.Content.ReadAsStringAsync());
        Assert.Equal(2, _f.Runner.Started.Count);
    }

    [Fact]
    public async Task UnknownChannel_Returns404_AndStartsNothing()
    {
        var res = await _c.GetAsync("/hls/99.9/index.m3u8");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Empty(_f.Runner.Started);
    }

    [Theory]
    [InlineData("/hls/..%2f..%2fetc/index.m3u8")]
    [InlineData("/hls/2.1/..%2f..%2fsecret.ts")]
    [InlineData("/hls/abc/index.m3u8")]
    [InlineData("/hls/2.1/notes.txt")]
    public async Task MaliciousPaths_AreRejected(string url)
    {
        var res = await _c.GetAsync(url);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Empty(_f.Runner.Started);
    }

    [Fact]
    public async Task PlaylistThatNeverAppears_Returns504()
    {
        _f.Runner.WritePlaylist = false;
        var res = await _c.GetAsync("/hls/2.1/index.m3u8");
        Assert.Equal(HttpStatusCode.GatewayTimeout, res.StatusCode);
    }

    [Fact]
    public async Task IdleReaper_StopsAndCleansIdlePipelinesOnly()
    {
        await _c.GetAsync("/hls/2.1/index.m3u8");
        await _c.GetAsync("/hls/7.1/index.m3u8");
        var manager = _f.Services.GetRequiredService<ChannelPipelineManager>();

        _f.Time.Advance(TimeSpan.FromSeconds(20));
        await _c.GetAsync("/hls/7.1/seg00000.ts"); // keeps 7.1 alive
        _f.Time.Advance(TimeSpan.FromSeconds(15));

        Assert.Equal(1, manager.ReapIdle());
        var two = _f.Runner.Started[0];
        var seven = _f.Runner.Started[1];
        Assert.Equal(1, two.Process.KillCount);
        Assert.False(Directory.Exists(two.Info.OutputDirectory));
        Assert.Equal(0, seven.Process.KillCount);
        Assert.True(Directory.Exists(seven.Info.OutputDirectory));
        Assert.Equal(["7.1"], manager.RunningChannels);

        // freed slot lets a new channel start
        Assert.Equal(HttpStatusCode.OK, (await _c.GetAsync("/hls/22.1/index.m3u8")).StatusCode);
    }

    [Fact]
    public async Task Shutdown_KillsAllProcessesAndDeletesFiles()
    {
        await _c.GetAsync("/hls/2.1/index.m3u8");
        await _c.GetAsync("/hls/7.1/index.m3u8");
        var started = _f.Runner.Started.ToList();

        await _f.DisposeAsync();

        Assert.All(started, s =>
        {
            Assert.True(s.Process.KillCount >= 1);
            Assert.False(Directory.Exists(s.Info.OutputDirectory));
        });
    }
}
