using System.Text.Json.Serialization;
using Observation.Core;
using SmartTVRelay.Core;

namespace SmartTVRelay.Viewer.State;

/// <summary>One piece of evidence behind a reported state (provenance only; no raw payloads).</summary>
public sealed record EvidenceDto(string Source, string Kind);

/// <summary>Wire shape of <c>GET /api/channels/{guideNumber}/state</c> and each SSE event.</summary>
public sealed record ChannelStateDto(
    string GuideNumber,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] BroadcastState State,
    double Confidence,
    IReadOnlyList<EvidenceDto> Evidence,
    DateTimeOffset AsOf);

/// <summary>
/// Supplies detector observations for a running channel's recent capture. Implementations only
/// observe; they never decide actions. Anything they cannot establish must be omitted (fail open).
/// </summary>
public interface IChannelEvidenceSource
{
    /// <param name="guideNumber">Channel guide number (digits and dots).</param>
    /// <param name="workDirectory">The running pipeline's work directory, including completed raw transport windows.</param>
    Task<IReadOnlyList<Observation<BroadcastState>>> GetObservationsAsync(
        string guideNumber, string workDirectory, CancellationToken cancellationToken);
}

/// <summary>Bound from <c>Viewer:State</c>.</summary>
public sealed class ChannelStateOptions
{
    /// <summary>How often running channels are re-evaluated.</summary>
    public int PollMilliseconds { get; set; } = 2000;
    /// <summary>SSE keep-alive comment interval.</summary>
    public int KeepAliveSeconds { get; set; } = 15;
    /// <summary>Observations older than this are stale and ignored.</summary>
    public int EvidenceWindowSeconds { get; set; } = 30;
    /// <summary>Fused confidence below this reports Unknown (mirrors BroadcastReplacementPolicy's default).</summary>
    public double ConfidenceThreshold { get; set; } = 0.75;
    /// <summary>Number of newest segments analysed by the default evidence source.</summary>
    public int WindowSegments { get; set; } = 3;
    /// <summary>Maximum time to retain a timed CueOut/CueIn while fresh raw transport arrives.</summary>
    public int MaxMarkerHoldSeconds { get; set; } = 120;
    /// <summary>Upper bound for one analysis pass of the default evidence source.</summary>
    public int AnalysisTimeoutSeconds { get; set; } = 10;
}
