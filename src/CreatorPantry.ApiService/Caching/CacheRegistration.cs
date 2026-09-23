using CreatorPantry.Domain.Managers.Caching;

namespace CreatorPantry.ApiService.Caching;

public static class CacheRegistration
{
    /// <summary>The Aspire resource name for the shared Redis cache, as the AppHost declares it.</summary>
    public const string ConnectionName = "cache";

    /// <summary>
    /// Registers the distributed cache the API reads reference data through: Redis when the AppHost has
    /// supplied a connection string, an in-process store when it has not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallback is not a convenience — it is what keeps the cache out of the way of anything that boots
    /// this host without infrastructure. <c>WebApplicationFactory</c> and <c>SqliteApiHost</c> start the real
    /// <c>Program</c>, and an unconditional Redis registration would make every endpoint test depend on a
    /// running container to do something the tests never assert on.
    /// </para>
    /// <para>
    /// An in-memory store is per-process, so with more than one API instance each would cache separately. That
    /// is a correctness non-issue for reference data — every instance would compute the same answer from the
    /// same read-only catalogue — but it is a reason not to lean on the fallback in a deployment, where
    /// <see cref="ConnectionName"/> is always configured.
    /// </para>
    /// </remarks>
    public static IHostApplicationBuilder AddCreatorPantryCache(this IHostApplicationBuilder builder)
    {
        if (builder.Configuration.GetConnectionString(ConnectionName) is not null)
        {
            builder.AddRedisDistributedCache(ConnectionName);
        }
        else
        {
            builder.Services.AddDistributedMemoryCache();
        }

        builder.Services.AddApplicationCache();

        return builder;
    }
}
