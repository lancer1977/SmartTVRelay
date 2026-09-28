namespace SmartTVRelay.Core;

using Observation.Core;

public sealed class ExplicitMarkerDetector
{
    private const string SourceKind = "explicit-marker";
    private const double MarkerConfidence = 0.98;

    public IReadOnlyList<Observation<BroadcastState>> Detect(string sourceId, ExplicitMarkerSignal signal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        var value = signal.Kind switch
        {
            ExplicitMarkerKind.CueOut => BroadcastState.Commercial,
            ExplicitMarkerKind.CueIn => BroadcastState.Program,
            ExplicitMarkerKind.None => (BroadcastState?)null,
            _ => null,
        };

        if (value is null)
        {
            return Array.Empty<Observation<BroadcastState>>();
        }

        return new[]
        {
            new Observation<BroadcastState>(
                Guid.NewGuid(),
                ObservationScope.Of(sourceId),
                BroadcastObservations.Subject,
                BroadcastObservations.Predicate,
                value.Value,
                sourceId,
                SourceKind,
                MarkerConfidence,
                signal.ObservedAt)
        };
    }
}
