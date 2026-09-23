using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Managers.Caching;

public static class CachingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IApplicationCache"/> over whichever
    /// <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> the host has registered.
    /// </summary>
    /// <remarks>
    /// The host chooses the store — Redis where one is configured, in-memory otherwise — because that is a
    /// deployment concern. This layer only decides how a cached value is shaped and how failures are handled.
    /// </remarks>
    public static IServiceCollection AddApplicationCache(this IServiceCollection services)
    {
        services.TryAddSingleton<IApplicationCache, DistributedApplicationCache>();

        return services;
    }
}
