namespace SmartTVRelay.Core.Ingest;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

public sealed record SamplingOptions(
    bool VideoEnabled = true,
    bool AudioEnabled = true,
    TimeSpan? FrameInterval = null,   // default 1 second if null
    TimeSpan? AudioWindow = null);    // default 200 milliseconds if null

public sealed class FrameAudioSampler
{
    private readonly string _ffmpegPath;

    public FrameAudioSampler(string ffmpegPath = "ffmpeg")
    {
        _ffmpegPath = ffmpegPath ?? throw new ArgumentNullException(nameof(ffmpegPath));
    }

    public async IAsyncEnumerable<FrameLuminanceSample> SampleVideoAsync(
        string filePath,
        SamplingOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Apply defaults to options
        options ??= new SamplingOptions();

        // If video is disabled, return immediately without launching ffmpeg
        if (!options.VideoEnabled)
        {
            yield break;
        }

        await foreach (var sample in SampleVideoInternalAsync(filePath, options, cancellationToken))
        {
            yield return sample;
        }
    }

    private async IAsyncEnumerable<FrameLuminanceSample> SampleVideoInternalAsync(
        string filePath,
        SamplingOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var frameInterval = options.FrameInterval ?? TimeSpan.FromSeconds(1.0);
        double fps = 1.0 / frameInterval.TotalSeconds;

        // Format fps as a decimal number for ffmpeg
        string fpsStr = fps.ToString("G", CultureInfo.InvariantCulture);

        // Build ffmpeg command for video sampling
        string arguments = $"-v quiet -i \"{filePath}\" -vf \"fps={fpsStr},signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-\" -f null -";

        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start ffmpeg process");
            }

            // Read stderr in background to avoid deadlock
            var stderrTask = process.StandardError.ReadToEndAsync();

            // Parse stdout line by line
            string? frameLine = null;
            while (true)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    // Kill the process and clean up on cancellation
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        try
                        {
                            await process.WaitForExitAsync(CancellationToken.None);
                        }
                        catch { }
                    }
                    throw;
                }

                // Read the next line from stdout
                var line = await process.StandardOutput.ReadLineAsync();

                if (line == null)
                {
                    // EOF reached; wait for process to exit
                    await process.WaitForExitAsync(cancellationToken);
                    break;
                }

                // Check if this is a frame line (contains "frame:" and "pts_time:")
                if (line.StartsWith("frame:", StringComparison.Ordinal) && line.Contains("pts_time:"))
                {
                    frameLine = line;
                }
                else if (frameLine != null && line.StartsWith("lavfi.signalstats.YAVG=", StringComparison.Ordinal))
                {
                    // Parse the frame line for pts_time
                    double ptsTime = ExtractPtsTime(frameLine);
                    var capturedAt = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(ptsTime);

                    // Parse the YAVG value (0-255 scale)
                    string yavgStr = line.Substring("lavfi.signalstats.YAVG=".Length).Trim();
                    if (double.TryParse(yavgStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double yavg))
                    {
                        // Normalize to [0, 1]
                        double averageLuminance = yavg / 255.0;
                        yield return new FrameLuminanceSample(averageLuminance, capturedAt);
                    }

                    frameLine = null;
                }
            }

            // Check exit code
            if (process.ExitCode != 0)
            {
                await stderrTask;
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode} while sampling video from {filePath}");
            }
        }
        finally
        {
            process?.Dispose();
        }
    }

    public async IAsyncEnumerable<AudioLoudnessSample> SampleAudioAsync(
        string filePath,
        SamplingOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Apply defaults to options
        options ??= new SamplingOptions();

        // If audio is disabled, return immediately without launching ffmpeg
        if (!options.AudioEnabled)
        {
            yield break;
        }

        await foreach (var sample in SampleAudioInternalAsync(filePath, options, cancellationToken))
        {
            yield return sample;
        }
    }

    private async IAsyncEnumerable<AudioLoudnessSample> SampleAudioInternalAsync(
        string filePath,
        SamplingOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var audioWindow = options.AudioWindow ?? TimeSpan.FromMilliseconds(200);

        // Use 48000 Hz as the fixed assumed sample rate (per issue spec)
        const int sampleRateHz = 48000;
        int windowSamples = (int)(audioWindow.TotalSeconds * sampleRateHz);

        // Build ffmpeg command for audio sampling
        string arguments = $"-v quiet -i \"{filePath}\" -af \"asetnsamples=n={windowSamples}:p=0,astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level:file=-\" -f null -";

        Process? process = null;
        double? previousLevel = null;

        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start ffmpeg process");
            }

            // Read stderr in background to avoid deadlock
            var stderrTask = process.StandardError.ReadToEndAsync();

            // Parse stdout line by line
            string? frameLine = null;
            while (true)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    // Kill the process and clean up on cancellation
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        try
                        {
                            await process.WaitForExitAsync(CancellationToken.None);
                        }
                        catch { }
                    }
                    throw;
                }

                // Read the next line from stdout
                var line = await process.StandardOutput.ReadLineAsync();

                if (line == null)
                {
                    // EOF reached; wait for process to exit
                    await process.WaitForExitAsync(cancellationToken);
                    break;
                }

                // Check if this is a frame line (contains "frame:" and "pts_time:")
                if (line.StartsWith("frame:", StringComparison.Ordinal) && line.Contains("pts_time:"))
                {
                    frameLine = line;
                }
                else if (frameLine != null && line.StartsWith("lavfi.astats.Overall.RMS_level=", StringComparison.Ordinal))
                {
                    // Parse the frame line for pts_time
                    double ptsTime = ExtractPtsTime(frameLine);
                    var capturedAt = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(ptsTime);

                    // Parse the RMS level value (in dBFS, negative)
                    string rmsStr = line.Substring("lavfi.astats.Overall.RMS_level=".Length).Trim();
                    if (double.TryParse(rmsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double currentLevel))
                    {
                        // Compute delta from previous level (0.0 for first window)
                        double loudnessDeltaDb = previousLevel.HasValue ? currentLevel - previousLevel.Value : 0.0;

                        yield return new AudioLoudnessSample(loudnessDeltaDb, capturedAt);

                        previousLevel = currentLevel;
                    }

                    frameLine = null;
                }
            }

            // Check exit code
            if (process.ExitCode != 0)
            {
                await stderrTask;
                throw new InvalidOperationException(
                    $"ffmpeg exited with code {process.ExitCode} while sampling audio from {filePath}");
            }
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static double ExtractPtsTime(string frameLine)
    {
        // Example: "frame:0    pts:480     pts_time:0.00533333"
        // Extract the pts_time value
        const string marker = "pts_time:";
        int startIndex = frameLine.IndexOf(marker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            throw new InvalidOperationException($"Could not find 'pts_time:' in frame line: {frameLine}");
        }

        startIndex += marker.Length;
        int endIndex = startIndex;
        while (endIndex < frameLine.Length && (char.IsDigit(frameLine[endIndex]) || frameLine[endIndex] == '.' || frameLine[endIndex] == '-'))
        {
            endIndex++;
        }

        string ptsTimeStr = frameLine.Substring(startIndex, endIndex - startIndex).Trim();
        if (!double.TryParse(ptsTimeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double ptsTime))
        {
            throw new InvalidOperationException($"Could not parse pts_time value: {ptsTimeStr}");
        }

        return ptsTime;
    }
}
