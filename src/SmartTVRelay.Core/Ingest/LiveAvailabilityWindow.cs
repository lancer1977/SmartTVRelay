namespace SmartTVRelay.Core.Ingest;

/// <summary>
/// Inspects one bounded, complete MPEG-TS window from a live source. The inspection runs away
/// from the stream read loop so a slow caption decoder cannot stall delivery of media chunks.
/// </summary>
internal sealed class LiveAvailabilityWindow
{
    private const int PacketSize = 188;
    private const int MaxBytes = (2 * 1024 * 1024 / PacketSize) * PacketSize;
    private readonly MemoryStream capture = new(MaxBytes);
    private readonly CancellationToken cancellationToken;
    private Task<(bool? CaptionsAvailable, bool? MarkersAvailable)>? inspection;

    public LiveAvailabilityWindow(CancellationToken cancellationToken) => this.cancellationToken = cancellationToken;

    public void Add(ReadOnlyMemory<byte> bytes)
    {
        if (inspection is not null) return;
        var remaining = MaxBytes - (int)capture.Length;
        capture.Write(bytes.Span[..Math.Min(remaining, bytes.Length)]);
        if (capture.Length == MaxBytes) StartInspection();
    }

    public (bool? CaptionsAvailable, bool? MarkersAvailable) Snapshot()
    {
        if (inspection is not { IsCompletedSuccessfully: true }) return (null, null);
        return inspection.Result;
    }

    public async Task CompleteAsync()
    {
        if (inspection is null) StartInspection();
        if (inspection is not null) await inspection.ConfigureAwait(false);
    }

    private void StartInspection()
    {
        if (inspection is not null || capture.Length == 0) return;
        var bytes = capture.ToArray();
        capture.Dispose();
        inspection = Task.Run(() => InspectAsync(bytes, cancellationToken));
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
