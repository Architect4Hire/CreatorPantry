using CreatorPantry.Domain.Managers.Outbox.Events;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Facade;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Content;

public static class ContentServiceCollectionExtensions
{
    /// <summary>
    /// The content module's composition root. Requires <see cref="CreatorPantry.Domain.Managers.Persistence.CreatorPantryDbContext"/>,
    /// tenancy and application time to be registered. Registers the staleness facade as a recipe-change consumer,
    /// so a host that also registers the Recipes module reacts to a recipe gaining a version.
    /// </summary>
    public static IServiceCollection AddContentModule(this IServiceCollection services)
    {
        services.AddScoped<IContentStalenessRepository, ContentStalenessRepository>();
        services.AddScoped<IContentStalenessDataLayer, ContentStalenessDataLayer>();
        services.AddScoped<IContentStalenessBusiness, ContentStalenessBusiness>();
        services.AddScoped<IContentStalenessFacade, ContentStalenessFacade>();
        services.AddScoped<IRecipeChangeConsumer>(provider => provider.GetRequiredService<IContentStalenessFacade>());

        services.AddScoped<IContentSeoRepository, ContentSeoRepository>();
        services.AddScoped<IContentSeoDataLayer, ContentSeoDataLayer>();
        services.AddScoped<IContentSeoBusiness, ContentSeoBusiness>();
        services.AddScoped<IContentSeoFacade, ContentSeoFacade>();
        services.AddScoped<IContentEditorialBusiness, ContentEditorialBusiness>();
        services.AddScoped<IContentEditorialFacade, ContentEditorialFacade>();

        return services;
    }
}
