namespace SmartTVRelay.Core.Ingest;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddedCaptionExtractor = PolyhydraGames.CaptionExtractor.CaptionExtractor;

/// <summary>A single caption/subtitle cue, normalized to file-relative timestamps.</summary>
public sealed record CaptionSample(string Text, DateTimeOffset StartTime, DateTimeOffset EndTime);

/// <summary>
/// Extracts captions/subtitles from a media file as normalized, timestamped text samples.
///
/// Tries two extraction paths, in order:
/// 1. The file's standalone subtitle stream (if any), decoded to WebVTT via ffmpeg. A file with no
///    subtitle stream at all is a normal, expected case -- not a tool failure -- and yields an empty
///    list here rather than throwing (confirmed empirically: ffmpeg exits non-zero with "Output file
///    does not contain any stream" in that case, distinct from every other failure mode observed).
/// 2. If that yields nothing and the file looks like an MPEG-TS (its first byte is the 0x47 sync
///    byte), CEA-608/708 captions embedded directly in the video elementary stream -- the form real
///    ATSC broadcasts actually use, which ffmpeg's subtitle-stream mapping cannot see at all (see
///    issue #72). Decoded via the standalone PolyhydraGames.CaptionExtractor library.
///
/// Metadata/text extraction only -- no commercial-language classification (see AGENTS.md).
/// </summary>
public sealed class CaptionExtractor
{
    private const string NoSubtitleStreamMarker = "Output file does not contain any stream";
    private const byte MpegTsSyncByte = 0x47;

    /// <summary>
    /// CEA-608/708 give one timestamp per completed caption (when it's shown), not a start+end
    /// duration. A caption with no next caption on the same channel/window to bound it (the last one
    /// in the stream) gets this fixed fallback duration instead.
    /// </summary>
    private static readonly TimeSpan DefaultEmbeddedCaptionDuration = TimeSpan.FromSeconds(4);

    private readonly string _ffmpegPath;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Initializes a new CaptionExtractor with the specified ffmpeg binary path and subprocess timeout.
    /// </summary>
    /// <param name="ffmpegPath">Path to the ffmpeg binary. Defaults to "ffmpeg" (uses system PATH).</param>
    /// <param name="timeout">Timeout for the ffmpeg subprocess. Defaults to 15 seconds.</param>
    public CaptionExtractor(string ffmpegPath = "ffmpeg", TimeSpan? timeout = null)
    {
        _ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Extracts a media file's captions as a list of timestamped caption samples, in file order.
    /// Tries the standalone subtitle stream first, then falls back to embedded CEA-608/708 captions
    /// (see the class summary). Returns an empty list if neither path finds anything.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when ffmpeg fails for a reason other than "no subtitle stream present" -- a
    /// genuine tool failure, not the normal missing-captions case. The embedded-caption fallback is
    /// not attempted in this case, matching the standalone-subtitle path's existing behavior exactly.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the operation times out or <paramref name="cancellationToken"/> is cancelled.
    /// </exception>
    public async Task<IReadOnlyList<CaptionSample>> ExtractAsync(string filePath, CancellationToken cancellationToken)
    {
        var standaloneSamples = await ExtractFromStandaloneSubtitleAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (standaloneSamples.Count > 0)
        {
            return standaloneSamples;
        }

        if (!await LooksLikeMpegTsAsync(filePath, cancellationToken).ConfigureAwait(false))
        {
            return standaloneSamples;
        }

        var embeddedSamples = await ExtractEmbeddedAsync(filePath, cancellationToken).ConfigureAwait(false);
        return embeddedSamples.Count > 0 ? embeddedSamples : standaloneSamples;
    }

    private async Task<IReadOnlyList<CaptionSample>> ExtractFromStandaloneSubtitleAsync(string filePath, CancellationToken cancellationToken)
    {
        // Create a linked cancellation token source to enforce the timeout while respecting the
        // caller's token (same pattern as TransportStreamInspector.InspectAsync).
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(_timeout);

        var processInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Passed as separate arguments, not an interpolated Arguments string: filePath can
        // legally contain a double quote on Unix, which would otherwise let it terminate the
        // quoted segment early and inject additional ffmpeg options or a different path.
        processInfo.ArgumentList.Add("-v");
        processInfo.ArgumentList.Add("error");
        processInfo.ArgumentList.Add("-i");
        processInfo.ArgumentList.Add(filePath);
        processInfo.ArgumentList.Add("-map");
        processInfo.ArgumentList.Add("0:s:0?");
        processInfo.ArgumentList.Add("-c:s");
        processInfo.ArgumentList.Add("webvtt");
        processInfo.ArgumentList.Add("-f");
        processInfo.ArgumentList.Add("webvtt");
        processInfo.ArgumentList.Add("-");

        using var process = new Process { StartInfo = processInfo };

        try
        {
            process.Start();

            // Read stdout/stderr and await process exit all against the SAME linked token, so a
            // hung ffmpeg is actually bounded by the configured timeout (see
            // TransportStreamInspector.InspectAsync for the reasoning and a confirmed repro of
            // the alternative, unbounded shape).
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                if (stderr.Contains(NoSubtitleStreamMarker, StringComparison.Ordinal))
                {
                    // No subtitle stream at all: a normal, expected case, not a tool failure.
                    return Array.Empty<CaptionSample>();
                }

                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode} while extracting captions from '{filePath}'. " +
                    $"stderr: {stderr}");
            }

            return ParseWebVtt(stdout);
        }
        catch (OperationCanceledException)
        {
            // Ensure process is killed on cancellation OR timeout (both surface as
            // OperationCanceledException via the linked token). Kill() only requests
            // termination asynchronously, so wait for the exit to actually complete --
            // otherwise a caller that cancels repeatedly can accumulate live ffmpeg processes
            // (same pattern as FrameAudioSampler's cancellation paths).
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort: the cancellation itself is what matters to the caller.
                }
            }

            throw;
        }
    }

    private static async Task<bool> LooksLikeMpegTsAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return read == 1 && buffer[0] == MpegTsSyncByte;
    }

    private static async Task<IReadOnlyList<CaptionSample>> ExtractEmbeddedAsync(string filePath, CancellationToken cancellationToken)
    {
        var cues = await EmbeddedCaptionExtractor.ExtractAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (cues.Count == 0)
        {
            return Array.Empty<CaptionSample>();
        }

        var samples = new List<CaptionSample>();
        foreach (var group in cues.Where(c => c.Pts.HasValue).GroupBy(c => c.Source))
        {
            var ordered = group.OrderBy(c => c.Pts!.Value).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var start = PtsToDateTimeOffset(ordered[i].Pts!.Value);
                var end = i + 1 < ordered.Count
                    ? PtsToDateTimeOffset(ordered[i + 1].Pts!.Value)
                    : start + DefaultEmbeddedCaptionDuration;
                samples.Add(new CaptionSample(ordered[i].Text, start, end));
            }
        }

        return samples.OrderBy(s => s.StartTime).ToList();
    }

    private const double CaptionPtsFrequencyHz = 90_000.0;

    private static DateTimeOffset PtsToDateTimeOffset(long pts) =>
        DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(pts / CaptionPtsFrequencyHz);

    private static IReadOnlyList<CaptionSample> ParseWebVtt(string webVttText)
    {
        var samples = new List<CaptionSample>();
        var lines = webVttText.Replace("\r\n", "\n").Split('\n');

        int i = 0;
        while (i < lines.Length)
        {
            if (lines[i].Contains("-->", StringComparison.Ordinal) && TryParseCueTiming(lines[i], out var start, out var end))
            {
                i++;
                var textLines = new List<string>();
                while (i < lines.Length && lines[i].Length > 0)
                {
                    textLines.Add(lines[i]);
                    i++;
                }

                if (textLines.Count > 0)
                {
                    samples.Add(new CaptionSample(string.Join('\n', textLines), start, end));
                }

                continue;
            }

            i++;
        }

        return samples;
    }

    private static bool TryParseCueTiming(string line, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = default;
        end = default;

        var parts = line.Split("-->", 2, StringSplitOptions.None);
        if (parts.Length != 2)
        {
            return false;
        }

        // The end side may carry cue settings after the timestamp (e.g. "00:02.500 align:start");
        // only the first whitespace-delimited token is the timestamp itself.
        var endTrimmed = parts[1].Trim();
        var spaceIndex = endTrimmed.IndexOf(' ');
        var endToken = spaceIndex >= 0 ? endTrimmed[..spaceIndex] : endTrimmed;

        if (!TryParseVttTimestamp(parts[0].Trim(), out var startSeconds) ||
            !TryParseVttTimestamp(endToken, out var endSeconds))
        {
            return false;
        }

        start = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(startSeconds);
        end = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(endSeconds);
        return true;
    }

    /// <summary>
    /// Parses a WebVTT cue timestamp, which is either "HH:MM:SS.mmm" or, when hours is 0, the
    /// shorter "MM:SS.mmm" -- both forms are valid per the WebVTT spec, and ffmpeg emits the
    /// shorter form whenever hours is 0 (confirmed empirically against real ffmpeg output).
    /// </summary>
    private static bool TryParseVttTimestamp(string text, out double totalSeconds)
    {
        totalSeconds = 0;
        var segments = text.Split(':');
        if (segments.Length is not (2 or 3))
        {
            return false;
        }

        double hours = 0;
        int segmentIndex = 0;
        if (segments.Length == 3)
        {
            if (!double.TryParse(segments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out hours))
            {
                return false;
            }

            segmentIndex = 1;
        }

        if (!double.TryParse(segments[segmentIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(segments[segmentIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        totalSeconds = (hours * 3600) + (minutes * 60) + seconds;
        return true;
    }
}
