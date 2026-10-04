using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// Picks one candidate per facet from a seed token. A pure function: no clock, no randomness, no state.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Rendezvous selection, not indexing.</strong> Each candidate is scored
/// <c>SHA-256(token ␟ facet ␟ key)</c> and the lowest score wins. Scoring every candidate independently is what
/// makes a token survive a catalogue edit: adding one entry to a list of <em>n</em> changes that facet's pick
/// only if the new entry happens to score lowest, which is a 1-in-(<em>n</em>+1) chance. Selecting by index into
/// a sorted list would instead shift every pick the moment a row was inserted, so a shared seed link would stop
/// meaning what it meant the day a cuisine was added.
/// </para>
/// <para>
/// <strong>The facet name is part of the hash</strong>, so the same token picks independently per facet rather
/// than correlating them. Without it, a cuisine and an occasion that happened to share a key would always be
/// chosen together.
/// </para>
/// <para>
/// <strong>Uniform by construction, and extensible to weights.</strong> SHA-256 over distinct inputs is
/// uniformly distributed, so every active candidate is equally likely over the space of tokens — which is the
/// honest default while no weighting data exists (<c>ContentSeedPolicy</c> says why). Should real weights ever
/// arrive, weighted rendezvous scores <c>-ln(u)/w</c> over the same digest and needs no contract change.
/// </para>
/// <para>
/// Not a cryptographic use. SHA-256 is here because it is a fast, well-distributed, stable-across-runtimes hash;
/// <c>string.GetHashCode</c> is randomized per process and would make a token mean something different after a
/// restart, which is the one property this must not have.
/// </para>
/// </remarks>
internal static class ContentSeedSelector
{
    /// <summary>Separates the hash's parts so no two different triples can produce the same input bytes.</summary>
    private const char Separator = '\u001f';

    /// <summary>
    /// The candidate of <paramref name="candidates"/> this <paramref name="token"/> selects for
    /// <paramref name="facet"/>, or null when there are none — an empty catalogue is a facet the seed omits, not
    /// an error.
    /// </summary>
    /// <param name="keyOf">The candidate's stable key. Scored, so it must be the stable one, never a display name.</param>
    public static T? Select<T>(string token, string facet, IReadOnlyList<T> candidates, Func<T, string> keyOf)
        where T : class
    {
        T? winner = null;
        var best = (High: ulong.MaxValue, Key: string.Empty);

        foreach (var candidate in candidates)
        {
            var key = keyOf(candidate);
            var score = Score(token, facet, key);

            // Ties broken on the key, so the answer never depends on the order candidates arrived in — a
            // database read with no ORDER BY would otherwise make the same token answer differently.
            if (winner is null
                || score < best.High
                || (score == best.High && string.CompareOrdinal(key, best.Key) < 0))
            {
                winner = candidate;
                best = (score, key);
            }
        }

        return winner;
    }

    private static ulong Score(string token, string facet, string key)
    {
        var input = string.Concat(token, Separator, facet, Separator, key);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), digest);

        // The leading eight bytes are plenty to order candidates by, and big-endian so the comparison follows
        // the digest's own byte order rather than the machine's.
        return BinaryPrimitives.ReadUInt64BigEndian(digest);
    }
}
