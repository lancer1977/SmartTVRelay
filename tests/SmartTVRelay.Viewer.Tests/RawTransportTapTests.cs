using System.Text;

namespace SmartTVRelay.Viewer.Tests;

public sealed class RawTransportTapTests
{
    [Fact]
    public async Task CopyAsync_feeds_ffmpeg_exactly()
    {
        byte[] input = CreateBytes(188 * 23 + 17);
        using var source = new MemoryStream(input);
        using var ffmpeg = new MemoryStream();
        string directory = NewDirectory();

        await new RawTransportTap().CopyAsync(source, ffmpeg, directory);

        Assert.Equal(input, ffmpeg.ToArray());
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Captures_are_packet_aligned_and_partial_tail_is_not_completed()
    {
        const int targetBytes = (2 * 1024 * 1024 / 188) * 188;
        byte[] input = CreateBytes(targetBytes + 7);
        using var source = new MemoryStream(input);
        using var ffmpeg = new MemoryStream();
        string directory = NewDirectory();

        await new RawTransportTap().CopyAsync(source, ffmpeg, directory);

        var files = Directory.GetFiles(directory, "raw-*.ts");
        Assert.Single(files);
        Assert.Equal(targetBytes, new FileInfo(files[0]).Length);
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Retains_at_most_sixteen_completed_segments()
    {
        const int targetBytes = (2 * 1024 * 1024 / 188) * 188;
        byte[] input = CreateBytes(targetBytes * 17);
        using var source = new MemoryStream(input);
        using var ffmpeg = new MemoryStream();
        string directory = NewDirectory();

        await new RawTransportTap().CopyAsync(source, ffmpeg, directory);

        Assert.Equal(16, Directory.GetFiles(directory, "raw-*.ts").Length);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Cancellation_removes_partial_capture()
    {
        using var source = new BlockingStream(CreateBytes(188 * 1000));
        using var ffmpeg = new MemoryStream();
        string directory = NewDirectory();
        using var cancellation = new CancellationTokenSource();

        Task copy = new RawTransportTap().CopyAsync(source, ffmpeg, directory, cancellation.Token);
        await source.Started;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Evidence_write_failure_is_fail_open_for_ffmpeg_feed()
    {
        byte[] input = Encoding.UTF8.GetBytes("feed survives evidence failure");
        using var source = new MemoryStream(input);
        using var ffmpeg = new MemoryStream();
        string evidencePath = Path.Combine(Path.GetTempPath(), "raw-tap-file-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(evidencePath, "not a directory");

        try
        {
            await new RawTransportTap().CopyAsync(source, ffmpeg, evidencePath);
            Assert.Equal(input, ffmpeg.ToArray());
        }
        finally
        {
            File.Delete(evidencePath);
        }
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "raw-tap-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] CreateBytes(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i * 31 + 7);
        return bytes;
    }

    private sealed class BlockingStream(byte[] data) : MemoryStream(data)
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
