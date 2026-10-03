using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartTVRelay.Viewer;

namespace SmartTVRelay.Viewer.Tests;

public sealed class FakeProcess(string dir) : IFfmpegProcess
{
    public bool HasExited { get; private set; }
    public int KillCount { get; private set; }
    public string Dir { get; } = dir;
    public void Kill() { KillCount++; HasExited = true; }
    public void Dispose() { }
}

public sealed class FakeRunner : IFfmpegRunner
{
    public List<(FfmpegStartInfo Info, FakeProcess Process)> Started { get; } = new();
    public bool WritePlaylist { get; set; } = true;

    public IFfmpegProcess Start(FfmpegStartInfo info)
    {
        var p = new FakeProcess(info.OutputDirectory);
        lock (Started) Started.Add((info, p));
        if (WritePlaylist)
        {
            File.WriteAllText(info.PlaylistPath, "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:4.0,\nseg00000.ts\n");
            File.WriteAllBytes(Path.Combine(info.OutputDirectory, "seg00000.ts"), [0x47, 0, 0, 0]);
        }
        return p;
    }
}

public sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class FakeTunerHandler : HttpMessageHandler
{
    public const string Lineup = """
        [{"GuideNumber":"2.1","GuideName":"WDTN HD","VideoCodec":"MPEG2","HD":1,"URL":"http://x/auto/v2.1"},
         {"GuideNumber":"7.1","GuideName":"WHIO","VideoCodec":"MPEG2","URL":"http://x/auto/v7.1"},
         {"GuideNumber":"22.1","GuideName":"WKEF","VideoCodec":"H264","URL":"http://x/auto/v22.1"}]
        """;
    public List<Uri> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Lineup, Encoding.UTF8, "application/json"),
        });
    }
}

public sealed class ViewerFactory : WebApplicationFactory<Program>
{
    public FakeRunner Runner { get; } = new();
    public FakeTime Time { get; } = new();
    public FakeTunerHandler Tuner { get; } = new();
    public string WorkDir { get; } = Path.Combine(AppContext.BaseDirectory, "work-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Viewer:WorkDir"] = WorkDir,
            ["Viewer:MaxChannels"] = "2",
            ["Viewer:IdleSeconds"] = "30",
            ["Viewer:StartupTimeoutSeconds"] = "1",
            ["Tuner:BaseUrl"] = "http://tuner.test",
        }));
        builder.ConfigureServices(s =>
        {
            s.AddSingleton<IFfmpegRunner>(Runner);
            s.AddSingleton<TimeProvider>(Time);
            s.AddHttpClient<ITunerLineup, HttpTunerLineup>().ConfigurePrimaryHttpMessageHandler(() => Tuner);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(WorkDir)) Directory.Delete(WorkDir, true); } catch { }
    }
}
