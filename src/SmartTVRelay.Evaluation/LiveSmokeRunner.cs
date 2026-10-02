using SmartTVRelay.Core.Ingest;

namespace SmartTVRelay.Evaluation;

public sealed record LiveSmokeResult(int ExitCode, string Summary, IngestDiagnostics Diagnostics, int MarkerCount, int CaptionCount, bool VideoFound, string CapturePath);

public sealed class LiveSmokeRunner
{
    public async Task<LiveSmokeResult> RunAsync(EvaluationCliOptions options, HttpClient client, CancellationToken cancellationToken = default)
    {
        var directory = options.OutputDirectory == "artifacts/evaluation"
            ? Path.Combine(Path.GetTempPath(), "smarttvrelay-live-smoke-" + Guid.NewGuid().ToString("N"))
            : options.OutputDirectory;
        Directory.CreateDirectory(directory);
        var capturePath = Path.Combine(directory, "live-smoke.ts");
        await using var source = new HdHomeRunMediaSource(client, "live-smoke", options.Host!, options.Channel!);
        var failure = "none";
        try
        {
            using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            duration.CancelAfter(TimeSpan.FromSeconds(options.Seconds!.Value));
            await using var capture = File.Create(capturePath);
            try
            {
                await foreach (var chunk in source.ReadAsync(duration.Token).ConfigureAwait(false))
                    await capture.WriteAsync(chunk.Data, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await capture.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex.Message; }

        var diagnostics = source.Diagnostics;
        var markers = 0;
        var captionCount = 0;
        var videoFound = false;
        string? inspectionFailure = null;
        string? evidenceNote = null;
        if (diagnostics.BytesProcessed > 0)
        {
            try { videoFound = (await new TransportStreamInspector().InspectAsync(capturePath, cancellationToken).ConfigureAwait(false)).Streams.Any(s => s.CodecType == "video"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { inspectionFailure = ex.Message; }

            try
            {
                markers = new Scte35MarkerExtractor().Extract(capturePath, cancellationToken).Count;
                captionCount = (await new CaptionExtractor().ExtractAsync(capturePath, cancellationToken).ConfigureAwait(false)).Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { evidenceNote = ex.Message; }
        }

        var exit = diagnostics.BytesProcessed > 0 && videoFound ? 0 : 1;
        var reason = exit == 0 ? "PASS" : failure != "none" ? failure : inspectionFailure ?? (diagnostics.BytesProcessed == 0 ? "no stream bytes received" : "inspector found no video stream");
        var summary = $"Live smoke: {(exit == 0 ? "PASS" : "FAIL — " + reason)}\nHost: {options.Host}  Channel: {options.Channel}\nCapture: {capturePath}\nIngest: bytes={diagnostics.BytesProcessed}, chunks={diagnostics.ChunksProcessed}, status={diagnostics.Health}, reconnects={diagnostics.ReconnectAttempts}\nInspector video: {(videoFound ? "yes" : "no or unknown")}\nSCTE-35 markers: {markers}  Caption samples: {captionCount}" + (evidenceNote is null ? "" : $"\nEvidence extraction unavailable: {evidenceNote}");
        return new LiveSmokeResult(exit, summary, diagnostics, markers, captionCount, videoFound, capturePath);
    }
}
