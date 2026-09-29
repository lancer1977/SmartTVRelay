namespace SmartTVRelay.Core;

/// <summary>
/// A simple in-memory <see cref="IKnownCommercialFingerprintStore"/> backed by a fixed set of known
/// fingerprints supplied at construction. Intended for tests and small corpora; this repo does not build
/// or maintain a real fingerprint database as part of this detector (see #37's acceptance criteria: "no
/// direct persistence choice beyond minimal interface").
/// </summary>
public sealed class InMemoryCommercialFingerprintStore : IKnownCommercialFingerprintStore
{
    private readonly List<(string CommercialId, IReadOnlyList<int> Hash)> entries;

    public InMemoryCommercialFingerprintStore(IEnumerable<KeyValuePair<string, IReadOnlyList<int>>> knownFingerprints)
    {
        ArgumentNullException.ThrowIfNull(knownFingerprints);

        entries = new List<(string, IReadOnlyList<int>)>();
        foreach (var entry in knownFingerprints)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Key);
            ArgumentNullException.ThrowIfNull(entry.Value);
            if (entry.Value.Count == 0)
            {
                throw new ArgumentException("Known fingerprint hash vectors must be non-empty.", nameof(knownFingerprints));
            }

            entries.Add((entry.Key, entry.Value));
        }
    }

    /// <summary>
    /// Finds the closest known fingerprint using Hamming distance (the fraction of positions with a
    /// differing value) over vectors of equal length. Iterates entries in construction order and keeps the
    /// first strictly-lower-distance match, so results are deterministic for a fixed corpus and query.
    /// </summary>
    public FingerprintMatch? FindClosestMatch(IReadOnlyList<int> hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Count == 0)
        {
            throw new ArgumentException("Query hash vector must be non-empty.", nameof(hash));
        }

        FingerprintMatch? best = null;

        foreach (var (commercialId, knownHash) in entries)
        {
            if (knownHash.Count != hash.Count)
            {
                continue;
            }

            var differing = 0;
            for (var i = 0; i < hash.Count; i++)
            {
                if (hash[i] != knownHash[i])
                {
                    differing++;
                }
            }

            var ratio = (double)differing / hash.Count;
            if (best is null || ratio < best.DistanceRatio)
            {
                best = new FingerprintMatch(commercialId, ratio);
            }
        }

        return best;
    }
}
