namespace SmartTVRelay.Core;

public enum ExplicitMarkerKind
{
    None = 0,
    CueOut, // ad break starts -- commercial begins
    CueIn,  // ad break ends -- program resumes
}

public sealed record ExplicitMarkerSignal(ExplicitMarkerKind Kind, DateTimeOffset ObservedAt);
