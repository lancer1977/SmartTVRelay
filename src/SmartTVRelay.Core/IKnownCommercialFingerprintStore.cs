namespace SmartTVRelay.Core;

/// <summary>
/// Minimal lookup abstraction for matching a query audio fingerprint against a corpus of known
/// commercial fingerprints. Deliberately prescribes no persistence mechanism: an implementation may be
/// backed by an in-memory set (see <see cref="InMemoryCommercialFingerprintStore"/>), a database, a file,
/// or a remote service -- <see cref="KnownCommercialFingerprintDetector"/> only depends on this interface.
/// </summary>
public interface IKnownCommercialFingerprintStore
{
    /// <summary>
    /// Finds the closest known fingerprint to <paramref name="hash"/> among entries of the same vector
    /// length. Entries whose vector length differs from <paramref name="hash"/> are not comparable and are
    /// skipped. Returns <see langword="null"/> when no comparable entry exists.
    /// </summary>
    /// <param name="hash">The non-empty query hash vector.</param>
    FingerprintMatch? FindClosestMatch(IReadOnlyList<int> hash);
}

/// <summary>
/// The closest known fingerprint found for a query, and how far it was from an exact match.
/// </summary>
/// <param name="CommercialId">The identifier of the matched known commercial.</param>
/// <param name="DistanceRatio">
/// The fraction of hash-vector positions that differed from the query, in [0, 1]. 0 means an exact match.
/// </param>
public sealed record FingerprintMatch(string CommercialId, double DistanceRatio);
