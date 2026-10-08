using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Observation.Core;
using SmartTVRelay.Core;
using SmartTVRelay.Viewer.State;

namespace SmartTVRelay.Viewer.Tests;

/// <summary>
/// Offline proof (issue #123, part A): a SCTE-35 break in raw tuner transport makes
/// <c>GET /api/channels/{n}/state</c> report Commercial only while the break is active, and missing,
/// stale or contradictory evidence reports Unknown/Program (fail open: the original broadcast is kept).
///
/// The transport bytes below are hand-encoded from ANSI/SCTE 35 and ISO/IEC 13818-1 and are deliberately
/// NOT produced by <c>SyntheticFixtureGenerator</c> or <c>generate-scte35-fixture.cs</c>
/// (docs/scte35-findings.md: an earlier parser and its own generator agreed while both being wrong).
/// The sections were cross-checked with <c>ffprobe</c>, which lists PID 0x1F0 as stream_type 0x86.
/// The route is the real one: raw-*.ts files in the running pipeline's work directory ->
/// SegmentEvidenceSource -> Scte35MarkerExtractor + PCR clock -> ChannelStateService -> the HTTP endpoint.
/// </summary>
public sealed class Scte35CommercialStateProofTests : IDisposable
{
    // Layout: PAT PID 0, PMT PID 0x1000 (program 1, PCR PID 0x100), video 0x100 (stream_type 0x02),
    // SCTE-35 PID 0x1F0 (stream_type 0x86). Every section starts after a pointer_field of 0.
    // CRC_32/MPEG-2 trailers are the final 4 bytes of each literal.
    private const string Pat = "00b00d0001c100000001f0002ab104b2";
    private const string PmtWithScte35 = "02b0170001c10000e100f00002e100f00086e1f0f0002f1d77b0";
    private const string PmtVideoOnly = "02b0120001c10000e100f00002e100f0009e8b23d1";

    // splice_info_section, splice_insert (0x05), event 1, out_of_network=1, splice_time pts=900000 (10 s),
    // break_duration 2700000 (30 s). splice_time is FE 000DBBA0: flag(1) reserved(111111) pts[32]=0, then 32 bits.
    private const string SpliceInsertCueOut = "fc3025000000000000fffff01405000000017feffe000dbba0fe002932e0000100000000ae8ef27f";
    // event 2, out_of_network=0, pts=3600000 (40 s).
    private const string SpliceInsertCueIn = "fc3020000000000000fffff00f05000000027f4ffe0036ee800001000000006e6a7d49";
    // splice_insert with splice_event_cancel_indicator=1 for event 1.
    private const string SpliceInsertCancelEvent1 = "fc3016000000000000fffff0050500000001ff000013661bfa";
    // time_signal (0x06) + segmentation_descriptor: Distributor Placement Opportunity Start (0x36) at 10 s,
    // End (0x37) at 40 s (the shape seen on real WLWT cues, issue #124).
    private const string TimeSignalStart = "fc3027000000000000fffff00506fe000dbba00011020f43554549000000aa7fbf0000360000367477e0";
    private const string TimeSignalEnd = "fc3027000000000000fffff00506fe0036ee800011020f43554549000000ab7fbf00003700006a6e6f6a";

    private const long Pts10s = 900_000, Pts40s = 3_600_000;

    public static TheoryData<string, string, string> CueFlavours => new()
    {
        { "splice_insert", SpliceInsertCueOut, SpliceInsertCueIn },
        { "time_signal+segmentation", TimeSignalStart, TimeSignalEnd },
    };

    private readonly ViewerFactory _viewer = new();
    private readonly List<IDisposable> _disposables = new();
    private int _fileCounter;
    private string _workDir = "";
    private ChannelStateService _service = null!;
    private HttpClient _client = null!;

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        _viewer.Dispose();
    }

    private async Task StartAsync(Func<IChannelEvidenceSource, TimeProvider, IChannelEvidenceSource>? wrap = null)
    {
        var factory = _viewer.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Viewer:FfmpegPath"] = "/nonexistent/ffmpeg", // video sampling is not part of this proof
                ["Viewer:State:PollMilliseconds"] = "3600000", // refresh only when the test says so
                ["Viewer:State:WindowSegments"] = "6",
            }));
            if (wrap is not null)
                b.ConfigureServices(s => s.AddSingleton<IChannelEvidenceSource>(sp =>
                {
                    var time = sp.GetRequiredService<TimeProvider>();
                    var inner = new SegmentEvidenceSource(
                        Microsoft.Extensions.Options.Options.Create(new ViewerOptions { FfmpegPath = "/nonexistent/ffmpeg" }),
                        Microsoft.Extensions.Options.Options.Create(new ChannelStateOptions { WindowSegments = 6 }), time);
                    return wrap(inner, time);
                }));
        });
        _disposables.Add(factory);
        _client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/hls/2.1/index.m3u8")).StatusCode);
        _workDir = _viewer.Runner.Started.Single().Info.OutputDirectory;
        _service = factory.Services.GetRequiredService<ChannelStateService>();
    }

    // ---- hand-built transport -------------------------------------------------------------------

    private static byte[] Packet(int pid, string sectionHex)
    {
        var section = Convert.FromHexString(sectionHex);
        Assert.True(section.Length <= 183);
        var p = new byte[188];
        Array.Fill(p, (byte)0xFF);
        p[0] = 0x47;
        p[1] = (byte)(0x40 | (pid >> 8)); // payload_unit_start_indicator
        p[2] = (byte)pid;
        p[3] = 0x10; // payload only
        p[4] = 0x00; // pointer_field
        section.CopyTo(p, 5);
        return p;
    }

    private static byte[] PcrPacket(long pcrBase)
    {
        var p = new byte[188];
        Array.Fill(p, (byte)0xFF);
        p[0] = 0x47; p[1] = 0x01; p[2] = 0x00; // PID 0x100
        p[3] = 0x30; p[4] = 183; p[5] = 0x10;  // adaptation field, PCR flag
        p[6] = (byte)(pcrBase >> 25); p[7] = (byte)(pcrBase >> 17); p[8] = (byte)(pcrBase >> 9);
        p[9] = (byte)(pcrBase >> 1); p[10] = (byte)(((pcrBase & 1) << 7) | 0x7E); p[11] = 0;
        return p;
    }

    private static byte[] Psi(string pmt) => [.. Packet(0, Pat), .. Packet(0x1000, pmt)];

    /// <summary>Writes the next raw tuner window file.</summary>
    private void WriteRaw(params byte[][] parts) =>
        File.WriteAllBytes(Path.Combine(_workDir, $"raw-{_fileCounter++:D6}.ts"), parts.SelectMany(p => p).ToArray());

    private static byte[] Cues(params string[] sections) =>
        sections.SelectMany(s => Packet(0x1F0, s)).ToArray();

    /// <summary>Moves stream clock and wall clock forward together, then reads the real endpoint.</summary>
    private async Task<JsonElement> StateAtAsync(long pcrSeconds, TimeSpan wallAdvance)
    {
        WriteRaw(PcrPacket(pcrSeconds * 90_000));
        return await ObserveAsync(wallAdvance);
    }

    private async Task<JsonElement> ObserveAsync(TimeSpan wallAdvance)
    {
        _viewer.Time.Advance(wallAdvance);
        await _service.RefreshAsync("2.1", default);
        var response = await _client.GetAsync("/api/channels/2.1/state");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string StateOf(JsonElement json) => json.GetProperty("state").GetString()!;

    private static void AssertUnknownWithNoMarkerEvidence(JsonElement json)
    {
        Assert.Equal("Unknown", StateOf(json));
        Assert.True(json.GetProperty("confidence").GetDouble() <= 0.2);
        Assert.Equal(0, json.GetProperty("evidence").GetArrayLength());
    }

    // ---- the break ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CueFlavours))]
    public async Task State_is_Commercial_only_inside_the_SCTE35_break(string flavour, string cueOut, string cueIn)
    {
        await StartAsync();
        // Window 0: PSI, stream clock at 0 s, both cues already in the stream (announced ahead of time).
        WriteRaw(Psi(PmtWithScte35), PcrPacket(0), Cues(cueOut, cueIn));

        // Before the break (stream 0 s, 5 s): cues are announced but their PTS is in the future.
        AssertUnknownWithNoMarkerEvidence(await ObserveAsync(TimeSpan.Zero));
        AssertUnknownWithNoMarkerEvidence(await StateAtAsync(5, TimeSpan.FromSeconds(5)));

        // Inside the break (stream 12 s, 39 s).
        var during = await StateAtAsync(12, TimeSpan.FromSeconds(7));
        Assert.Equal("Commercial", StateOf(during));
        Assert.True(during.GetProperty("confidence").GetDouble() >= 0.75);
        var evidence = during.GetProperty("evidence");
        Assert.Equal(1, evidence.GetArrayLength());
        Assert.Equal("explicit-marker", evidence[0].GetProperty("kind").GetString());
        Assert.Equal("viewer-2.1", evidence[0].GetProperty("source").GetString());
        Assert.Equal("Commercial", StateOf(await StateAtAsync(39, TimeSpan.FromSeconds(27))));

        // After the break (stream 41 s): CueIn has been reached.
        var after = await StateAtAsync(41, TimeSpan.FromSeconds(2));
        Assert.Equal("Program", StateOf(after));
        Assert.Equal("explicit-marker", after.GetProperty("evidence")[0].GetProperty("kind").GetString());
        Assert.Equal("Program", StateOf(await StateAtAsync(60, TimeSpan.FromSeconds(19))));
        _ = flavour;
    }

    [Fact]
    public async Task The_break_boundary_is_the_cue_PTS_not_arrival_order()
    {
        await StartAsync();
        WriteRaw(Psi(PmtWithScte35), PcrPacket(0), Cues(SpliceInsertCueOut, SpliceInsertCueIn));
        await ObserveAsync(TimeSpan.Zero);
        // One 90 kHz tick short of the CueOut PTS, then exactly at it.
        WriteRaw(PcrPacket(Pts10s - 1));
        Assert.Equal("Unknown", StateOf(await ObserveAsync(TimeSpan.FromSeconds(10))));
        WriteRaw(PcrPacket(Pts10s));
        Assert.Equal("Commercial", StateOf(await ObserveAsync(TimeSpan.FromMilliseconds(1))));
        WriteRaw(PcrPacket(Pts40s - 1));
        Assert.Equal("Commercial", StateOf(await ObserveAsync(TimeSpan.FromSeconds(29))));
        WriteRaw(PcrPacket(Pts40s));
        Assert.Equal("Program", StateOf(await ObserveAsync(TimeSpan.FromMilliseconds(1))));
    }

    // ---- missing evidence -----------------------------------------------------------------------

    [Fact]
    public async Task No_raw_data_at_all_is_Unknown()
    {
        await StartAsync();
        AssertUnknownWithNoMarkerEvidence(await ObserveAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task Stream_without_a_SCTE35_PID_is_Unknown_however_far_the_clock_runs()
    {
        await StartAsync();
        WriteRaw(Psi(PmtVideoOnly), PcrPacket(0));
        AssertUnknownWithNoMarkerEvidence(await ObserveAsync(TimeSpan.Zero));
        AssertUnknownWithNoMarkerEvidence(await StateAtAsync(12, TimeSpan.FromSeconds(12)));
        AssertUnknownWithNoMarkerEvidence(await StateAtAsync(100, TimeSpan.FromSeconds(88)));
    }

    [Fact]
    public async Task SCTE35_PID_that_carries_no_cues_is_Unknown()
    {
        await StartAsync();
        WriteRaw(Psi(PmtWithScte35), PcrPacket(0));
        AssertUnknownWithNoMarkerEvidence(await ObserveAsync(TimeSpan.Zero));
        AssertUnknownWithNoMarkerEvidence(await StateAtAsync(12, TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public async Task Cues_without_any_transport_clock_never_report_Commercial()
    {
        await StartAsync();
        WriteRaw(Psi(PmtWithScte35), Cues(SpliceInsertCueOut, SpliceInsertCueIn)); // no PCR anywhere
        AssertUnknownWithNoMarkerEvidence(await ObserveAsync(TimeSpan.Zero));
        AssertUnknownWithNoMarkerEvidence(await ObserveAsync(TimeSpan.FromSeconds(60)));
    }

    // ---- stale evidence -------------------------------------------------------------------------

    [Fact]
    public async Task Commercial_goes_stale_to_Unknown_when_the_capture_stops_updating()
    {
        await StartAsync();
        WriteRaw(Psi(PmtWithScte35), PcrPacket(0), Cues(SpliceInsertCueOut, SpliceInsertCueIn));
        Assert.Equal("Commercial", StateOf(await StateAtAsync(12, TimeSpan.FromSeconds(12))));

        // Capture freezes: no new raw file. Inside the 30 s evidence window nothing changes...
        Assert.Equal("Commercial", StateOf(await ObserveAsync(TimeSpan.FromSeconds(20))));
        // ...and once the evidence is older than the window the endpoint must not keep claiming Commercial.
        AssertUnknownWithNoMarkerEvidence(await ObserveAsync(TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public async Task Break_with_no_CueIn_expires_after_the_marker_hold_even_if_raw_data_keeps_arriving()
    {
        await StartAsync();
        WriteRaw(Psi(PmtWithScte35), PcrPacket(0), Cues(SpliceInsertCueOut));
        Assert.Equal("Commercial", StateOf(await StateAtAsync(12, TimeSpan.FromSeconds(12))));
        Assert.Equal("Commercial", StateOf(await StateAtAsync(70, TimeSpan.FromSeconds(60)))); // within MaxMarkerHoldSeconds=120
        // 125 s of wall time after the CueOut was first applied, fresh raw data still arriving, CueIn never came.
        AssertUnknownWithNoMarkerEvidence(await StateAtAsync(130, TimeSpan.FromSeconds(65)));
    }

    // ---- conflicting evidence -------------------------------------------------------------------

    [Fact]
    public async Task CueIn_without_a_CueOut_never_reports_Commercial()
    {
        await StartAsync();
        WriteRaw(Psi(PmtWithScte35), PcrPacket(0), Cues(SpliceInsertCueIn));
        Assert.Equal("Unknown", StateOf(await ObserveAsync(TimeSpan.Zero)));
        var json = await StateAtAsync(41, TimeSpan.FromSeconds(41));
        // Current behavior: an orphan CueIn is explicit "program" evidence. Either answer keeps the original
        // broadcast; the contract is that it is never Commercial.
        Assert.Contains(StateOf(json), new[] { "Program", "Unknown" });
        Assert.NotEqual("Commercial", StateOf(json));
    }

    [Fact]
    public async Task Cancelled_CueOut_never_reports_Commercial()
    {
        await StartAsync();
        WriteRaw(Psi(PmtWithScte35), PcrPacket(0), Cues(SpliceInsertCueOut, SpliceInsertCancelEvent1));
        AssertUnknownWithNoMarkerEvidence(await StateAtAsync(12, TimeSpan.FromSeconds(12)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CueOut_and_CueIn_at_the_same_instant_are_contradictory_and_never_report_Commercial(bool cueOutFirst)
    {
        await StartAsync();
        // A CueIn whose PTS equals the CueOut's PTS (10 s): event 2 re-timed from the usual 40 s.
        const string cueInAt10s = "fc3020000000000000fffff00f05000000027f4ffe000dbba000010000000030dcf2f5";
        WriteRaw(Psi(PmtWithScte35), PcrPacket(0),
            cueOutFirst ? Cues(SpliceInsertCueOut, cueInAt10s) : Cues(cueInAt10s, SpliceInsertCueOut));
        var json = await StateAtAsync(12, TimeSpan.FromSeconds(12));
        AssertUnknownWithNoMarkerEvidence(json); // not order-dependent: neither cue may win
    }

    [Fact]
    public async Task Disputed_by_a_competing_detector_is_Unknown_with_provenance_kept()
    {
        await StartAsync((inner, time) => new DisputingSource(inner, time));
        WriteRaw(Psi(PmtWithScte35), PcrPacket(0), Cues(SpliceInsertCueOut, SpliceInsertCueIn));
        var json = await StateAtAsync(12, TimeSpan.FromSeconds(12));
        Assert.Equal("Unknown", StateOf(json));
        Assert.True(json.GetProperty("confidence").GetDouble() <= 0.2);
        var kinds = json.GetProperty("evidence").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToArray();
        Assert.Contains("explicit-marker", kinds);
        Assert.Contains("heuristic", kinds);
    }

    // ---- guard on the hand-built bytes themselves -----------------------------------------------

    [Theory]
    [InlineData(Pat)]
    [InlineData(PmtWithScte35)]
    [InlineData(PmtVideoOnly)]
    [InlineData(SpliceInsertCueOut)]
    [InlineData(SpliceInsertCueIn)]
    [InlineData(SpliceInsertCancelEvent1)]
    [InlineData(TimeSignalStart)]
    [InlineData(TimeSignalEnd)]
    public void Fixture_sections_carry_valid_lengths_and_CRC32_MPEG2(string hex)
    {
        var s = Convert.FromHexString(hex);
        Assert.Equal(3 + (((s[1] & 0x0F) << 8) | s[2]), s.Length);
        uint crc = 0xFFFFFFFF; // independent bitwise CRC-32/MPEG-2 (poly 0x04C11DB7, no reflection)
        foreach (var b in s)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        Assert.Equal(0u, crc); // a CRC over data+CRC is zero
    }

    /// <summary>Real marker evidence plus a near-equal, opposing heuristic detector.</summary>
    private sealed class DisputingSource(IChannelEvidenceSource inner, TimeProvider time) : IChannelEvidenceSource
    {
        public async Task<IReadOnlyList<Observation<BroadcastState>>> GetObservationsAsync(string guide, string dir, CancellationToken ct)
        {
            var real = await inner.GetObservationsAsync(guide, dir, ct);
            if (real.Count == 0) return real;
            var rival = new Observation<BroadcastState>(Guid.NewGuid(), ObservationScope.Of("rival"), BroadcastObservations.Subject,
                BroadcastObservations.Predicate, BroadcastState.Program, "rival-detector", "heuristic", 0.95, time.GetUtcNow());
            return [.. real, rival];
        }
    }
}
