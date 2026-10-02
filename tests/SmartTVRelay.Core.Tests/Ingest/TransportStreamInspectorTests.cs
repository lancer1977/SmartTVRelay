namespace SmartTVRelay.Core.Tests.Ingest;

using SmartTVRelay.Core.Ingest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

[Collection("Subprocess")]
public class TransportStreamInspectorTests
{
    /// <summary>Fixture file path using AppContext.BaseDirectory for test output directory.</summary>
    private static string GetFixturePath(string filename)
    {
        return Path.Combine(AppContext.BaseDirectory, "captures", filename);
    }

    [Fact]
    public async Task InspectAsync_WithRealFixture_ReturnsFormatNameMpegts()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert
        Assert.Equal("mpegts", metadata.FormatName);
    }

    [Fact]
    public async Task InspectAsync_WithRealFixture_ReturnsConfidentProbeScore()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert
        // ffprobe's probe_score is a format-detection confidence heuristic that is not
        // stable across ffprobe versions for a real (not synthetically-generated) capture.
        // Confirmed: local ffprobe 9.0.1 scores this fixture 100, while the ffmpeg build
        // apt-get installs on the ubuntu-24.04 CI runner scores it 50. Assert on "confidently
        // detected" (score present and above the midpoint), not an exact value pinned to one
        // ffprobe build.
        Assert.True(metadata.ProbeScore >= 50, $"Expected a confident probe score, got {metadata.ProbeScore}");
    }

    [Fact]
    public async Task InspectAsync_WithRealFixture_ReturnsThreeStreams()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert
        Assert.Equal(3, metadata.Streams.Count);
    }

    [Fact]
    public async Task InspectAsync_WithRealFixture_ContainsVideoStream()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert
        var videoStream = metadata.Streams.FirstOrDefault(s => s.CodecType == "video");
        Assert.NotNull(videoStream);
        Assert.Equal("mpeg2video", videoStream.CodecName);
        Assert.Equal(1920, videoStream.Width);
        Assert.Equal(1080, videoStream.Height);
    }

    [Fact]
    public async Task InspectAsync_WithRealFixture_ContainsAc3AudioStream()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert
        var audioStream = metadata.Streams.FirstOrDefault(s => s.CodecType == "audio" && s.CodecName == "ac3");
        Assert.NotNull(audioStream);
        Assert.Equal(48000, audioStream.SampleRate);
    }

    [Fact]
    public async Task InspectAsync_WithRealFixture_AudioStreamHasLanguageTag()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert
        var engStream = metadata.Streams.FirstOrDefault(s => s.Language == "eng");
        Assert.NotNull(engStream);
        Assert.Equal("audio", engStream.CodecType);
    }

    [Fact]
    public async Task InspectAsync_WithRealFixture_FormatDurationIsPositive()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert
        Assert.NotNull(metadata.Duration);
        Assert.True(metadata.Duration > 0, $"Expected Duration > 0, got {metadata.Duration}");
    }

    [Fact]
    public async Task InspectAsync_WithRealFixture_FormatStartTimeIsNonNull()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert
        Assert.NotNull(metadata.StartTime);
        Assert.True(metadata.StartTime >= 0, $"Expected StartTime >= 0, got {metadata.StartTime}");
    }

    [Fact]
    public async Task InspectAsync_WithNonexistentFile_ThrowsInvalidOperationException()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var nonexistentPath = Path.Combine(AppContext.BaseDirectory, "nonexistent", "file.ts");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => inspector.InspectAsync(nonexistentPath, CancellationToken.None));

        // The exception message should contain the exit code and stderr details.
        Assert.Contains("exited with code", exception.Message);
    }

    [Fact]
    public async Task InspectAsync_WithShortButReasonableTimeout_CompletesSuccessfully()
    {
        // Arrange: Use a 2-second timeout which is longer than ffprobe typically takes (46ms),
        // allowing the operation to complete normally while still testing the timeout mechanism is in place.
        var inspector = new TransportStreamInspector(timeout: TimeSpan.FromSeconds(2));
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert: The operation should complete successfully.
        Assert.Equal(3, metadata.Streams.Count);
    }

    [Fact]
    public async Task InspectAsync_WithCustomFfprobePath_UsesProvidedPath()
    {
        // Arrange: Use the default ffprobe in PATH, but exercise the constructor parameter.
        var inspector = new TransportStreamInspector(ffprobePath: "ffprobe");
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert: If a custom path was incorrectly used, this would fail. Success indicates correct usage.
        Assert.Equal("mpegts", metadata.FormatName);
    }

    [Fact]
    public async Task InspectAsync_WithCustomTimeout_RespectesTimeoutParameter()
    {
        // Arrange: Use a reasonable timeout (e.g., 30 seconds) that should work even in CI.
        var inspector = new TransportStreamInspector(timeout: TimeSpan.FromSeconds(30));
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act: Should complete successfully within the timeout.
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert
        Assert.Equal(3, metadata.Streams.Count);
    }

    [Fact]
    public async Task InspectAsync_AllStreamIndicesArePresent()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert: All streams should have their index set.
        for (int i = 0; i < metadata.Streams.Count; i++)
        {
            Assert.Equal(i, metadata.Streams[i].Index);
        }
    }

    [Fact]
    public async Task InspectAsync_AllStreamsHaveCodecType()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert: All streams must have a codec type.
        foreach (var stream in metadata.Streams)
        {
            Assert.NotNull(stream.CodecType);
            Assert.False(string.IsNullOrEmpty(stream.CodecType));
        }
    }

    [Fact]
    public async Task InspectAsync_VideoStreamHasCorrectMetadata()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert: Verify comprehensive metadata on the video stream.
        var videoStream = metadata.Streams.First(s => s.CodecType == "video");
        Assert.Equal("mpeg2video", videoStream.CodecName);
        Assert.Equal(1920, videoStream.Width);
        Assert.Equal(1080, videoStream.Height);
        Assert.NotNull(videoStream.FrameRate);
        // Frame rate should be a string like "30000/1001", not computed.
        Assert.Contains("/", videoStream.FrameRate);
    }

    [Fact]
    public async Task InspectAsync_AudioStreamMetadataIsCorrect()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert: Verify audio stream metadata.
        var audioStreams = metadata.Streams.Where(s => s.CodecType == "audio").ToList();
        Assert.NotEmpty(audioStreams);

        var engStream = audioStreams.First(s => s.Language == "eng");
        Assert.Equal("ac3", engStream.CodecName);
        Assert.Equal(48000, engStream.SampleRate);
        Assert.NotNull(engStream.ChannelLayout);
    }

    [Fact]
    public async Task InspectAsync_FormatMetadataIsParsedCorrectly()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert: Verify format-level metadata.
        Assert.NotNull(metadata.FormatName);
        Assert.NotNull(metadata.Duration);
        Assert.NotNull(metadata.StartTime);
        Assert.NotNull(metadata.Size);
        Assert.True(metadata.Size > 0, "Expected Size > 0");
        Assert.NotNull(metadata.BitRate);
        Assert.True(metadata.BitRate > 0, "Expected BitRate > 0");
    }

    [Fact]
    public async Task InspectAsync_HungProcess_IsBoundedByConfiguredTimeout()
    {
        // Arrange: point ffprobePath at a script that never produces output and never exits on
        // its own -- this is what actually exercises "bounded subprocess timeout," as opposed to
        // only ever running against the real, fast ffprobe binary. Found and fixed during
        // independent review: the original implementation read stdout/stderr via the no-token
        // ReadToEndAsync() overload and only checked the timeout with a separate WaitForExit(int)
        // called AFTER those reads already returned -- so a process that hangs before writing
        // anything was never actually bounded (confirmed via a live repro: 500ms configured
        // timeout took 5+ seconds to react, and left the child process running afterward).
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "hanging-ffprobe.sh");
        var inspector = new TransportStreamInspector(ffprobePath: scriptPath, timeout: TimeSpan.FromMilliseconds(300));

        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inspector.InspectAsync("irrelevant-path", CancellationToken.None));

        sw.Stop();

        // Generous upper bound (well above the 300ms configured timeout) to avoid flaking on a
        // loaded CI box, while still failing decisively if the timeout isn't actually enforced
        // (the pre-fix behavior took 5+ seconds against the same shape of hang).
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"Expected InspectAsync to honor its ~300ms timeout against a hung process, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task InspectAsync_CallerCancellation_ThrowsOperationCanceledExceptionPromptly()
    {
        // Arrange: same hung-process double, but bound by a very generous inspector timeout so
        // the CALLER's own token is what actually cuts it off, not the inspector's internal one.
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "hanging-ffprobe.sh");
        var inspector = new TransportStreamInspector(ffprobePath: scriptPath, timeout: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inspector.InspectAsync("irrelevant-path", cts.Token));

        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"Expected caller cancellation (~200ms) to cut off InspectAsync promptly, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task InspectAsync_MultipleAudioStreamsHaveDistinctLanguages()
    {
        // Arrange
        var inspector = new TransportStreamInspector();
        var fixturePath = GetFixturePath("sample-live-capture.ts");

        // Act
        var metadata = await inspector.InspectAsync(fixturePath, CancellationToken.None);

        // Assert: The real fixture has 2 audio streams with languages "eng" and "spa".
        var audioStreams = metadata.Streams.Where(s => s.CodecType == "audio").ToList();
        Assert.Equal(2, audioStreams.Count);

        var languages = audioStreams.Select(s => s.Language).ToList();
        Assert.Contains("eng", languages);
        Assert.Contains("spa", languages);
    }
}
