namespace CreatorPantry.Domain.Managers.Caching;

/// <summary>
/// A shared cache, keyed by <see cref="CacheKeys"/>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow: get, set with an explicit lifetime, remove. No "get or create", because that hides
/// whether a call went to the database and the hit/miss behaviour is exactly what these seams are tested on.
/// </para>
/// <para>
/// Values are serialized, so only data may be cached — ServiceModels and DTOs, never EF entities, whose
/// change-tracked graphs and lazy references do not survive a round trip and must not be shared between
/// requests.
/// </para>
/// <para>
/// Implementations must <em>fail open</em>: a cache that is unreachable is a slow request, not a failed one.
/// </para>
/// </remarks>
public interface IApplicationCache
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
        where T : class;

    Task SetAsync<T>(string key, T value, TimeSpan lifetime, CancellationToken cancellationToken)
        where T : class;

    Task RemoveAsync(string key, CancellationToken cancellationToken);
}
