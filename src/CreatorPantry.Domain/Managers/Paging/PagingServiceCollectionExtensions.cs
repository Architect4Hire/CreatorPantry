using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Managers.Paging;

public static class PagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared cached-page read path. Safe to call from every module that pages, which is why
    /// it uses <c>TryAdd</c>: five modules each declaring the dependency they use is clearer than one caller
    /// remembering to register it for all of them.
    /// </summary>
    public static IServiceCollection AddPaging(this IServiceCollection services)
    {
        services.TryAddScoped<CachedPageReader>();

        return services;
    }
}
