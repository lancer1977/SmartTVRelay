using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Observation.Core;
using SmartTVRelay.Core;

namespace SmartTVRelay.Viewer.State;

/// <summary>An SSE subscription: the current state at subscribe time plus later changes.</summary>
public sealed class ChannelStateSubscription(ChannelStateDto initial, ChannelReader<ChannelStateDto> reader, Action dispose) : IDisposable
{
    public ChannelStateDto Initial { get; } = initial;
    public ChannelReader<ChannelStateDto> Reader { get; } = reader;
    public void Dispose() => dispose();
}

/// <summary>
/// Fuses a running channel's evidence through <see cref="BroadcastStateFusion"/> (Observation Core)
/// and publishes state changes. Detectors never reach this layer's decisions; anything unknown,
/// stale, disputed or below threshold is reported as Unknown.
/// </summary>
public sealed class ChannelStateService
{
    private const double UnknownConfidenceCeiling = 0.2;
    private static readonly TimeSpan FutureSkew = TimeSpan.FromSeconds(5);

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ChannelStateDto? Current { get; set; }
        public List<Channel<ChannelStateDto>> Subscribers { get; } = new();
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly IChannelPipelineRegistry _registry;
    private readonly IChannelEvidenceSource _source;
    private readonly ChannelStateOptions _options;
    private readonly TimeProvider _time;
    private readonly BroadcastStateFusion _fusion =
        new(new ObservationFusionOptions<BroadcastState>(minimumConfidenceMargin: 0.2));

    public ChannelStateService(IChannelPipelineRegistry registry, IChannelEvidenceSource source,
        IOptions<ChannelStateOptions> options, TimeProvider time)
    {
        _registry = registry;
        _source = source;
        _options = options.Value;
        _time = time;
    }

    public bool IsRunning(string guideNumber) =>
        ChannelPipelineManager.IsValidGuideNumber(guideNumber) && _registry.GetWorkDirectory(guideNumber) is not null;

    /// <summary>Latest state, evaluating once if none is cached. Null when the channel is not running.</summary>
    public async Task<ChannelStateDto?> GetStateAsync(string guideNumber, CancellationToken ct)
    {
        if (!IsRunning(guideNumber)) return null;
        var entry = GetEntry(guideNumber);
        lock (_lock) { if (entry.Current is { } cached) return cached; }
        return await RefreshAsync(guideNumber, ct);
    }

    /// <summary>Re-evaluates a channel, caches the result and publishes it to subscribers if the state changed.</summary>
    public async Task<ChannelStateDto?> RefreshAsync(string guideNumber, CancellationToken ct)
    {
        var workDir = ChannelPipelineManager.IsValidGuideNumber(guideNumber) ? _registry.GetWorkDirectory(guideNumber) : null;
        if (workDir is null) { Drop(guideNumber); return null; }

        var entry = GetEntry(guideNumber);
        await entry.Gate.WaitAsync(ct);
        try
        {
            IReadOnlyList<Observation<BroadcastState>> observations;
            try { observations = await _source.GetObservationsAsync(guideNumber, workDir, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { observations = Array.Empty<Observation<BroadcastState>>(); } // fail open

            var dto = Evaluate(guideNumber, observations);
            lock (_lock)
            {
                var changed = entry.Current is not null && entry.Current.State != dto.State;
                entry.Current = dto;
                if (changed) foreach (var s in entry.Subscribers) s.Writer.TryWrite(dto);
            }
            return dto;
        }
        finally { entry.Gate.Release(); }
    }

    /// <summary>Pure fusion of observations into the reported state (exposed for tests and reuse).</summary>
    public ChannelStateDto Evaluate(string guideNumber, IEnumerable<Observation<BroadcastState>> observations)
    {
        var now = _time.GetUtcNow();
        var oldest = now - TimeSpan.FromSeconds(_options.EvidenceWindowSeconds);
        var scope = ObservationScope.Of($"channel-{guideNumber}");

        var usable = observations
            .Where(o => o.Subject == BroadcastObservations.Subject && o.Predicate == BroadcastObservations.Predicate)
            .Where(o => o.ObservedAt > oldest && o.ObservedAt <= now + FutureSkew)
            .Select(o => new Observation<BroadcastState>(o.Id, scope, o.Subject, o.Predicate, o.Value,
                o.SourceId, o.SourceKind, o.Confidence, o.ObservedAt))
            .ToArray();

        if (usable.Length == 0) return Unknown(guideNumber, 0.0, Array.Empty<EvidenceDto>(), now);

        var result = _fusion.Fuse(usable).FirstOrDefault(r => r.Scope == scope);
        if (result is null || result.Status is not (FusionStatus.Agreed or FusionStatus.Resolved)
            || result.Value == BroadcastState.Unknown || result.Confidence < _options.ConfidenceThreshold)
        {
            var confidence = result is null ? 0.0 : Math.Min(result.Confidence, UnknownConfidenceCeiling);
            return Unknown(guideNumber, confidence, ToEvidence(usable), now);
        }

        var supporting = usable.Where(o => o.Value == result.Value);
        return new ChannelStateDto(guideNumber, result.Value, Math.Clamp(result.Confidence, 0.0, 1.0), ToEvidence(supporting), now);
    }

    /// <summary>Subscribes to state changes. Null when the channel is not running.</summary>
    public async Task<ChannelStateSubscription?> SubscribeAsync(string guideNumber, CancellationToken ct)
    {
        if (await GetStateAsync(guideNumber, ct) is null) return null;
        var entry = GetEntry(guideNumber);
        var channel = Channel.CreateBounded<ChannelStateDto>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        ChannelStateDto initial;
        lock (_lock)
        {
            entry.Subscribers.Add(channel);
            initial = entry.Current!;
        }
        return new ChannelStateSubscription(initial, channel.Reader, () =>
        {
            lock (_lock) entry.Subscribers.Remove(channel);
            channel.Writer.TryComplete();
        });
    }

    /// <summary>Refreshes every running channel and drops state (ending streams) of stopped ones.</summary>
    public async Task RefreshAllAsync(CancellationToken ct)
    {
        var running = _registry.RunningChannels;
        string[] known;
        lock (_lock) known = _entries.Keys.ToArray();
        foreach (var g in known.Where(g => !running.Contains(g))) Drop(g);
        foreach (var g in running)
        {
            try { await RefreshAsync(g, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* one channel's failure must not stop the others */ }
        }
    }

    private Entry GetEntry(string guideNumber)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(guideNumber, out var e)) _entries[guideNumber] = e = new Entry();
            return e;
        }
    }

    private void Drop(string guideNumber)
    {
        Entry? entry;
        lock (_lock)
        {
            if (!_entries.Remove(guideNumber, out entry)) return;
            foreach (var s in entry.Subscribers) s.Writer.TryComplete();
            entry.Subscribers.Clear();
        }
    }

    private static ChannelStateDto Unknown(string guide, double confidence, IReadOnlyList<EvidenceDto> evidence, DateTimeOffset now) =>
        new(guide, BroadcastState.Unknown, confidence, evidence, now);

    private static IReadOnlyList<EvidenceDto> ToEvidence(IEnumerable<Observation<BroadcastState>> observations) =>
        observations.Select(o => new EvidenceDto(o.SourceId, o.SourceKind)).Distinct().ToArray();
}

/// <summary>Periodically refreshes the state of every running channel.</summary>
public sealed class ChannelStateMonitor(ChannelStateService service, IOptions<ChannelStateOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromMilliseconds(Math.Max(10, options.Value.PollMilliseconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await service.RefreshAllAsync(stoppingToken);
                await Task.Delay(period, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { try { await Task.Delay(period, stoppingToken); } catch (OperationCanceledException) { break; } }
        }
    }
}
