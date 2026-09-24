using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Managers.Paging;

/// <summary>
/// Paging, search, and cache limits shared by the reference read seam's validators, business rules, and
/// cache keys.
/// </summary>
/// <remarks>
/// The page-size bounds are not chosen here — they are already published as the <c>Limit</c> parameter in the
/// reviewed OpenAPI document (min 1, max 100, default 25). This class restates them as constants so the
/// implementation and the contract cannot drift apart silently.
/// </remarks>
public static class ReferencePolicy
{
    public const int DefaultPageSize = 25;

    public const int MinPageSize = 1;

    public const int MaxPageSize = 100;

    /// <summary>
    /// Below this, a search term is ignored rather than applied. A single character matches most of the
    /// catalogue, so filtering on one costs a full scan to return nearly everything — the first page
    /// unfiltered is the same answer for less work.
    /// </summary>
    public const int MinSearchLength = 2;

    /// <summary>Rejected above this, so a pathological query string cannot become a pathological LIKE pattern.</summary>
    public const int SearchMaxLength = 128;

    /// <summary>Refused above this many candidates in one batch-resolve request (7.3's ingredient/unit matcher).</summary>
    public const int MaxMatchCandidates = 200;

    /// <summary>Refused above this many characters for a single candidate in a batch-resolve request.</summary>
    public const int MaxMatchCandidateLength = 128;

    /// <summary>The <see cref="Caching.CacheKeys.Global"/> category every reference page is cached under.</summary>
    public const string CacheCategory = "reference";

    /// <summary>
    /// Part of every cache key. Bump it whenever a reference ServiceModel's shape changes, so a deployment
    /// reads its own entries instead of failing to deserialize the previous release's JSON.
    /// </summary>
    public const string CacheSchemaVersion = "v2";

    /// <summary>
    /// How long a cached page stays valid.
    /// </summary>
    /// <remarks>
    /// There is no invalidation path, deliberately: nothing writes reference data at runtime — it changes only
    /// when the migration service runs its seeder at deployment — so an invalidation API here would be code no
    /// caller could ever reach. This lifetime is what bounds staleness after a deploy instead.
    /// </remarks>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Clamps a requested page size into the published bounds. An absent limit takes the default; an
    /// out-of-range one is clamped rather than rejected, so a client cannot fail a read by asking for too much.
    /// </summary>
    public static int ClampPageSize(int? limit) =>
        limit is null ? DefaultPageSize : Math.Clamp(limit.Value, MinPageSize, MaxPageSize);

    /// <summary>
    /// The searchable forms of a term, or <c>null</c> when there is nothing worth filtering on.
    /// </summary>
    /// <remarks>
    /// The length floor is applied to the raw term, before normalizing: normalizing can only shorten a term
    /// (<c>"a-b"</c> becomes <c>"a b"</c>, <c>"!!"</c> becomes empty), so testing the raw length decides on
    /// what the creator actually typed rather than on what punctuation survived.
    /// </remarks>
    public static ReferenceSearch? NormalizeSearch(string? search)
    {
        var raw = search?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(raw) || raw.Length < MinSearchLength)
        {
            return null;
        }

        var normalized = NameNormalization.NormalizeName(raw);

        // A term of pure punctuation ("!!!") normalizes to nothing, and an empty key is a substring of every
        // row — so an unguarded empty form would turn a search that should match nothing into one that matches
        // the entire catalogue. Falling back to the raw term searches for it literally, which finds nothing.
        return new ReferenceSearch(raw, normalized.Length == 0 ? raw : normalized);
    }
}
