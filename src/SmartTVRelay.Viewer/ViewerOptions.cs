namespace SmartTVRelay.Viewer;

public sealed class ViewerOptions
{
    public string WorkDir { get; set; } = Path.Combine(Path.GetTempPath(), "smarttvrelay-viewer");
    public int MaxChannels { get; set; } = 2;
    public int IdleSeconds { get; set; } = 30;
    public int StartupTimeoutSeconds { get; set; } = 15;
    public string FfmpegPath { get; set; } = "ffmpeg";
}

public sealed class TunerOptions
{
    public string BaseUrl { get; set; } = "http://192.168.0.66";
    public int StreamPort { get; set; } = 5004;
}
