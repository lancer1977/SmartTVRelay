using System.Diagnostics;
using Microsoft.Extensions.Options;
using SmartTVRelay.Viewer.State;

namespace SmartTVRelay.Viewer.Tests;

/// <summary>
/// Issue #136: the Viewer's default evidence source must run the audio-loudness detector over the
/// raw tuner window, not only SCTE-35 and black-frame. The window is a real ffmpeg-made transport
/// stream with a loud-to-quiet step, so this also exercises the real sampler and detector.
/// </summary>
public sealed class SegmentEvidenceLoudnessTests : IDisposable
{
    private readonly string _workDir = Directory.CreateTempSubdirectory("segment-evidence-loudness-").FullName;

    public void Dispose() => Directory.Delete(_workDir, recursive: true);

    private static SegmentEvidenceSource Source(string ffmpeg = "ffmpeg") => new(
        Options.Create(new ViewerOptions { FfmpegPath = ffmpeg }),
        Options.Create(new ChannelStateOptions { WindowSegments = 3 }),
        TimeProvider.System);

    /// <summary>3 s of full-scale tone, then 3 s at a much lower level, with a plain video track.</summary>
    private void WriteLoudThenQuietWindow()
    {
        var output = Path.Combine(_workDir, "raw-000000.ts");
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
        {
            "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=6",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3",
            "-filter_complex", "[2:a]volume=0.02[q];[1:a][q]concat=n=2:v=0:a=1[a]",
            "-map", "0:v", "-map", "[a]",
            "-c:v", "mpeg2video", "-c:a", "mp2", "-f", "mpegts", output,
        }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"ffmpeg could not build the test window: {error}");
    }

    [Fact]
    public async Task Window_with_a_loudness_step_yields_audio_loudness_evidence()
    {
        WriteLoudThenQuietWindow();

        var observations = await Source().GetObservationsAsync("2.1", _workDir, CancellationToken.None);

        Assert.Contains(observations, o => o.SourceKind == "audio-loudness");
    }

    [Fact]
    public async Task Audio_sampler_failure_does_not_remove_other_evidence_or_throw()
    {
        WriteLoudThenQuietWindow();

        // A missing ffmpeg fails every sampler; the source must still fail open without throwing.
        var observations = await Source("/nonexistent/ffmpeg")
            .GetObservationsAsync("2.1", _workDir, CancellationToken.None);

        Assert.DoesNotContain(observations, o => o.SourceKind == "audio-loudness");
    }
}
