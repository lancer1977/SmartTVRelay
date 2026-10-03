namespace SmartTVRelay.Core.Ingest;

/// <summary>
/// Inspects one bounded, complete MPEG-TS window from a live source. The inspection runs away
/// from the stream read loop so a slow caption decoder cannot stall delivery of media chunks.
/// </summary>
internal sealed class LiveAvailabilityWindow : IDisposable
{
    private const int PacketSize = 188;
    private const int MaxBytes = (2 * 1024 * 1024 / PacketSize) * PacketSize;
    private readonly object gate = new();
    private MemoryStream? capture = new(MaxBytes);
    private readonly CancellationTokenSource inspectionCancellation;
    private readonly CancellationToken inspectionToken;
    private Task<(bool? CaptionsAvailable, bool? MarkersAvailable)>? inspection;
    private bool disposed;

    public LiveAvailabilityWindow(CancellationToken cancellationToken)
    {
        inspectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inspectionToken = inspectionCancellation.Token;
    }

    public void Add(ReadOnlyMemory<byte> bytes)
    {
        lock (gate)
        {
            if (capture is null || inspection is not null) return;
            var remaining = MaxBytes - (int)capture.Length;
            capture.Write(bytes.Span[..Math.Min(remaining, bytes.Length)]);
            if (capture.Length == MaxBytes) StartInspection();
        }
    }

    public (bool? CaptionsAvailable, bool? MarkersAvailable) Snapshot()
    {
        Task<(bool? CaptionsAvailable, bool? MarkersAvailable)>? task;
        lock (gate) task = inspection;
        return task is { IsCompletedSuccessfully: true } ? task.Result : (null, null);
    }

    public async Task CompleteAsync()
    {
        Task<(bool? CaptionsAvailable, bool? MarkersAvailable)>? task;
        lock (gate)
        {
            if (inspection is null) StartInspection();
            task = inspection;
        }
        if (task is not null) await task.ConfigureAwait(false);
    }

    private void StartInspection()
    {
        if (inspection is not null || capture is null || capture.Length == 0) return;
        var bytes = capture.ToArray();
        capture.Dispose();
        capture = null;
        inspection = Task.Run(() => InspectAsync(bytes, inspectionToken));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            capture?.Dispose();
            capture = null;
            inspectionCancellation.Cancel();
            inspectionCancellation.Dispose();
        }
    }

    private static async Task<(bool? CaptionsAvailable, bool? MarkersAvailable)> InspectAsync(
        byte[] bytes, CancellationToken cancellationToken)
    {
        // An incomplete packet or broken framing cannot establish verified absence.
        if (bytes.Length % PacketSize != 0) return (null, null);

        var directory = Path.Combine(Path.GetTempPath(), $"smarttv-live-availability-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "window.ts");
        try
        {
            Directory.CreateDirectory(directory);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            return await RecordedAvailabilityInspector.InspectAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (null, null);
        }
        catch (Exception)
        {
            return (null, null);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { Directory.Delete(directory); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
