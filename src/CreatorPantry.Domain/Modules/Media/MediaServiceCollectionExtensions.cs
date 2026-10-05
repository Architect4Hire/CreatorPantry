using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Facade;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Media;

/// <summary>
/// The Media module's composition root: generated images now, the DAM from 12.9.
/// </summary>
public static class MediaServiceCollectionExtensions
{
    /// <summary>
    /// Registers the generated-image workspace's read seam.
    /// </summary>
    /// <remarks>
    /// Thin on purpose. 12.6 lands the entities, their configuration and the one lookup another module needs
    /// to validate a reference; staging, retrieval and the retention sweep arrive with the worker that writes
    /// them (12.7, 12.8) rather than being registered here before anything calls them.
    /// </remarks>
    public static IServiceCollection AddMediaModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IGeneratedImageRepository, GeneratedImageRepository>();
        services.AddScoped<IGeneratedImageDataLayer, GeneratedImageDataLayer>();
        services.AddScoped<IGeneratedImageLookupBusiness, GeneratedImageLookupBusiness>();
        services.AddScoped<IGeneratedImageLookupFacade, GeneratedImageLookupFacade>();

        return services;
    }
}
