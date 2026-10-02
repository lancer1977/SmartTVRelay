namespace SmartTVRelay.Viewer;

/// <summary>Input/output description for one ffmpeg HLS pipeline.</summary>
public sealed record FfmpegStartInfo(string InputUrl, string OutputDirectory, string PlaylistPath);

/// <summary>A running ffmpeg process.</summary>
public interface IFfmpegProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>Kills the process (and anything it spawned). Must be safe to call repeatedly.</summary>
    void Kill();
}

/// <summary>Launches ffmpeg. Replaceable in tests.</summary>
public interface IFfmpegRunner
{
    IFfmpegProcess Start(FfmpegStartInfo info);
}

/// <summary>One tuner channel as exposed by <c>GET /api/channels</c>.</summary>
public sealed record ChannelInfo(string GuideNumber, string Name, string VideoCodec);

/// <summary>Reads the tuner channel lineup.</summary>
public interface ITunerLineup
{
    Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken);
}

/// <summary>Read-only view of running channel pipelines (for later slices).</summary>
public interface IChannelPipelineRegistry
{
    IReadOnlyCollection<string> RunningChannels { get; }

    /// <summary>Work directory of a running pipeline, or null if the channel is not running.</summary>
    string? GetWorkDirectory(string guideNumber);
}
