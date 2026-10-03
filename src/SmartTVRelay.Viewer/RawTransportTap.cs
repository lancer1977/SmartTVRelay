namespace SmartTVRelay.Viewer;

/// <summary>Fans a raw MPEG-TS stream out to ffmpeg and a bounded evidence capture.</summary>
public sealed class RawTransportTap
{
    private const int TsPacketSize = 188;
    private const int SegmentTargetBytes = (2 * 1024 * 1024 / TsPacketSize) * TsPacketSize;
    private const int RetainedSegments = 16;

    /// <summary>
    /// Copies the source byte-for-byte to ffmpeg while best-effort writing packet-aligned captures.
    /// Capture I/O is deliberately fail-open: it can never interrupt the live ffmpeg feed.
    /// </summary>
    public async Task CopyAsync(
        Stream source,
        Stream ffmpegInput,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ffmpegInput);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var capture = new CaptureWriter(outputDirectory);
        var buffer = new byte[64 * 1024];

        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                // This write is the primary path. Evidence work below must not affect it.
                await ffmpegInput.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                capture.Accept(buffer.AsSpan(0, read));
            }

            await ffmpegInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            capture.Dispose();
        }
    }

    private sealed class CaptureWriter
    {
        private readonly string _directory;
        private readonly byte[] _pending = new byte[TsPacketSize];
        private int _pendingCount;
        private FileStream? _file;
        private string? _partialPath;
        private int _segmentBytes;
        private long _sequence;
        private bool _disabled;

        public CaptureWriter(string directory) => _directory = directory;

        public void Accept(ReadOnlySpan<byte> bytes)
        {
            if (_disabled)
                return;

            try
            {
                while (!bytes.IsEmpty)
                {
                    if (_pendingCount != 0)
                    {
                        int take = Math.Min(TsPacketSize - _pendingCount, bytes.Length);
                        bytes[..take].CopyTo(_pending.AsSpan(_pendingCount));
                        _pendingCount += take;
                        bytes = bytes[take..];
                        if (_pendingCount != TsPacketSize)
                            return;

                        WritePacket(_pending);
                        _pendingCount = 0;
                    }

                    int completeBytes = bytes.Length - (bytes.Length % TsPacketSize);
                    for (int offset = 0; offset < completeBytes; offset += TsPacketSize)
                        WritePacket(bytes.Slice(offset, TsPacketSize));

                    bytes = bytes[completeBytes..];
                    if (!bytes.IsEmpty)
                    {
                        bytes.CopyTo(_pending);
                        _pendingCount = bytes.Length;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Disable();
            }
        }

        private void WritePacket(ReadOnlySpan<byte> packet)
        {
            if (_file is null)
                OpenSegment();

            _file!.Write(packet);
            _segmentBytes += packet.Length;
            if (_segmentBytes == SegmentTargetBytes)
                CompleteSegment();
        }

        private void OpenSegment()
        {
            Directory.CreateDirectory(_directory);
            PruneCompletedSegments();
            string stem = $"raw-{DateTime.UtcNow.Ticks:x16}-{_sequence++:x4}";
            _partialPath = Path.Combine(_directory, stem + ".partial");
            _file = new FileStream(_partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                bufferSize: 64 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            _segmentBytes = 0;
        }

        private void CompleteSegment()
        {
            string partialPath = _partialPath!;
            _file!.Flush(flushToDisk: true);
            _file.Dispose();
            _file = null;
            _partialPath = null;

            string completePath = Path.ChangeExtension(partialPath, ".ts");
            File.Move(partialPath, completePath);
            _segmentBytes = 0;
            PruneCompletedSegments();
        }

        private void PruneCompletedSegments()
        {
            var files = new DirectoryInfo(_directory).GetFiles("raw-*.ts")
                .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(RetainedSegments)
                .ToArray();
            foreach (var file in files)
            {
                try { file.Delete(); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private void Disable()
        {
            _disabled = true;
            try { _file?.Dispose(); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _file = null;
            if (_partialPath is not null)
            {
                try { File.Delete(_partialPath); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _partialPath = null;
        }

        public void Dispose()
        {
            if (_file is not null || _partialPath is not null)
                Disable();
        }
    }
}
