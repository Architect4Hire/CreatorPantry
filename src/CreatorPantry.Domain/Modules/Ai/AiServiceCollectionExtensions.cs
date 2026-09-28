using CreatorPantry.Domain.Managers.Prompts;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Facade;
using FluentValidation;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Modules.Ai;

public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AI module's provider gateway and the pieces it needs.
    /// </summary>
    /// <param name="resiliencePipelineKey">
    /// The named pipeline provider calls run inside, from <c>AiResilience.PipelineKey</c>. Passed in because
    /// this project may not reference ServiceDefaults, and a duplicated string constant is the drift this whole
    /// module has been avoiding elsewhere.
    /// </param>
    /// <remarks>
    /// <see cref="IAiFailureClassifier"/> is registered with <c>TryAdd</c>, so a host that has already supplied
    /// a provider-specific classifier keeps it and one that has not falls back to the conservative default. The
    /// default is genuinely weaker — it cannot see a provider SDK's status code — so a deployed host should
    /// register the specific one before calling this.
    /// </remarks>
    public static IServiceCollection AddAiModule(
        this IServiceCollection services,
        IConfiguration configuration,
        string resiliencePipelineKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(resiliencePipelineKey);

        var gatewayOptions = new AiGatewayOptions { ResiliencePipelineKey = resiliencePipelineKey };
        configuration.GetSection(AiGatewayOptions.SectionName).Bind(gatewayOptions);

        // Rebound after binding: configuration must not be able to point the gateway at a pipeline the host
        // never registered, which would fail at the first provider call rather than at startup.
        gatewayOptions.ResiliencePipelineKey = resiliencePipelineKey;

        var costOptions = new AiCostOptions();
        configuration.GetSection(AiCostOptions.SectionName).Bind(costOptions);

        services.AddSingleton(gatewayOptions);
        services.AddSingleton(costOptions);
        services.TryAddSingleton<IAiCostEstimator, ConfiguredAiCostEstimator>();
        services.TryAddSingleton<IAiFailureClassifier, DefaultAiFailureClassifier>();
        services.AddScoped<IAiCompletionGateway, AiCompletionGateway>();
        services.AddScoped<IAiOperationRepository, AiOperationRepository>();
        services.AddScoped<IAiOperationDataLayer, AiOperationDataLayer>();

        // Concrete rather than behind an interface: it is the documented cross-workspace carve-out, and giving
        // it an interface would invite something else to be registered as one.
        services.AddScoped<AiOperationClaimRepository>();

        services.AddSingleton(AiTaskCatalog.Bind(configuration));
        services.AddPromptTemplates();

        return services;
    }

    /// <summary>
    /// Registers the request seam a host serving the AI proposal routes needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="AddAiModule"/> because it carries a prerequisite that host does not.
    /// <c>AiProposalBusiness</c> calls the recipe module's facade to check the recipe and pin the source
    /// version, so <c>AddRecipesModule</c> has to be registered already.
    /// </para>
    /// <para>
    /// The worker calls <see cref="AddAiModule"/> and not this. It serves no HTTP request and has no use for a
    /// facade, and registering the recipe module there purely to satisfy a dependency it never exercises would
    /// be half the domain carried for nothing. When the proposal worker needs the recipe facade for its own
    /// reasons, it will register that module deliberately.
    /// </para>
    /// <para>
    /// Splitting this was not a preference. Registering both in one call made the worker fail to start on
    /// <c>IRecipeFacade</c>, which is the container validating exactly the thing it is there to validate.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAiProposalSeam(this IServiceCollection services)
    {
        services.AddScoped<IAiProposalBusiness, AiProposalBusiness>();
        services.AddScoped<IAiProposalFacade, AiProposalFacade>();
        services.AddScoped<IValidator<RequestAiProposalViewModel>, RequestAiProposalViewModelValidator>();
        services.AddScoped<IValidator<AiProposalDispositionViewModel>, AiProposalDispositionViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Registers the request seam for AIREC-001's recipe-concept generation.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddAiModule"/> for the same reason <see cref="AddAiProposalSeam"/> is: a host
    /// with no use for this route should not carry its validator and business type just to call
    /// <see cref="AddAiModule"/>. Unlike <see cref="AddAiProposalSeam"/>, this carries no recipe-module
    /// prerequisite — a concept request names no recipe, so nothing here ever calls <c>IRecipeFacade</c>.
    /// </remarks>
    public static IServiceCollection AddAiConceptRequestSeam(this IServiceCollection services)
    {
        services.AddScoped<IAiConceptRequestBusiness, AiConceptRequestBusiness>();
        services.AddScoped<IAiConceptRequestFacade, AiConceptRequestFacade>();
        services.AddScoped<IValidator<RequestRecipeConceptsViewModel>, RequestRecipeConceptsViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Registers the request seam for AIREC-002's structured first-draft generation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="AddAiModule"/> for the reason <see cref="AddAiConceptRequestSeam"/> is.
    /// </para>
    /// <para>
    /// <strong>Unlike <see cref="AddAiConceptRequestSeam"/>, this one does carry the recipe-module
    /// prerequisite.</strong> Requesting a draft names no recipe, but accepting one creates it —
    /// <c>AiDraftAcceptanceBusiness</c> calls <c>IRecipeFacade</c>, so <c>AddRecipesModule</c> must be
    /// registered already. The container validates that at startup rather than at the first acceptance.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAiFirstDraftRequestSeam(this IServiceCollection services)
    {
        services.AddScoped<IAiFirstDraftRequestBusiness, AiFirstDraftRequestBusiness>();
        services.AddScoped<IAiDraftAcceptanceBusiness, AiDraftAcceptanceBusiness>();
        services.AddScoped<IAiFirstDraftRequestFacade, AiFirstDraftRequestFacade>();
        services.AddScoped<IValidator<RequestRecipeFirstDraftViewModel>, RequestRecipeFirstDraftViewModelValidator>();
        services.AddScoped<IValidator<AiDraftAcceptanceViewModel>, AiDraftAcceptanceViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Registers the request seam for AIREC-003’s scoped recipe revision.
    /// </summary>
    /// <remarks>
    /// Carries the recipe-module prerequisite that <see cref="AddAiProposalSeam"/> does: a revision names a
    /// recipe and the exact version it revises, and <c>AiRevisionRequestBusiness</c> checks both through
    /// <c>IRecipeFacade</c> before anything is queued.
    /// </remarks>
    public static IServiceCollection AddAiRevisionRequestSeam(this IServiceCollection services)
    {
        services.AddScoped<IAiRevisionRequestBusiness, AiRevisionRequestBusiness>();
        services.AddScoped<IAiRevisionRequestFacade, AiRevisionRequestFacade>();
        services.AddScoped<IValidator<RequestRecipeRevisionViewModel>, RequestRecipeRevisionViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="IAiOperationWorker"/> and the task handlers it dispatches to, keyed by
    /// <see cref="AiTaskType"/>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddAiModule"/> for the same reason <see cref="AddAiProposalSeam"/> is:
    /// <see cref="DiagnosticAiTaskHandler"/> calls the recipe module's facade, so this carries a prerequisite
    /// — <c>AddRecipesModule</c>, and <c>AddTenancy</c> for <see cref="Tenancy.Facade.IWorkspaceResolutionFacade"/>
    /// — that a host with no use for either should not be made to carry just to call <see cref="AddAiModule"/>.
    /// Only the worker calls this.
    /// </remarks>
    public static IServiceCollection AddAiOperationWorker(this IServiceCollection services)
    {
        services.AddKeyedScoped<IAiTaskHandler, DiagnosticAiTaskHandler>(AiTaskType.Diagnostic);
        services.AddKeyedScoped<IAiTaskHandler, RecipeConceptsAiTaskHandler>(AiTaskType.RecipeConcepts);
        services.AddKeyedScoped<IAiTaskHandler, RecipeFirstDraftAiTaskHandler>(AiTaskType.RecipeFirstDraft);
        services.AddKeyedScoped<IAiTaskHandler, RecipeRevisionAiTaskHandler>(AiTaskType.RecipeRevision);
        services.AddScoped<IAiOperationWorker, AiOperationWorker>();

        return services;
    }
}
