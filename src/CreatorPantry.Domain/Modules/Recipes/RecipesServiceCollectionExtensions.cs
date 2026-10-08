using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Outbox.Events;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Modules.Recipes;

public static class RecipesServiceCollectionExtensions
{
    /// <summary>
    /// The recipe module's composition root. Repositories, the data layer and business are registered here;
    /// the facade joins them when its seam is built.
    /// </summary>
    /// <param name="configuration">
    /// Optional, and only read for <see cref="RecipeReadinessOptions"/>. Omitted — as the module's own tests omit
    /// it — the readiness rules run at the catalogue's default severities, which is the intended behaviour of a host
    /// that has configured nothing rather than a degraded one.
    /// </param>
    public static IServiceCollection AddRecipesModule(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddKeyedScoped<IOutboxMessageHandler, RecipeVersionChangedOutboxHandler>(
            RecipeVersionChangedEvent.MessageType);
        services.AddScoped<IRecipeRepository, RecipeRepository>();
        services.AddScoped<IRecipeSearchRepository, RecipeSearchRepository>();
        services.AddScoped<IRecipeVersionRepository, RecipeVersionRepository>();
        services.AddScoped<IRecipeStatusTransitionRepository, RecipeStatusTransitionRepository>();
        services.AddScoped<IWorkspaceTagRepository, WorkspaceTagRepository>();
        services.AddScoped<IRecipeTestRunRepository, RecipeTestRunRepository>();
        services.AddScoped<IRecipeTestRunHistoryRepository, RecipeTestRunHistoryRepository>();
        services.AddScoped<IRecipeReadinessRepository, RecipeReadinessRepository>();
        services.AddScoped<IRecipeExportDataLayer, RecipeExportDataLayer>();
        services.AddScoped<IRecipeDataLayer, RecipeDataLayer>();
        services.AddScoped<IRecipeTestRunDataLayer, RecipeTestRunDataLayer>();
        services.AddScoped<IRecipeReadinessDataLayer, RecipeReadinessDataLayer>();
        services.AddScoped<IRecipeExportBusiness, RecipeExportBusiness>();
        services.AddScoped<IRecipeBusiness, RecipeBusiness>();
        services.AddScoped<IRecipeTestRunBusiness, RecipeTestRunBusiness>();
        services.AddScoped<IRecipeReadinessBusiness, RecipeReadinessBusiness>();
        services.AddScoped<IRecipeFacade, RecipeFacade>();
        services.AddScoped<IRecipeTestRunFacade, RecipeTestRunFacade>();

        // RCPUB-005: a recipe's own asset links, and the pictures attached to a recorded test.
        services.AddScoped<IRecipeAssetLinkBusiness, RecipeAssetLinkBusiness>();
        services.AddScoped<IRecipeAssetLinkFacade, RecipeAssetLinkFacade>();
        services.AddScoped<ITestAttachmentRepository, TestAttachmentRepository>();
        services.AddScoped<ITestAttachmentDataLayer, TestAttachmentDataLayer>();
        services.AddScoped<ITestAttachmentBusiness, TestAttachmentBusiness>();
        services.AddScoped<IRecipeTestAttachmentFacade, RecipeTestAttachmentFacade>();
        services.AddScoped<IValidator<LinkRecipeAssetViewModel>, LinkRecipeAssetViewModelValidator>();
        services.AddScoped<IValidator<UnlinkRecipeAssetViewModel>, UnlinkRecipeAssetViewModelValidator>();
        services.AddScoped<IValidator<AttachTestImageViewModel>, AttachTestImageViewModelValidator>();
        services.AddScoped<IIngredientParsingFacade, IngredientParsingFacade>();
        services.AddScoped<IValidator<CreateRecipeTestRunViewModel>, CreateRecipeTestRunViewModelValidator>();
        services.AddScoped<IValidator<UpdateRecipeTestRunViewModel>, UpdateRecipeTestRunViewModelValidator>();
        services.AddScoped<IValidator<TestRunHistoryViewModel>, TestRunHistoryViewModelValidator>();
        services.AddScoped<
            IValidator<RecipeReadinessTransitionViewModel>, RecipeReadinessTransitionViewModelValidator>();
        services.AddScoped<IValidator<ResolveTestIssueViewModel>, ResolveTestIssueViewModelValidator>();
        services.AddScoped<IValidator<ParseIngredientLinesViewModel>, ParseIngredientLinesViewModelValidator>();
        services.AddScoped<IValidator<CreateRecipeViewModel>, CreateRecipeViewModelValidator>();
        services.AddScoped<IValidator<UpdateRecipeViewModel>, UpdateRecipeViewModelValidator>();
        services.AddScoped<IValidator<RecipeSearchViewModel>, RecipeSearchViewModelValidator>();
        services.AddScoped<IValidator<RecipeVersionHistoryViewModel>, RecipeVersionHistoryViewModelValidator>();
        services.AddScoped<IValidator<RecipeVersionComparisonViewModel>, RecipeVersionComparisonViewModelValidator>();
        services.AddScoped<IValidator<ScaleRecipeViewModel>, ScaleRecipeViewModelValidator>();
        services.AddScoped<IValidator<ConvertUnitsViewModel>, ConvertUnitsViewModelValidator>();
        services.AddScoped<IValidator<ConvertTemperatureViewModel>, ConvertTemperatureViewModelValidator>();
        services.AddScoped<IValidator<RecalculateYieldViewModel>, RecalculateYieldViewModelValidator>();
        services.AddScoped<IValidator<NormalizeDisplayViewModel>, NormalizeDisplayViewModelValidator>();
        services.AddScoped<IValidator<RestoreRecipeVersionViewModel>, RestoreRecipeVersionViewModelValidator>();
        services.AddScoped<IValidator<DuplicateRecipeViewModel>, DuplicateRecipeViewModelValidator>();
        services.AddScoped<IValidator<RecipeLifecycleViewModel>, RecipeLifecycleViewModelValidator>();
        services.AddScoped<IValidator<RecipeJsonLdExportViewModel>, RecipeJsonLdExportViewModelValidator>();
        services.AddScoped<IValidator<RecipeMarkdownExportViewModel>, RecipeMarkdownExportViewModelValidator>();
        services.AddScoped<IValidator<RecipePdfExportViewModel>, RecipePdfExportViewModelValidator>();

        // Bound when a host supplies configuration, and left at the catalogue's defaults when it does not — the
        // same shape AddAiUsageModule uses for its quota options. A readiness rule set that failed to register
        // without a configuration file would make every test host carry one to evaluate nothing differently.
        var readiness = services.AddOptions<RecipeReadinessOptions>();

        if (configuration is not null)
        {
            readiness.Bind(configuration.GetSection(RecipeReadinessOptions.SectionName));
        }

        return services;
    }

    /// <summary>
    /// Registers the readiness evaluation's application boundary (TESTRUN-004).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="AddRecipesModule"/> because its prerequisites are different, the same reason
    /// <c>AddAiProposalSeam</c> is separate from <c>AddAiModule</c>. Four of the twenty rules read other modules'
    /// data, so this facade needs <c>IAiProposalFacade</c> and <c>IIngredientFacade</c> — a host registering it must
    /// also call <c>AddAiProposalSeam</c> and <c>AddIngredientModule</c>. The worker has no readiness route and
    /// deliberately does not call this; startup validation catches a host that forgets either prerequisite.
    /// </para>
    /// <para>
    /// The rules, the evaluator and the read they work over are all in <see cref="AddRecipesModule"/> and need
    /// nothing from another module, so a host can evaluate nothing without carrying this.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddRecipeReadinessSeam(this IServiceCollection services)
    {
        services.AddScoped<IRecipeReadinessFacade, RecipeReadinessFacade>();

        // The transition seam rides on the readiness seam rather than being registered on its own, because
        // its one gated move cannot work without it: an approval needs an evaluation, and a host that
        // registered the transitions and not the readiness would resolve a facade that throws on the only
        // move anybody cares about. Registering them together makes that impossible to get half right.
        //
        // The archive and restore routes are not affected either way — they reach the same Business method
        // through IRecipeFacade, which AddRecipesModule has always registered.
        services.AddScoped<IRecipeStatusTransitionFacade, RecipeStatusTransitionFacade>();

        return services;
    }

    /// <summary>
    /// Registers the recipe exports' application boundary (RCPUB-002 onward).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddRecipesModule"/> because its prerequisite is another module, the same reason
    /// <see cref="AddRecipeReadinessSeam"/> is: the facade reads the accepted SEO revision through
    /// <c>IContentSeoFacade</c>, so a host registering it must also call <c>AddContentModule</c>, and it reads cuisine and course names and conversion units through <c>IVocabularyFacade</c> and <c>IMeasurementFacade</c>, so <c>AddVocabularyModule</c> and <c>AddMeasurementModule</c> as well. The Business
    /// and data layer it runs over need nothing from another module and are in <see cref="AddRecipesModule"/>.
    /// Startup validation catches a host that forgets the prerequisite.
    /// </remarks>
    public static IServiceCollection AddRecipeExportSeam(this IServiceCollection services)
    {
        services.AddScoped<IRecipeExportFacade, RecipeExportFacade>();

        return services;
    }
}
