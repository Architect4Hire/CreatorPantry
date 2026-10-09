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
    /// <para>
    /// The content-seed generator additionally needs the <strong>Vocabulary</strong> and <strong>Brand</strong>
    /// modules, whose facades it reads for reference vocabulary and for the workspace's declared channels. A host
    /// that registers this module without those two still resolves everything else here — the seed facade is the
    /// only thing that would fail, and it fails loudly at resolution rather than quietly at runtime.
    /// </para>
    /// <para>
    /// The prompt library needs the <strong>Recipes</strong> module and the AI module's
    /// <c>IAiProposalLookupFacade</c>, which it resolves a prompt's recipe, version and proposal pins through
    /// before writing a row that can never be corrected. Same bargain as above: a host missing either fails on
    /// the prompt facade at resolution and nowhere else. <c>AddAiModule</c> registers that lookup, so the AI
    /// request seam is not also required.
    /// </para>
    /// <para>
    /// It resolves a prompt's generated-image pin the same way, through the <strong>Media</strong> module's
    /// <c>IGeneratedImageLookupFacade</c>, so <c>AddMediaModule</c> belongs beside this call in any host that
    /// serves the prompt routes. 12.3a refused to accept that id at all until something could verify it inside
    /// the resolved workspace; this is that something, and the row it guards can never be corrected.
    /// </para>
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

        services.AddScoped<IPromptRecordRepository, PromptRecordRepository>();
        services.AddScoped<IPromptRecordSearchRepository, PromptRecordSearchRepository>();
        services.AddScoped<IPromptRecordDataLayer, PromptRecordDataLayer>();
        services.AddScoped<IPromptRecordBusiness, PromptRecordBusiness>();
        services.AddScoped<IPromptRecordFacade, PromptRecordFacade>();
        services.AddScoped<IValidator<SavePromptRecordViewModel>, SavePromptRecordViewModelValidator>();
        services.AddScoped<IValidator<PromptSearchViewModel>, PromptSearchViewModelValidator>();

        services.AddScoped<ICreativeContextRepository, CreativeContextRepository>();
        services.AddScoped<ICreativeContextDataLayer, CreativeContextDataLayer>();
        services.AddScoped<ICreativeContextBusiness, CreativeContextBusiness>();
        services.AddScoped<ICreativeContextFacade, CreativeContextFacade>();
        services.AddScoped<ICreativeContextPackageBusiness, CreativeContextPackageBusiness>();
        services.AddScoped<ICreativeContextPackageFacade, CreativeContextPackageFacade>();
        services.AddScoped<IValidator<CreateCreativeContextViewModel>, CreateCreativeContextViewModelValidator>();
        services.AddScoped<IValidator<PatchCreativeContextViewModel>, PatchCreativeContextViewModelValidator>();
        services.AddScoped<IValidator<AddCreativeContextReferenceViewModel>, AddCreativeContextReferenceViewModelValidator>();
        services.AddScoped<IValidator<CreativeContextListViewModel>, CreativeContextListViewModelValidator>();

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
