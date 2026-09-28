namespace SmartTVRelay.Core.Ingest;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Metadata for a single stream (video/audio/subtitle) extracted via ffprobe.</summary>
public sealed record StreamMetadata(
    int Index,
    string CodecType,               // "video" | "audio" | "subtitle" | others ffprobe reports
    string? CodecName,
    int? Width,
    int? Height,
    string? FrameRate,              // raw r_frame_rate string, e.g. "30000/1001" -- not computed
    int? SampleRate,
    int? Channels,
    string? ChannelLayout,
    long? BitRate,
    double? Duration,
    double? StartTime,
    string? Language);              // from tags.language, if present

/// <summary>Metadata for the complete transport stream, including format-level and per-stream data.</summary>
public sealed record TransportStreamMetadata(
    string FormatName,
    double? Duration,
    double? StartTime,
    long? Size,
    long? BitRate,
    int ProbeScore,
    IReadOnlyList<StreamMetadata> Streams);

/// <summary>
/// Inspects MPEG-TS transport stream metadata using ffprobe.
/// Emits structured metadata about streams, programs, PIDs, codecs, frame rates, resolutions, audio metadata,
/// and subtitle/caption metadata. Metadata inspection only -- no decoding, sampling, or detection logic.
/// </summary>
public sealed class TransportStreamInspector
{
    private readonly string _ffprobePath;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Initializes a new TransportStreamInspector with the specified ffprobe binary path and subprocess timeout.
    /// </summary>
    /// <param name="ffprobePath">Path to the ffprobe binary. Defaults to "ffprobe" (uses system PATH).</param>
    /// <param name="timeout">Timeout for the ffprobe subprocess. Defaults to 10 seconds.</param>
    public TransportStreamInspector(string ffprobePath = "ffprobe", TimeSpan? timeout = null)
    {
        _ffprobePath = ffprobePath;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Inspects a transport stream file and returns structured metadata.
    /// </summary>
    /// <param name="filePath">Full path to the transport stream file to inspect.</param>
    /// <param name="cancellationToken">Cancellation token to allow caller-initiated cancellation.</param>
    /// <returns>Structured transport stream metadata.</returns>
    /// <exception cref="InvalidOperationException">Thrown when ffprobe exits non-zero; message includes exit code and stderr.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation times out or cancellationToken is cancelled.</exception>
    public async Task<TransportStreamMetadata> InspectAsync(string filePath, CancellationToken cancellationToken)
    {
        // Create a linked cancellation token source to enforce the timeout while respecting the caller's token.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(_timeout);

        var processInfo = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            Arguments = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = processInfo };

        try
        {
            process.Start();

            // Read stdout and stderr concurrently to avoid deadlock if either pipe buffer fills.
            var (stdout, stderr) = await ReadProcessStreamsAsync(process, linkedCts.Token);

            // Wait for the process to complete, respecting the linked cancellation token.
            var completed = process.WaitForExit((int)_timeout.TotalMilliseconds);
            if (!completed)
            {
                // Process did not exit before timeout. Kill it.
                process.Kill(entireProcessTree: true);
                throw new OperationCanceledException($"ffprobe timed out after {_timeout.TotalSeconds} seconds while inspecting '{filePath}'");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffprobe exited with code {process.ExitCode} while inspecting '{filePath}'. " +
                    $"stderr: {stderr}");
            }

            // Parse the JSON output.
            return ParseFfprobeOutput(stdout);
        }
        catch (OperationCanceledException)
        {
            // Ensure process is killed on cancellation.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
    }

    private static async Task<(string stdout, string stderr)> ReadProcessStreamsAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // On cancellation, still drain the streams to avoid abandoned processes.
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch { /* ignore */ }
            throw;
        }

        return (stdoutTask.Result, stderrTask.Result);
    }

    private static TransportStreamMetadata ParseFfprobeOutput(string jsonOutput)
    {
        using var doc = JsonDocument.Parse(jsonOutput);
        var root = doc.RootElement;

        // Parse format-level metadata.
        var formatElement = root.GetProperty("format");
        var formatName = formatElement.GetProperty("format_name").GetString() ?? "";
        var probeScore = formatElement.TryGetProperty("probe_score", out var ps) ? ps.GetInt32() : 0;

        var duration = ParseOptionalDouble(formatElement, "duration");
        var startTime = ParseOptionalDouble(formatElement, "start_time");
        var size = ParseOptionalLong(formatElement, "size");
        var bitRate = ParseOptionalLong(formatElement, "bit_rate");

        // Parse streams.
        var streams = new List<StreamMetadata>();
        if (root.TryGetProperty("streams", out var streamsElement) && streamsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var streamElement in streamsElement.EnumerateArray())
            {
                var index = streamElement.GetProperty("index").GetInt32();
                var codecType = streamElement.GetProperty("codec_type").GetString() ?? "";
                var codecName = streamElement.TryGetProperty("codec_name", out var cn) ? cn.GetString() : null;
                var width = streamElement.TryGetProperty("width", out var w) ? (int?)w.GetInt32() : null;
                var height = streamElement.TryGetProperty("height", out var h) ? (int?)h.GetInt32() : null;
                var frameRate = streamElement.TryGetProperty("r_frame_rate", out var fr) ? fr.GetString() : null;
                var sampleRate = streamElement.TryGetProperty("sample_rate", out var sr)
                    ? (int.TryParse(sr.GetString() ?? "", out var srVal) ? (int?)srVal : null)
                    : null;
                var channels = streamElement.TryGetProperty("channels", out var ch) ? (int?)ch.GetInt32() : null;
                var channelLayout = streamElement.TryGetProperty("channel_layout", out var cl) ? cl.GetString() : null;
                var bitRateStream = ParseOptionalLong(streamElement, "bit_rate");
                var durationStream = ParseOptionalDouble(streamElement, "duration");
                var startTimeStream = ParseOptionalDouble(streamElement, "start_time");

                // Extract language from tags if present.
                string? language = null;
                if (streamElement.TryGetProperty("tags", out var tagsElement) && tagsElement.TryGetProperty("language", out var langElement))
                {
                    language = langElement.GetString();
                }

                var stream = new StreamMetadata(
                    Index: index,
                    CodecType: codecType,
                    CodecName: codecName,
                    Width: width,
                    Height: height,
                    FrameRate: frameRate,
                    SampleRate: sampleRate,
                    Channels: channels,
                    ChannelLayout: channelLayout,
                    BitRate: bitRateStream,
                    Duration: durationStream,
                    StartTime: startTimeStream,
                    Language: language);

                streams.Add(stream);
            }
        }

        return new TransportStreamMetadata(
            FormatName: formatName,
            Duration: duration,
            StartTime: startTime,
            Size: size,
            BitRate: bitRate,
            ProbeScore: probeScore,
            Streams: streams);
    }

    private static double? ParseOptionalDouble(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
            {
                var str = prop.GetString();
                if (double.TryParse(str, out var dVal))
                {
                    return dVal;
                }
            }
            else if (prop.ValueKind == JsonValueKind.Number)
            {
                return prop.GetDouble();
            }
        }

        return null;
    }

    private static long? ParseOptionalLong(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
            {
                var str = prop.GetString();
                if (long.TryParse(str, out var lVal))
                {
                    return lVal;
                }
            }
            else if (prop.ValueKind == JsonValueKind.Number)
            {
                return prop.GetInt64();
            }
        }

        return null;
    }
}
