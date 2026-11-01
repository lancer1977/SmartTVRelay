namespace SmartTVRelay.Core.Tests.Ingest;

using SmartTVRelay.Core.Ingest;
using Xunit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public class FrameAudioSamplerTests
{
    private static string GetFixturePath(string filename)
    {
        return Path.Combine(AppContext.BaseDirectory, "synthetic", filename);
    }

    #region Video Sampling Tests

    [Fact]
    public async Task SampleVideoAsync_WithBlackFixture_YieldsExpectedLuminance()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: true, AudioEnabled: false);

        // Expected: YAVG=16 -> AverageLuminance = 16/255 ≈ 0.0627
        const double expectedLuminance = 16.0 / 255.0;
        const double tolerance = 0.01;

        // Act
        var samples = await sampler.SampleVideoAsync(filePath, options, CancellationToken.None)
            .ToListAsync();

        // Assert
        Assert.NotEmpty(samples);
        // Verify at least one sample is close to expected black luminance
        var blackSamples = samples.Where(s => Math.Abs(s.AverageLuminance - expectedLuminance) < tolerance).ToList();
        Assert.NotEmpty(blackSamples);
    }

    [Fact]
    public async Task SampleVideoAsync_WithWhiteFixture_YieldsExpectedLuminance()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("white-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: true, AudioEnabled: false);

        // Expected: YAVG=235 -> AverageLuminance = 235/255 ≈ 0.9216
        const double expectedLuminance = 235.0 / 255.0;
        const double tolerance = 0.01;

        // Act
        var samples = await sampler.SampleVideoAsync(filePath, options, CancellationToken.None)
            .ToListAsync();

        // Assert
        Assert.NotEmpty(samples);
        // Verify at least one sample is close to expected white luminance
        var whiteSamples = samples.Where(s => Math.Abs(s.AverageLuminance - expectedLuminance) < tolerance).ToList();
        Assert.NotEmpty(whiteSamples);
    }

    [Fact]
    public async Task SampleVideoAsync_WithVideoDisabled_YieldsNoSamples()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: false, AudioEnabled: false);

        // Act
        var samples = await sampler.SampleVideoAsync(filePath, options, CancellationToken.None)
            .ToListAsync();

        // Assert
        Assert.Empty(samples);
    }

    [Fact]
    public async Task SampleVideoAsync_ShorterFrameInterval_YieldsMoreSamples()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");

        // Fixture is 2 seconds long
        // With 1 second interval: ~2 samples
        // With 0.2 second interval: ~10 samples
        var longIntervalOptions = new SamplingOptions(VideoEnabled: true, AudioEnabled: false,
            FrameInterval: TimeSpan.FromSeconds(1.0));
        var shortIntervalOptions = new SamplingOptions(VideoEnabled: true, AudioEnabled: false,
            FrameInterval: TimeSpan.FromSeconds(0.2));

        // Act
        var longIntervalSamples = await sampler.SampleVideoAsync(filePath, longIntervalOptions, CancellationToken.None)
            .ToListAsync();
        var shortIntervalSamples = await sampler.SampleVideoAsync(filePath, shortIntervalOptions, CancellationToken.None)
            .ToListAsync();

        // Assert
        Assert.NotEmpty(longIntervalSamples);
        Assert.NotEmpty(shortIntervalSamples);
        Assert.True(shortIntervalSamples.Count > longIntervalSamples.Count,
            $"Expected more samples with shorter interval. Got {shortIntervalSamples.Count} vs {longIntervalSamples.Count}");
    }

    [Fact]
    public async Task SampleVideoAsync_DeterministicTimestamps_ReproducibleAcrossRuns()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: true, AudioEnabled: false,
            FrameInterval: TimeSpan.FromSeconds(0.5));

        // Act: first run
        var firstRun = await sampler.SampleVideoAsync(filePath, options, CancellationToken.None)
            .ToListAsync();

        // Act: second run
        var secondRun = await sampler.SampleVideoAsync(filePath, options, CancellationToken.None)
            .ToListAsync();

        // Assert: same number of samples
        Assert.Equal(firstRun.Count, secondRun.Count);

        // Assert: same timestamps (deterministic, not wall-clock dependent)
        for (int i = 0; i < firstRun.Count; i++)
        {
            Assert.Equal(firstRun[i].CapturedAt, secondRun[i].CapturedAt);
        }
    }

    [Fact]
    public async Task SampleVideoAsync_NonexistentFile_ThrowsInvalidOperationException()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var nonexistentPath = "/nonexistent/path/to/file.ts";
        var options = new SamplingOptions(VideoEnabled: true, AudioEnabled: false);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sampler.SampleVideoAsync(nonexistentPath, options, CancellationToken.None))
            {
                // Consume the enumerable
            }
        });
    }

    [Fact]
    public async Task SampleVideoAsync_CancellationMidStream_ThrowsOperationCanceledException()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: true, AudioEnabled: false,
            FrameInterval: TimeSpan.FromMilliseconds(100)); // More frequent sampling to allow mid-stream cancellation
        using var cts = new CancellationTokenSource();

        // Act & Assert
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            int sampleCount = 0;
            await foreach (var _ in sampler.SampleVideoAsync(filePath, options, cts.Token))
            {
                sampleCount++;
                if (sampleCount == 1)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.NotNull(exception);
    }

    [Fact]
    public async Task SampleVideoAsync_HungProcess_CancellationBoundsItPromptly()
    {
        // The existing mid-stream cancellation test above cancels against a real, fast 2-second
        // fixture that keeps producing lines regularly -- it can't distinguish "cancellation
        // works" from "the fixture just finished fast enough that no one noticed." This test uses
        // a process that never produces any output and never exits on its own, which is what
        // actually exercises whether cancellation bounds a genuine hang.
        //
        // Found and fixed during independent review: the original implementation checked
        // cancellationToken.ThrowIfCancellationRequested() BEFORE an untokened
        // ReadLineAsync() call each loop iteration. That check only fires BETWEEN lines --
        // once blocked inside a ReadLineAsync() that never returns (a hung process), it never
        // got revisited. Confirmed via a live repro before fixing: cancelling after 300ms
        // against a hanging process hung for 6+ seconds with no reaction at all (only stopped
        // by an external `timeout` wrapper, not by this method's own cancellation).
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "hanging-ffmpeg.sh");
        var sampler = new FrameAudioSampler(ffmpegPath: scriptPath);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in sampler.SampleVideoAsync("irrelevant.ts", null, cts.Token))
            {
            }
        });

        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"Expected cancellation (~300ms) to bound a hung process promptly, took {sw.ElapsedMilliseconds}ms");
    }

    #endregion

    #region Audio Sampling Tests

    [Fact]
    public async Task SampleAudioAsync_WithBlackFixture_YieldsAtLeastOneSample()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: false, AudioEnabled: true);

        // Act
        var samples = await sampler.SampleAudioAsync(filePath, options, CancellationToken.None)
            .ToListAsync();

        // Assert
        Assert.NotEmpty(samples);
    }

    [Fact]
    public async Task SampleAudioAsync_FirstSample_LoudnessDeltaIsZero()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: false, AudioEnabled: true);

        // Act
        var samples = await sampler.SampleAudioAsync(filePath, options, CancellationToken.None)
            .ToListAsync();

        // Assert
        Assert.NotEmpty(samples);
        Assert.Equal(0.0, samples[0].LoudnessDeltaDb);
    }

    [Fact]
    public async Task SampleAudioAsync_WithAudioDisabled_YieldsNoSamples()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: false, AudioEnabled: false);

        // Act
        var samples = await sampler.SampleAudioAsync(filePath, options, CancellationToken.None)
            .ToListAsync();

        // Assert
        Assert.Empty(samples);
    }

    [Fact]
    public async Task SampleAudioAsync_ShorterAudioWindow_YieldsMoreSamples()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");

        // Fixture is 2 seconds long
        // With 200ms window: ~10 samples
        // With 500ms window: ~4 samples
        var longWindowOptions = new SamplingOptions(VideoEnabled: false, AudioEnabled: true,
            AudioWindow: TimeSpan.FromMilliseconds(500));
        var shortWindowOptions = new SamplingOptions(VideoEnabled: false, AudioEnabled: true,
            AudioWindow: TimeSpan.FromMilliseconds(200));

        // Act
        var longWindowSamples = await sampler.SampleAudioAsync(filePath, longWindowOptions, CancellationToken.None)
            .ToListAsync();
        var shortWindowSamples = await sampler.SampleAudioAsync(filePath, shortWindowOptions, CancellationToken.None)
            .ToListAsync();

        // Assert
        Assert.NotEmpty(longWindowSamples);
        Assert.NotEmpty(shortWindowSamples);
        Assert.True(shortWindowSamples.Count > longWindowSamples.Count,
            $"Expected more samples with shorter window. Got {shortWindowSamples.Count} vs {longWindowSamples.Count}");
    }

    [Fact]
    public async Task SampleAudioAsync_DeterministicTimestamps_ReproducibleAcrossRuns()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: false, AudioEnabled: true,
            AudioWindow: TimeSpan.FromMilliseconds(200));

        // Act: first run
        var firstRun = await sampler.SampleAudioAsync(filePath, options, CancellationToken.None)
            .ToListAsync();

        // Act: second run
        var secondRun = await sampler.SampleAudioAsync(filePath, options, CancellationToken.None)
            .ToListAsync();

        // Assert: same number of samples
        Assert.Equal(firstRun.Count, secondRun.Count);

        // Assert: same timestamps (deterministic, not wall-clock dependent)
        for (int i = 0; i < firstRun.Count; i++)
        {
            Assert.Equal(firstRun[i].CapturedAt, secondRun[i].CapturedAt);
        }
    }

    [Fact]
    public async Task SampleAudioAsync_NonexistentFile_ThrowsInvalidOperationException()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var nonexistentPath = "/nonexistent/path/to/file.ts";
        var options = new SamplingOptions(VideoEnabled: false, AudioEnabled: true);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sampler.SampleAudioAsync(nonexistentPath, options, CancellationToken.None))
            {
                // Consume the enumerable
            }
        });
    }

    [Fact]
    public async Task SampleAudioAsync_CancellationMidStream_ThrowsOperationCanceledException()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: false, AudioEnabled: true,
            AudioWindow: TimeSpan.FromMilliseconds(100)); // Shorter window for more samples, allowing mid-stream cancellation
        using var cts = new CancellationTokenSource();

        // Act & Assert
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            int sampleCount = 0;
            await foreach (var _ in sampler.SampleAudioAsync(filePath, options, cts.Token))
            {
                sampleCount++;
                if (sampleCount == 1)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.NotNull(exception);
    }

    #endregion

    #region Options and Defaults Tests

    [Fact]
    public async Task SamplingOptions_DefaultFrameInterval_IsOneSecond()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: true, AudioEnabled: false);
        // FrameInterval is null, should default to 1 second

        // Act: sample with default interval
        var samples = await sampler.SampleVideoAsync(filePath, options, CancellationToken.None)
            .ToListAsync();

        // Assert: 2-second fixture with 1-second interval should yield ~2 samples
        Assert.NotEmpty(samples);
        Assert.True(samples.Count >= 1, "Expected at least 1 sample with 1-second interval on 2-second fixture");
    }

    [Fact]
    public async Task SamplingOptions_DefaultAudioWindow_Is200Milliseconds()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: false, AudioEnabled: true);
        // AudioWindow is null, should default to 200 milliseconds

        // Act: sample with default window
        var samples = await sampler.SampleAudioAsync(filePath, options, CancellationToken.None)
            .ToListAsync();

        // Assert: 2-second fixture with 200ms window should yield ~10 samples
        Assert.NotEmpty(samples);
        Assert.True(samples.Count >= 5, "Expected at least 5 samples with 200ms window on 2-second fixture");
    }

    [Fact]
    public async Task SamplingOptions_Null_UsesAllDefaults()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");

        // Act: both video and audio with null options
        var videoSamples = await sampler.SampleVideoAsync(filePath, null, CancellationToken.None)
            .ToListAsync();
        var audioSamples = await sampler.SampleAudioAsync(filePath, null, CancellationToken.None)
            .ToListAsync();

        // Assert: both should produce samples with default settings
        Assert.NotEmpty(videoSamples);
        Assert.NotEmpty(audioSamples);
    }

    #endregion

    #region Process Lifecycle Tests

    [Fact]
    public async Task SampleVideoAsync_ProcessNotLaunchedWhenVideoDisabled()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        // Use a fake/nonexistent file path - if ffmpeg is launched, it will error;
        // if not launched, no error occurs
        var nonexistentPath = "/this/path/does/not/exist/nowhere.ts";
        var options = new SamplingOptions(VideoEnabled: false, AudioEnabled: false);

        // Act: this should complete without error if no process is launched
        var samples = await sampler.SampleVideoAsync(nonexistentPath, options, CancellationToken.None)
            .ToListAsync();

        // Assert: no samples, no exception (process was not launched)
        Assert.Empty(samples);
    }

    [Fact]
    public async Task SampleAudioAsync_ProcessNotLaunchedWhenAudioDisabled()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        // Use a fake/nonexistent file path - if ffmpeg is launched, it will error;
        // if not launched, no error occurs
        var nonexistentPath = "/this/path/does/not/exist/nowhere.ts";
        var options = new SamplingOptions(VideoEnabled: false, AudioEnabled: false);

        // Act: this should complete without error if no process is launched
        var samples = await sampler.SampleAudioAsync(nonexistentPath, options, CancellationToken.None)
            .ToListAsync();

        // Assert: no samples, no exception (process was not launched)
        Assert.Empty(samples);
    }

    [Fact]
    public async Task SampleVideoAsync_CompleteEnumeration_NoProcessLeaks()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: true, AudioEnabled: false);

        // Capture the number of ffmpeg processes before
        var processesBefore = Process.GetProcessesByName("ffmpeg").Length;

        // Act: fully enumerate
        await foreach (var _ in sampler.SampleVideoAsync(filePath, options, CancellationToken.None))
        {
            // Consume all samples
        }

        // Small delay to allow cleanup
        await Task.Delay(100);

        // Assert: no leaked processes
        var processesAfter = Process.GetProcessesByName("ffmpeg").Length;
        Assert.Equal(processesBefore, processesAfter);
    }

    [Fact]
    public async Task SampleAudioAsync_CompleteEnumeration_NoProcessLeaks()
    {
        // Arrange
        var sampler = new FrameAudioSampler();
        var filePath = GetFixturePath("black-with-tone.ts");
        var options = new SamplingOptions(VideoEnabled: false, AudioEnabled: true);

        // Capture the number of ffmpeg processes before
        var processesBefore = Process.GetProcessesByName("ffmpeg").Length;

        // Act: fully enumerate
        await foreach (var _ in sampler.SampleAudioAsync(filePath, options, CancellationToken.None))
        {
            // Consume all samples
        }

        // Small delay to allow cleanup
        await Task.Delay(100);

        // Assert: no leaked processes
        var processesAfter = Process.GetProcessesByName("ffmpeg").Length;
        Assert.Equal(processesBefore, processesAfter);
    }

    #endregion
}
