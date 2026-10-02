namespace SmartTVRelay.Core.Tests.Catalog;

using SmartTVRelay.Core.Catalog;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

/// <summary>
/// Fixture tests for <see cref="MediaCatalogScanner"/>. These generate tiny real media files with
/// ffmpeg at test setup time (rather than relying on checked-in binary fixtures) because the
/// scanner operates over a whole directory's contents, not a single named file -- matching the
/// approach used by this repo's other ffmpeg/ffprobe-backed tests (see
/// FrameAudioSamplerTests/TransportStreamInspectorTests for the single-fixture pattern this
/// complements). <see cref="TransportStreamInspector"/> is a sealed, non-virtual class with no
/// extracted interface, so it is not easily substitutable -- these tests exercise the real
/// ffprobe subprocess path end to end instead of mocking it.
/// </summary>
[Collection("Subprocess")]
public class MediaCatalogScannerTests : IDisposable
{
    private readonly string _tempDir;

    public MediaCatalogScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "smarttvrelay-catalog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leaked temp dir is harmless across test runs.
        }
    }

    /// <summary>Generates a tiny (~1 second) valid H.264/AAC mp4 via ffmpeg's lavfi test sources.</summary>
    private static async Task CreateValidClipAsync(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = "-y -f lavfi -i testsrc=duration=1:size=64x64:rate=5 " +
                        "-f lavfi -i sine=frequency=1000:duration=1 " +
                        $"-c:v libx264 -c:a aac -shortest \"{path}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stderr = await stderrTask;
        await stdoutTask;

        if (process.ExitCode != 0 || !File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Failed to generate test fixture at '{path}' (ffmpeg exit code {process.ExitCode}). stderr: {stderr}");
        }
    }

    [Fact]
    public async Task ScanAsync_DirectoryWithOneValidMediaFile_ReturnsOneEntryZeroInvalidWithCorrectMetadata()
    {
        // Arrange
        var filePath = Path.Combine(_tempDir, "valid.mp4");
        await CreateValidClipAsync(filePath);
        var scanner = new MediaCatalogScanner();

        // Act
        var result = await scanner.ScanAsync(_tempDir, CancellationToken.None);

        // Assert
        Assert.Single(result.Entries);
        Assert.Empty(result.Invalid);

        var entry = result.Entries[0];
        Assert.Equal(filePath, entry.Path);
        Assert.Equal(filePath, entry.Id); // deterministic id derived from path, not a random Guid
        Assert.InRange(entry.Duration.TotalSeconds, 0.5, 2.0);
        Assert.Equal("h264", entry.Codec.VideoCodec);
        Assert.Equal("aac", entry.Codec.AudioCodec);
        Assert.Contains("mp4", entry.Codec.Container);
        Assert.Empty(entry.Tags);
        Assert.True(entry.Enabled);
    }

    [Fact]
    public async Task ScanAsync_NonMediaJunkFile_IsSkippedNotCountedInvalid()
    {
        // Arrange: a .txt file has no plausible-media extension, so per this scanner's documented
        // policy it is skipped entirely -- not probed, and not reported as an invalid entry.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "readme.txt"), "not media");
        var scanner = new MediaCatalogScanner();

        // Act
        var result = await scanner.ScanAsync(_tempDir, CancellationToken.None);

        // Assert
        Assert.Empty(result.Entries);
        Assert.Empty(result.Invalid);
    }

    [Fact]
    public async Task ScanAsync_GarbageBytesWithMediaExtension_RecordedInvalidValidFilesStillScanned()
    {
        // Arrange: one real clip and one file that merely has a media extension but is garbage
        // ffprobe cannot parse -- the bad file must not abort the scan of the good one.
        var validPath = Path.Combine(_tempDir, "valid.mp4");
        await CreateValidClipAsync(validPath);

        var garbagePath = Path.Combine(_tempDir, "garbage.mp4");
        await File.WriteAllBytesAsync(garbagePath, [0x00, 0x01, 0x02, 0x03, 0x04]);

        var scanner = new MediaCatalogScanner();

        // Act
        var result = await scanner.ScanAsync(_tempDir, CancellationToken.None);

        // Assert
        Assert.Single(result.Entries);
        Assert.Equal(validPath, result.Entries[0].Path);

        Assert.Single(result.Invalid);
        Assert.Equal(garbagePath, result.Invalid[0].Path);
        Assert.False(string.IsNullOrWhiteSpace(result.Invalid[0].Reason));
    }

    [Fact]
    public async Task ScanAsync_SameDirectoryTwice_YieldsSameOrderBothTimes()
    {
        // Arrange: create files in an order that does NOT match sorted order, so a passing test
        // actually exercises the explicit sort rather than coincidentally matching filesystem
        // enumeration order.
        await CreateValidClipAsync(Path.Combine(_tempDir, "c-clip.mp4"));
        await CreateValidClipAsync(Path.Combine(_tempDir, "a-clip.mp4"));
        await CreateValidClipAsync(Path.Combine(_tempDir, "b-clip.mp4"));
        var scanner = new MediaCatalogScanner();

        // Act
        var first = await scanner.ScanAsync(_tempDir, CancellationToken.None);
        var second = await scanner.ScanAsync(_tempDir, CancellationToken.None);

        // Assert
        var firstPaths = first.Entries.Select(e => e.Path).ToList();
        var secondPaths = second.Entries.Select(e => e.Path).ToList();
        Assert.Equal(3, firstPaths.Count);
        Assert.Equal(firstPaths, secondPaths);
        Assert.Equal(firstPaths.OrderBy(p => p, StringComparer.Ordinal).ToList(), firstPaths);
    }

    [Fact]
    public async Task ScanAsync_EmptyDirectory_ReturnsEmptyEntriesAndInvalidNoException()
    {
        // Arrange
        var scanner = new MediaCatalogScanner();

        // Act
        var result = await scanner.ScanAsync(_tempDir, CancellationToken.None);

        // Assert
        Assert.Empty(result.Entries);
        Assert.Empty(result.Invalid);
    }

    [Fact]
    public async Task ScanAsync_NonexistentDirectory_ReturnsEmptyResultNoException()
    {
        // Arrange
        var scanner = new MediaCatalogScanner();
        var missingDir = Path.Combine(_tempDir, "does-not-exist");

        // Act
        var result = await scanner.ScanAsync(missingDir, CancellationToken.None);

        // Assert
        Assert.Empty(result.Entries);
        Assert.Empty(result.Invalid);
    }
}
