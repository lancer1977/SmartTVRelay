namespace SmartTVRelay.Core.Ingest;

/// <summary>Coarse health/availability of a broadcast media source, independent of whether a read is
/// currently in progress.</summary>
public enum SourceHealth
{
    Unknown = 0,
    Healthy,
    Degraded,
    Unavailable,
}

/// <summary>Point-in-time health/status snapshot for a source, with an optional human-readable detail
/// (e.g. why a source is degraded/unavailable).</summary>
public sealed record SourceStatus(SourceHealth Health, string? Detail = null);
