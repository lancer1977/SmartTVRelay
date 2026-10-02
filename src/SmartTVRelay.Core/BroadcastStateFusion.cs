namespace SmartTVRelay.Core;

using Observation.Core;

public sealed class BroadcastStateFusion
{
    private readonly ObservationFusion<BroadcastState> fusion;

    public BroadcastStateFusion(ObservationFusionOptions<BroadcastState>? options = null)
        => fusion = new ObservationFusion<BroadcastState>(options ?? new ObservationFusionOptions<BroadcastState>());

    public IReadOnlyList<FusionResult<BroadcastState>> Fuse(IEnumerable<Observation<BroadcastState>> observations)
        => fusion.Resolve(observations);
}
