namespace SmartTVRelay.Core.Tests.Ingest;

using SmartTVRelay.Core.Ingest;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

/// <summary>
/// Covers the embedded CEA-608/708 fallback path (issue #72): a synthetic MPEG-TS fixture with no
/// standalone subtitle stream but captions embedded directly in the video elementary stream's SEI
/// NAL units. Uses a scripted ffmpeg double (see no-subtitle-ffmpeg.sh) so the standalone-subtitle
/// leg fails deterministically the same way ffmpeg does for a file with no subtitle stream, without
/// depending on ffmpeg being able to probe a fixture that is only spec-shaped enough for
/// PolyhydraGames.CaptionExtractor's own demuxer.
/// </summary>
[Collection("Subprocess")]
public class EmbeddedCaptionExtractorTests
{
    private static string NoSubtitleFfmpegPath => Path.Combine(AppContext.BaseDirectory, "no-subtitle-ffmpeg.sh");

    [Fact]
    public async Task ExtractAsync_MpegTsWithEmbeddedCaptions_FallsBackAndAppliesNextCaptionEndsThisOneHeuristic()
    {
        // Arrange: two CEA-608 captions on CC1, 1 second apart (90kHz ticks).
        var tsBytes = EmbeddedCaptionFixtureBuilder.BuildTransportStreamWithCaptions(
        [
            (Pts: 90_000L, EmbeddedCaptionFixtureBuilder.Cea608Caption((byte)'H', (byte)'I')),
            (Pts: 180_000L, EmbeddedCaptionFixtureBuilder.Cea608Caption((byte)'O', (byte)'K')),
        ]);

        var tempFile = Path.Combine(Path.GetTempPath(), $"embedded-captions-{Guid.NewGuid()}.ts");
        await File.WriteAllBytesAsync(tempFile, tsBytes);
        try
        {
            var extractor = new CaptionExtractor(ffmpegPath: NoSubtitleFfmpegPath);

            // Act
            var samples = await extractor.ExtractAsync(tempFile, CancellationToken.None);

            // Assert
            Assert.Equal(2, samples.Count);

            Assert.Equal("HI", samples[0].Text);
            Assert.Equal(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(1.0), samples[0].StartTime);
            // The next caption's timestamp ends this one.
            Assert.Equal(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(2.0), samples[0].EndTime);

            Assert.Equal("OK", samples[1].Text);
            Assert.Equal(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(2.0), samples[1].StartTime);
            // No next caption to bound the last one: falls back to the fixed default duration.
            Assert.Equal(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(2.0) + TimeSpan.FromSeconds(4), samples[1].EndTime);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExtractAsync_MpegTsWithNoEmbeddedCaptions_ReturnsEmptyWithoutThrowing()
    {
        // Arrange: a valid MPEG-TS stream (PAT+PMT, video PID) but no caption data at all -- the
        // fallback must not fabricate anything when there is genuinely nothing to find.
        var tsBytes = EmbeddedCaptionFixtureBuilder.BuildTransportStreamWithCaptions([]);

        var tempFile = Path.Combine(Path.GetTempPath(), $"embedded-captions-empty-{Guid.NewGuid()}.ts");
        await File.WriteAllBytesAsync(tempFile, tsBytes);
        try
        {
            var extractor = new CaptionExtractor(ffmpegPath: NoSubtitleFfmpegPath);

            // Act
            var samples = await extractor.ExtractAsync(tempFile, CancellationToken.None);

            // Assert
            Assert.Empty(samples);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExtractAsync_NonMpegTsFileWithNoSubtitleStream_DoesNotAttemptEmbeddedFallback()
    {
        // Arrange: a file that does NOT start with the MPEG-TS sync byte -- the embedded-caption
        // fallback must not even be attempted (EmbeddedCaptionExtractor.ExtractAsync would throw or
        // misparse arbitrary non-TS bytes; the sync-byte guard exists precisely to avoid that).
        var tempFile = Path.Combine(Path.GetTempPath(), $"not-a-ts-file-{Guid.NewGuid()}.bin");
        await File.WriteAllBytesAsync(tempFile, [0x00, 0x01, 0x02, 0x03]);
        try
        {
            var extractor = new CaptionExtractor(ffmpegPath: NoSubtitleFfmpegPath);

            // Act
            var samples = await extractor.ExtractAsync(tempFile, CancellationToken.None);

            // Assert
            Assert.Empty(samples);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
