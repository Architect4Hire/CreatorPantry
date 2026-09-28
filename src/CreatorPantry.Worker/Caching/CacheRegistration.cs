using CreatorPantry.Domain.Managers.Caching;

namespace CreatorPantry.Worker.Caching;

public static class CacheRegistration
{
    /// <summary>
    /// Registers the distributed cache this host reads reference data through: Redis when the AppHost has
    /// supplied a connection string, an in-process store when it has not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The worker needs this for the same reason the API does, and not as a nicety: it registers the recipe,
    /// ingredient, measurement and vocabulary modules so an AI operation can load the exact recipe it names
    /// through their facades, and those facades take <see cref="IApplicationCache"/>. Without it the container
    /// fails validation at startup and the host exits, leaving every queued operation unclaimed.
    /// </para>
    /// <para>
    /// The same Redis resource the API uses, so an invalidation on one side is seen by the other. The
    /// in-memory fallback keeps a host without infrastructure able to start, matching the API's; a worker
    /// falling back to it caches only for itself, which is safe while it reads and would not be if it started
    /// invalidating what the API has cached.
    /// </para>
    /// </remarks>
    public static IHostApplicationBuilder AddCreatorPantryCache(this IHostApplicationBuilder builder)
    {
        if (builder.Configuration.GetConnectionString(CacheConnection.Name) is not null)
        {
            builder.AddRedisDistributedCache(CacheConnection.Name);
        }
        else
        {
            builder.Services.AddDistributedMemoryCache();
        }

        builder.Services.AddApplicationCache();

        return builder;
    }
}
