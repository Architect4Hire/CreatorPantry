using CreatorPantry.Domain.Managers.Outbox.Events;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Content;

public static class ContentServiceCollectionExtensions
{
    /// <summary>
    /// The content module's composition root. Requires <see cref="CreatorPantry.Domain.Managers.Persistence.CreatorPantryDbContext"/>,
    /// tenancy and application time to be registered. Registers the staleness facade as a recipe-change consumer,
    /// so a host that also registers the Recipes module reacts to a recipe gaining a version.
    /// </summary>
    /// <remarks>
    /// The content-seed generator additionally needs the <strong>Vocabulary</strong> and <strong>Brand</strong>
    /// modules, whose facades it reads for reference vocabulary and for the workspace's declared channels. A host
    /// that registers this module without those two still resolves everything else here — the seed facade is the
    /// only thing that would fail, and it fails loudly at resolution rather than quietly at runtime.
    /// </remarks>
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

        services.AddScoped<IWorkspaceWeeklyThemeRepository, WorkspaceWeeklyThemeRepository>();
        services.AddScoped<IWorkspaceWeeklyThemeDataLayer, WorkspaceWeeklyThemeDataLayer>();
        services.AddScoped<IWorkspaceWeeklyThemeBusiness, WorkspaceWeeklyThemeBusiness>();
        services.AddScoped<IWorkspaceWeeklyThemeFacade, WorkspaceWeeklyThemeFacade>();
        services.AddScoped<IValidator<ReplaceWeeklyThemesViewModel>, ReplaceWeeklyThemesViewModelValidator>();

        // The seed generator owns no table, so it has no repository or data layer: it composes this module's
        // weekly themes with the vocabulary and brand facades and the three code-owned catalogues.
        services.AddContentChannelCatalog();
        services.AddPhotographyStyleCatalog();
        services.AddOccasionCatalog();
        services.AddSingleton<IContentSeedTokenSource, ContentSeedTokenSource>();
        services.AddScoped<IContentSeedBusiness, ContentSeedBusiness>();
        services.AddScoped<IContentSeedFacade, ContentSeedFacade>();
        services.AddScoped<IValidator<ContentSeedQueryViewModel>, ContentSeedQueryViewModelValidator>();

        return services;
    }
}
