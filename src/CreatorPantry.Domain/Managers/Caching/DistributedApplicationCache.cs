using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Managers.Caching;

/// <summary>
/// <see cref="IApplicationCache"/> over the registered <see cref="IDistributedCache"/> — Redis in a deployed
/// environment, an in-memory store where none is configured.
/// </summary>
/// <remarks>
/// <para>
/// Every operation fails open. A Redis outage would otherwise turn a cache, whose entire purpose is to make
/// reads cheaper, into a new way for reads to fail — so a get that throws is reported as a miss and a set
/// that throws is dropped, both logged. The caller then does exactly what it would have done on a real miss.
/// </para>
/// <para>
/// Deserialization failures are treated the same way, which is what makes a cached shape change survivable:
/// an entry written by a previous release that no longer fits its type is a miss, not a 500. The schema
/// version in the key is the deliberate control for that; this is the backstop.
/// </para>
/// </remarks>
internal sealed class DistributedApplicationCache(
    IDistributedCache cache,
    ILogger<DistributedApplicationCache> logger) : IApplicationCache
{
    /// <summary>
    /// Mirrors the HTTP contract these models are also serialized to: camelCase names, enums by name.
    /// </summary>
    /// <remarks>
    /// Matching the wire format is not strictly required — a cached value only ever round-trips back to its own
    /// type — but a cache that stored a different shape than the API returns would make any dump of it a
    /// misleading thing to read while debugging a response.
    /// </remarks>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var payload = await cache.GetAsync(key, cancellationToken);

            return payload is null ? null : JsonSerializer.Deserialize<T>(payload, SerializerOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller is going away: this is not a cache failure and must not be swallowed as a miss.
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache read failed for {CacheKey}; treating it as a miss.", key);

            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan lifetime, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);

            // Absolute rather than sliding: a reference page should be re-read on a schedule after a deploy
            // replaces the catalogue, not kept alive indefinitely by being popular.
            await cache.SetAsync(
                key,
                payload,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Cache write failed for {CacheKey}; the value was not cached.", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveAsync(key, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Worth a warning rather than silence: a failed removal leaves a stale entry readable until it
            // expires, which is the one fail-open case a caller might need to know about.
            logger.LogWarning(exception, "Cache removal failed for {CacheKey}; the entry may still be served.", key);
        }
    }
}
