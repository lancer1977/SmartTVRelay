using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Observation.Core;
using SmartTVRelay.Core;
using SmartTVRelay.Core.Fixtures;
using SmartTVRelay.Viewer.State;

namespace SmartTVRelay.Viewer.Tests;

/// <summary>Evidence source driven by synthetic fixtures (SyntheticFixtureGenerator emissions) at a chosen fixture time.</summary>
public sealed class FixtureEvidenceSource(TimeProvider time) : IChannelEvidenceSource
{
    private BroadcastFixture? _fixture;
    private TimeSpan _at;
    public int Calls;

    public void Play(BroadcastFixture fixture, TimeSpan at) { _fixture = fixture; _at = at; }
    public void Clear() => _fixture = null;

    public Task<IReadOnlyList<Observation<BroadcastState>>> GetObservationsAsync(string guideNumber, string workDirectory, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        var now = time.GetUtcNow();
        IReadOnlyList<Observation<BroadcastState>> result = _fixture is null
            ? Array.Empty<Observation<BroadcastState>>()
            : _fixture.Emissions.Where(e => e.At == _at).Select(e => new Observation<BroadcastState>(
                e.Id, ObservationScope.Of("fixture"), BroadcastObservations.Subject, BroadcastObservations.Predicate,
                e.Value, e.SourceId, e.SourceKind, e.Confidence, now)).ToArray();
        return Task.FromResult(result);
    }
}

public sealed class ChannelStateTests : IDisposable
{
    private readonly ViewerFactory _viewer = new();
    private readonly FixtureEvidenceSource _evidence;
    private readonly WebApplicationFactory _factory;

    private sealed record WebApplicationFactory(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Inner) : IDisposable
    {
        public void Dispose() => Inner.Dispose();
    }

    public ChannelStateTests()
    {
        _evidence = new FixtureEvidenceSource(_viewer.Time);
        _factory = new WebApplicationFactory(_viewer.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Viewer:State:PollMilliseconds"] = "20",
                ["Viewer:State:KeepAliveSeconds"] = "1",
            }));
            b.ConfigureServices(s => s.AddSingleton<IChannelEvidenceSource>(_evidence));
        }));
    }

    public void Dispose() { _factory.Dispose(); _viewer.Dispose(); }

    private async Task StartChannelAsync(HttpClient client, string guide = "2.1")
    {
        var r = await client.GetAsync($"/hls/{guide}/index.m3u8");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private static async Task<JsonElement> ReadEventAsync(StreamReader reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token) ?? throw new EndOfStreamException();
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                Assert.Equal("", await reader.ReadLineAsync(cts.Token)); // event terminator
                return JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone();
            }
        }
    }

    /// <summary>The monitor refreshes on a timer, so GET may briefly serve the previous snapshot.</summary>
    private static async Task<JsonElement> WaitForStateAsync(HttpClient client, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var json = JsonDocument.Parse(await client.GetStringAsync("/api/channels/2.1/state")).RootElement.Clone();
            if (json.GetProperty("state").GetString() == expected || DateTime.UtcNow > deadline) return json;
            await Task.Delay(20);
        }
    }

    private async Task<(HttpResponseMessage Response, StreamReader Reader)> OpenEventsAsync(HttpClient client, string guide = "2.1")
    {
        var response = await client.GetAsync($"/api/channels/{guide}/events", HttpCompletionOption.ResponseHeadersRead);
        return (response, new StreamReader(await response.Content.ReadAsStreamAsync()));
    }

    [Fact]
    public async Task State_for_channel_that_is_not_running_is_404()
    {
        var client = _factory.Inner.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/channels/2.1/state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/channels/2.1/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/channels/..%2Fx/state")).StatusCode);
    }

    [Fact]
    public async Task No_evidence_is_Unknown_with_low_confidence()
    {
        var client = _factory.Inner.CreateClient();
        await StartChannelAsync(client);
        _evidence.Clear();

        var r = await client.GetAsync("/api/channels/2.1/state");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("2.1", json.GetProperty("guideNumber").GetString());
        Assert.Equal("Unknown", json.GetProperty("state").GetString());
        Assert.True(json.GetProperty("confidence").GetDouble() <= 0.2);
        Assert.Equal(0, json.GetProperty("evidence").GetArrayLength());
        Assert.True(DateTimeOffset.TryParse(json.GetProperty("asOf").GetString(), out _));
    }

    [Fact]
    public async Task Clean_break_commercial_reports_Commercial_with_evidence()
    {
        var client = _factory.Inner.CreateClient();
        await StartChannelAsync(client);
        _evidence.Play(SyntheticFixtureGenerator.GenerateCleanBreak(42), TimeSpan.FromSeconds(30));

        var json = await WaitForStateAsync(client, "Commercial");
        Assert.Equal("Commercial", json.GetProperty("state").GetString());
        Assert.True(json.GetProperty("confidence").GetDouble() >= 0.75);
        var ev = json.GetProperty("evidence");
        Assert.Equal(2, ev.GetArrayLength());
        Assert.Equal("heuristic", ev[0].GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(ev[0].GetProperty("source").GetString()));
    }

    [Fact]
    public async Task Ambiguous_evidence_is_Unknown()
    {
        var client = _factory.Inner.CreateClient();
        await StartChannelAsync(client);
        // P 0.62 vs C 0.61 from two detectors at the transition boundary.
        _evidence.Play(SyntheticFixtureGenerator.GenerateAmbiguousTransition(42), TimeSpan.FromSeconds(10));

        await Task.Delay(100); // let the monitor re-evaluate with the ambiguous evidence
        var json = JsonDocument.Parse(await client.GetStringAsync("/api/channels/2.1/state")).RootElement;
        Assert.Equal("Unknown", json.GetProperty("state").GetString());
        Assert.True(json.GetProperty("confidence").GetDouble() <= 0.2);
        Assert.True(json.GetProperty("evidence").GetArrayLength() > 0); // provenance of the dispute is kept
    }

    [Fact]
    public async Task Stale_evidence_is_Unknown()
    {
        var client = _factory.Inner.CreateClient();
        await StartChannelAsync(client);
        _evidence.Play(SyntheticFixtureGenerator.GenerateCleanBreak(42), TimeSpan.FromSeconds(30));
        // Evidence is stamped with "now" at read time, so evaluate it against a clock that has moved on.
        var service = _factory.Inner.Services.GetRequiredService<ChannelStateService>();
        var observed = await _evidence.GetObservationsAsync("2.1", "", default);
        _viewer.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(BroadcastState.Unknown, service.Evaluate("2.1", observed).State);
    }

    [Fact]
    public async Task Sse_emits_initial_state_on_connect()
    {
        var client = _factory.Inner.CreateClient();
        await StartChannelAsync(client);
        _evidence.Play(SyntheticFixtureGenerator.GenerateCleanBreak(42), TimeSpan.Zero);
        await WaitForStateAsync(client, "Program");

        var (response, reader) = await OpenEventsAsync(client);
        using var _ = response;
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var first = await ReadEventAsync(reader, TimeSpan.FromSeconds(5));
        Assert.Equal("Program", first.GetProperty("state").GetString());
        Assert.Equal("2.1", first.GetProperty("guideNumber").GetString());
    }

    [Fact]
    public async Task Sse_emits_Program_to_Commercial_transition_for_clean_break()
    {
        var client = _factory.Inner.CreateClient();
        await StartChannelAsync(client);
        var fixture = SyntheticFixtureGenerator.GenerateCleanBreak(42);
        _evidence.Play(fixture, TimeSpan.Zero);
        await WaitForStateAsync(client, "Program");

        var (response, reader) = await OpenEventsAsync(client);
        using var _ = response;
        Assert.Equal("Program", (await ReadEventAsync(reader, TimeSpan.FromSeconds(5))).GetProperty("state").GetString());

        _evidence.Play(fixture, TimeSpan.FromSeconds(30));
        var commercial = await ReadEventAsync(reader, TimeSpan.FromSeconds(5));
        Assert.Equal("Commercial", commercial.GetProperty("state").GetString());
        Assert.True(commercial.GetProperty("confidence").GetDouble() >= 0.75);

        _evidence.Play(fixture, TimeSpan.FromSeconds(90));
        Assert.Equal("Program", (await ReadEventAsync(reader, TimeSpan.FromSeconds(5))).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Sse_does_not_repeat_unchanged_state_and_sends_keep_alive()
    {
        var client = _factory.Inner.CreateClient();
        await StartChannelAsync(client);
        _evidence.Play(SyntheticFixtureGenerator.GenerateCleanBreak(42), TimeSpan.Zero);
        await WaitForStateAsync(client, "Program");
        var (response, reader) = await OpenEventsAsync(client);
        using var _ = response;
        await ReadEventAsync(reader, TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var line = await reader.ReadLineAsync(cts.Token);
        Assert.Equal(": keep-alive", line); // next thing on the wire is a comment, not a duplicate event
    }

    [Fact]
    public async Task Source_failure_fails_open_to_Unknown()
    {
        using var factory = _viewer.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton<IChannelEvidenceSource>(new ThrowingSource())));
        var client = factory.CreateClient();
        await StartChannelAsync(client);
        var json = JsonDocument.Parse(await client.GetStringAsync("/api/channels/2.1/state")).RootElement;
        Assert.Equal("Unknown", json.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Default_segment_source_returns_no_evidence_for_empty_or_garbage_windows()
    {
        var dir = Path.Combine(_viewer.WorkDir, "seg-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var opts = Microsoft.Extensions.Options.Options.Create(new ViewerOptions { FfmpegPath = "/nonexistent/ffmpeg" });
        var source = new SegmentEvidenceSource(opts, Microsoft.Extensions.Options.Options.Create(new ChannelStateOptions()), _viewer.Time);

        Assert.Empty(await source.GetObservationsAsync("2.1", dir, default));
        File.WriteAllBytes(Path.Combine(dir, "seg00000.ts"), [0x47, 0, 0, 0]);
        Assert.Empty(await source.GetObservationsAsync("2.1", dir, default));
        Assert.Empty(await source.GetObservationsAsync("2.1", Path.Combine(dir, "missing"), default));
    }

    private sealed class ThrowingSource : IChannelEvidenceSource
    {
        public Task<IReadOnlyList<Observation<BroadcastState>>> GetObservationsAsync(string g, string w, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }
}
