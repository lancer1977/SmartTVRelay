using System.Globalization;
using Microsoft.Extensions.Options;
using Observation.Core;
using SmartTVRelay.Core;
using SmartTVRelay.Core.Ingest;

namespace SmartTVRelay.Viewer.State;

/// <summary>
/// Basic production evidence source: concatenates the newest captured HLS segments into a window
/// file and runs the existing cheap detectors over it (SCTE-35 markers via
/// <see cref="Scte35MarkerExtractor"/> + <see cref="ExplicitMarkerDetector"/>, and
/// <see cref="BlackFrameDetector"/> over <see cref="FrameAudioSampler"/> luminance samples).
/// Re-analyses only when the window changed. Any failure yields no evidence (=> Unknown).
/// </summary>
public sealed class SegmentEvidenceSource(IOptions<ViewerOptions> viewer, IOptions<ChannelStateOptions> state, TimeProvider time)
    : IChannelEvidenceSource
{
    private const string WindowFileName = "state-window.tmp"; // not *.ts: must never be servable via /hls
    private readonly Dictionary<string, (string Key, IReadOnlyList<Observation<BroadcastState>> Result)> _last = new();

    public async Task<IReadOnlyList<Observation<BroadcastState>>> GetObservationsAsync(
        string guideNumber, string workDirectory, CancellationToken cancellationToken)
    {
        var empty = Array.Empty<Observation<BroadcastState>>();
        try
        {
            var segments = new DirectoryInfo(workDirectory).EnumerateFiles("seg*.ts")
                .OrderBy(f => f.Name, StringComparer.Ordinal).TakeLast(Math.Max(1, state.Value.WindowSegments)).ToArray();
            if (segments.Length == 0) return empty;

            var key = string.Join('|', segments.Select(s => $"{s.Name}:{s.Length}"));
            lock (_last) { if (_last.TryGetValue(guideNumber, out var c) && c.Key == key) return c.Result; }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, state.Value.AnalysisTimeoutSeconds)));
            var windowFile = Path.Combine(workDirectory, WindowFileName);
            await using (var output = new FileStream(windowFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var seg in segments)
                {
                    await using var input = new FileStream(seg.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    await input.CopyToAsync(output, timeout.Token);
                }
            }

            var now = time.GetUtcNow();
            var sourceId = $"viewer-{guideNumber}";
            var observations = new List<Observation<BroadcastState>>();

            // Explicit markers: only the most recent signal in the window speaks for the channel.
            var marker = new Scte35MarkerExtractor().Extract(windowFile, timeout.Token).LastOrDefault();
            if (marker is not null)
                observations.AddRange(new ExplicitMarkerDetector().Detect(sourceId, marker with { ObservedAt = now }));

            // Black frames: sampler timestamps are stream-relative; re-anchor so the last frame is "now".
            var samples = new List<FrameLuminanceSample>();
            await foreach (var s in new FrameAudioSampler(viewer.Value.FfmpegPath).SampleVideoAsync(
                windowFile, new SamplingOptions(AudioEnabled: false, FrameInterval: TimeSpan.FromMilliseconds(500)), timeout.Token))
                samples.Add(s);
            if (samples.Count > 0)
            {
                var end = samples[^1].CapturedAt;
                var anchored = samples.Select(s => s with { CapturedAt = now - (end - s.CapturedAt) }).ToArray();
                observations.AddRange(new BlackFrameDetector(minimumTransitionDuration: TimeSpan.FromSeconds(1)).Detect(sourceId, anchored));
            }

            lock (_last) _last[guideNumber] = (key, observations);
            return observations;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return empty; } // fail open: no evidence => Unknown
    }
}
