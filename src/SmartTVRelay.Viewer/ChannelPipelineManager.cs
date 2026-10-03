using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SmartTVRelay.Viewer;

/// <summary>Thrown when starting another pipeline would exceed Viewer:MaxChannels.</summary>
public sealed class CapacityExceededException(int max)
    : Exception($"All {max} tuner channels are in use; stop watching one channel before starting another.");

/// <summary>Owns one ffmpeg pipeline per channel, the concurrency cap and the idle reaper.</summary>
public sealed partial class ChannelPipelineManager : IChannelPipelineRegistry, IHostedService, IDisposable
{
    private sealed class Pipeline(IFfmpegProcess process, string dir, DateTimeOffset now)
    {
        public IFfmpegProcess Process { get; } = process;
        public string Directory { get; } = dir;
        public DateTimeOffset LastAccess { get; set; } = now;
        public string Playlist => Path.Combine(Directory, "index.m3u8");
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Pipeline> _pipelines = new();
    private readonly IFfmpegRunner _runner;
    private readonly ViewerOptions _viewer;
    private readonly TunerOptions _tuner;
    private readonly TimeProvider _time;
    private ITimer? _timer;

    public ChannelPipelineManager(IFfmpegRunner runner, IOptions<ViewerOptions> viewer,
        IOptions<TunerOptions> tuner, TimeProvider time)
    {
        _runner = runner;
        _viewer = viewer.Value;
        _tuner = tuner.Value;
        _time = time;
    }

    [GeneratedRegex(@"^[0-9.]+$")]
    private static partial Regex GuideNumberPattern();

    /// <summary>Digits and dots only; guards against path traversal.</summary>
    public static bool IsValidGuideNumber(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 16 && GuideNumberPattern().IsMatch(value)
        && !value.Contains("..", StringComparison.Ordinal);

    public IReadOnlyCollection<string> RunningChannels
    {
        get { lock (_gate) return _pipelines.Keys.ToArray(); }
    }

    public string? GetWorkDirectory(string guideNumber)
    {
        lock (_gate) return _pipelines.TryGetValue(guideNumber, out var p) ? p.Directory : null;
    }

    /// <summary>
    /// Returns the playlist path of the channel's pipeline, starting it if needed, and marks it as accessed.
    /// Throws <see cref="CapacityExceededException"/> if the cap is reached.
    /// </summary>
    public string EnsureStarted(string guideNumber)
    {
        if (!IsValidGuideNumber(guideNumber)) throw new ArgumentException("Invalid guide number.", nameof(guideNumber));
        lock (_gate)
        {
            if (_pipelines.TryGetValue(guideNumber, out var existing))
            {
                if (!existing.Process.HasExited)
                {
                    existing.LastAccess = _time.GetUtcNow();
                    return existing.Playlist;
                }
                Remove(guideNumber, existing);
            }

            if (_pipelines.Count >= _viewer.MaxChannels) throw new CapacityExceededException(_viewer.MaxChannels);

            var dir = Path.Combine(_viewer.WorkDir, $"ch-{guideNumber}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var playlist = Path.Combine(dir, "index.m3u8");
            var input = $"http://{new Uri(_tuner.BaseUrl).Host}:{_tuner.StreamPort}/auto/v{guideNumber}";
            IFfmpegProcess process;
            try { process = _runner.Start(new FfmpegStartInfo(input, dir, playlist)); }
            catch { TryDelete(dir); throw; }
            _pipelines[guideNumber] = new Pipeline(process, dir, _time.GetUtcNow());
            return playlist;
        }
    }

    /// <summary>Resolves a file inside a running channel's work dir and marks the pipeline as accessed.</summary>
    public string? TouchAndResolve(string guideNumber, string fileName)
    {
        lock (_gate)
        {
            if (!_pipelines.TryGetValue(guideNumber, out var p)) return null;
            p.LastAccess = _time.GetUtcNow();
            return Path.Combine(p.Directory, fileName);
        }
    }

    /// <summary>Stops and cleans pipelines idle for longer than Viewer:IdleSeconds (also removes exited ones).</summary>
    public int ReapIdle()
    {
        var cutoff = _time.GetUtcNow() - TimeSpan.FromSeconds(_viewer.IdleSeconds);
        lock (_gate)
        {
            var stale = _pipelines.Where(kv => kv.Value.LastAccess <= cutoff || kv.Value.Process.HasExited).ToList();
            foreach (var (key, p) in stale) Remove(key, p);
            return stale.Count;
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var (key, p) in _pipelines.ToList()) Remove(key, p);
        }
    }

    private void Remove(string key, Pipeline p)
    {
        _pipelines.Remove(key);
        try { p.Process.Kill(); } catch { /* best effort */ }
        try { p.Process.Dispose(); } catch { /* best effort */ }
        TryDelete(p.Directory);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, Math.Min(5, _viewer.IdleSeconds / 3.0)));
        _timer = _time.CreateTimer(_ => ReapIdle(), null, period, period);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        StopAll();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        StopAll();
    }
}
