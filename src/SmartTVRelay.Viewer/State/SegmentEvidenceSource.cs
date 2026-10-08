using Microsoft.Extensions.Options;
using Observation.Core;
using SmartTVRelay.Core;
using SmartTVRelay.Core.Ingest;

namespace SmartTVRelay.Viewer.State;

/// <summary>
/// Analyses completed raw tuner MPEG-TS windows. The HLS transcode is never used as detector
/// input: it can discard SCTE-35 and other original-stream evidence. A marker is applied only
/// after its PTS is reached by the transport PCR, and is held briefly while fresh raw data
/// continues. Missing timing or stale capture yields no marker evidence.
/// </summary>
public sealed class SegmentEvidenceSource(IOptions<ViewerOptions> viewer, IOptions<ChannelStateOptions> state, TimeProvider time)
    : IChannelEvidenceSource
{
    private const string WindowFileName = "state-window.tmp"; // not servable through /hls
    private sealed record MarkerMemory(string Directory, ExplicitMarkerKind Kind, long Pts, DateTimeOffset FirstSeen);

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Key, IReadOnlyList<Observation<BroadcastState>> Result)> _last = new();
    private readonly Dictionary<string, MarkerMemory> _markers = new();

    public async Task<IReadOnlyList<Observation<BroadcastState>>> GetObservationsAsync(
        string guideNumber, string workDirectory, CancellationToken cancellationToken)
    {
        var empty = Array.Empty<Observation<BroadcastState>>();
        try
        {
            var segments = new DirectoryInfo(workDirectory).EnumerateFiles("raw-*.ts")
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .TakeLast(Math.Max(1, state.Value.WindowSegments)).ToArray();
            if (segments.Length == 0) return empty;

            var key = workDirectory + "|" + string.Join('|', segments.Select(s => $"{s.Name}:{s.Length}"));
            lock (_gate)
            {
                if (_last.TryGetValue(guideNumber, out var cached) && cached.Key == key) return cached.Result;
                if (_markers.TryGetValue(guideNumber, out var memory) && memory.Directory != workDirectory)
                    _markers.Remove(guideNumber);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, state.Value.AnalysisTimeoutSeconds)));
            var windowFile = Path.Combine(workDirectory, WindowFileName);
            await using (var output = new FileStream(windowFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var segment in segments)
                {
                    await using var input = new FileStream(segment.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    await input.CopyToAsync(output, timeout.Token);
                }
            }

            var now = time.GetUtcNow();
            var sourceId = $"viewer-{guideNumber}";
            var observations = new List<Observation<BroadcastState>>();

            // A splice PTS is stream-relative, not wall time. Never re-anchor an uncorrelated
            // future cue to now: a premature Commercial report would violate fail-open policy.
            var latestPcr = MpegTsClock.FindLastPcrBase(windowFile, timeout.Token);
            if (latestPcr is { } pcr)
            {
                var reached = new Scte35MarkerExtractor().Extract(windowFile, timeout.Token)
                    .Where(marker => MpegTsClock.HasReached(pcr, MpegTsClock.ToPts(marker.ObservedAt)))
                    .ToArray();
                var due = reached.LastOrDefault();
                // A CueOut and a CueIn at the same PTS contradict each other; "last" would only be
                // file order. Never pick one: report no marker evidence (fail open).
                var contradictory = due is not null && reached.Any(m =>
                    m.Kind != due.Kind && MpegTsClock.ToPts(m.ObservedAt) == MpegTsClock.ToPts(due.ObservedAt));
                lock (_gate)
                {
                    if (contradictory)
                    {
                        _markers.Remove(guideNumber);
                    }
                    else if (due is not null)
                    {
                        var pts = MpegTsClock.ToPts(due.ObservedAt);
                        if (!_markers.TryGetValue(guideNumber, out var previous)
                            || previous.Directory != workDirectory || previous.Kind != due.Kind || previous.Pts != pts)
                            _markers[guideNumber] = new MarkerMemory(workDirectory, due.Kind, pts, now);
                    }

                    if (_markers.TryGetValue(guideNumber, out var active))
                    {
                        if (now - active.FirstSeen <= TimeSpan.FromSeconds(Math.Max(1, state.Value.MaxMarkerHoldSeconds)))
                            observations.AddRange(new ExplicitMarkerDetector().Detect(
                                sourceId, new ExplicitMarkerSignal(active.Kind, now)));
                        else
                            _markers.Remove(guideNumber);
                    }
                }
            }
            else
            {
                lock (_gate) _markers.Remove(guideNumber);
            }

            // A video sampler failure cannot erase a valid, timed explicit marker. Black-frame
            // evidence remains weak on its own and cannot authorize a Commercial state.
            try
            {
                var samples = new List<FrameLuminanceSample>();
                await foreach (var sample in new FrameAudioSampler(viewer.Value.FfmpegPath).SampleVideoAsync(
                    windowFile, new SamplingOptions(AudioEnabled: false, FrameInterval: TimeSpan.FromMilliseconds(500)), timeout.Token))
                    samples.Add(sample);
                if (samples.Count > 0)
                {
                    var end = samples[^1].CapturedAt;
                    var anchored = samples.Select(sample => sample with { CapturedAt = now - (end - sample.CapturedAt) }).ToArray();
                    observations.AddRange(new BlackFrameDetector(minimumTransitionDuration: TimeSpan.FromSeconds(1)).Detect(sourceId, anchored));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { /* marker observations remain valid; video evidence is omitted */ }

            lock (_gate) _last[guideNumber] = (key, observations);
            return observations;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            lock (_gate) _markers.Remove(guideNumber);
            return empty; // fail open
        }
    }
}
