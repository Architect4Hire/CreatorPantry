using CreatorPantry.Domain.Managers.Reference;
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

        // The SEO length and format rules: configuration over conventional defaults, refused at startup if unmeetable.
        services.AddSingleton(SeoRules.Bind(configuration));
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
        AddRequestQuotaGate(services);
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
        AddRequestQuotaGate(services);
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
        AddRequestQuotaGate(services);
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
        AddRequestQuotaGate(services);
        services.AddScoped<IAiRevisionRequestBusiness, AiRevisionRequestBusiness>();
        services.AddScoped<IAiRevisionRequestFacade, AiRevisionRequestFacade>();
        services.AddScoped<IValidator<RequestRecipeRevisionViewModel>, RequestRecipeRevisionViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Registers the request seam for AIREC-004's ingredient substitution advice.
    /// </summary>
    /// <remarks>
    /// Carries the recipe-module prerequisite <see cref="AddAiRevisionRequestSeam"/> does, and one more
    /// besides: <c>AiSubstitutionRequestBusiness</c> reads the pinned version's snapshot through
    /// <c>IRecipeFacade</c> to check that the selected ingredient is actually in it, so the dependency is
    /// exercised on every request rather than only on acceptance.
    /// </remarks>
    public static IServiceCollection AddAiSubstitutionRequestSeam(this IServiceCollection services)
    {
        AddRequestQuotaGate(services);
        services.AddScoped<IAiSubstitutionRequestBusiness, AiSubstitutionRequestBusiness>();
        services.AddScoped<IAiSubstitutionRequestFacade, AiSubstitutionRequestFacade>();
        services.AddScoped<
            IValidator<RequestIngredientSubstitutionViewModel>,
            RequestIngredientSubstitutionViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Registers the request seam for AIREC-005's single-goal recipe adaptation.
    /// </summary>
    /// <remarks>
    /// Carries the recipe-module prerequisite <see cref="AddAiRevisionRequestSeam"/> does, and one more
    /// besides: a yield-goal request calls <c>IRecipeFacade.ScaleAsync</c> to pre-check its target before the
    /// operation is even queued, the same dependency <see cref="AddAiSubstitutionRequestSeam"/> exercises on
    /// every request for its own reason.
    /// </remarks>
    public static IServiceCollection AddAiAdaptationRequestSeam(this IServiceCollection services)
    {
        AddRequestQuotaGate(services);
        services.AddScoped<IAiAdaptationRequestBusiness, AiAdaptationRequestBusiness>();
        services.AddScoped<IAiAdaptationRequestFacade, AiAdaptationRequestFacade>();
        services.AddScoped<
            IValidator<RequestRecipeAdaptationViewModel>,
            RequestRecipeAdaptationViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Registers the request seam for AIREC-006's recipe quality and safety review.
    /// </summary>
    /// <remarks>
    /// Carries the recipe-module prerequisite <see cref="AddAiSubstitutionRequestSeam"/> does, for the same
    /// reason: <c>AiReviewRequestBusiness</c> checks the recipe and its current version through
    /// <c>IRecipeFacade</c> before anything is queued.
    /// </remarks>
    public static IServiceCollection AddAiReviewRequestSeam(this IServiceCollection services)
    {
        AddRequestQuotaGate(services);
        services.AddScoped<IAiReviewRequestBusiness, AiReviewRequestBusiness>();
        services.AddScoped<IAiReviewRequestFacade, AiReviewRequestFacade>();
        services.AddScoped<IValidator<RequestRecipeReviewViewModel>, RequestRecipeReviewViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Registers the brand-guide preview a writing screen reads before it generates (11A.21a): a read of the
    /// active guide and what it would contribute to a task. Needs the brand module and the channel catalogue.
    /// </summary>
    public static IServiceCollection AddBrandWritingGuideSeam(this IServiceCollection services)
    {
        services.AddScoped<IBrandWritingGuideBusiness, BrandWritingGuideBusiness>();
        services.AddScoped<IBrandWritingGuideFacade, BrandWritingGuideFacade>();
        services.AddScoped<IValidator<BrandWritingGuideViewModel>, BrandWritingGuideViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Registers the request seam for RCPUB-001's editorial package.
    /// </summary>
    /// <remarks>
    /// Carries the prerequisites <see cref="AddAiReviewRequestSeam"/> does, plus the brand module:
    /// <c>AiEditorialPackageRequestBusiness</c> reads the workspace's brand profile through
    /// <c>IBrandProfileFacade</c> to pin its facts into the operation.
    /// </remarks>
    public static IServiceCollection AddAiEditorialPackageRequestSeam(this IServiceCollection services)
    {
        AddRequestQuotaGate(services);
        services.AddScoped<IAiEditorialPackageRequestBusiness, AiEditorialPackageRequestBusiness>();
        services.AddScoped<IAiEditorialPackageRequestFacade, AiEditorialPackageRequestFacade>();
        services.AddScoped<IValidator<RequestEditorialPackageViewModel>, RequestEditorialPackageViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Registers the request seam for RCPUB-002's SEO package. Same prerequisites as
    /// <see cref="AddAiEditorialPackageRequestSeam"/>; the length rules are bound by <see cref="AddAiModule"/>.
    /// </summary>
    public static IServiceCollection AddAiSeoPackageRequestSeam(this IServiceCollection services)
    {
        AddRequestQuotaGate(services);
        services.AddScoped<IAiSeoPackageRequestBusiness, AiSeoPackageRequestBusiness>();
        services.AddScoped<IAiSeoPackageRequestFacade, AiSeoPackageRequestFacade>();
        services.AddScoped<IValidator<RequestSeoPackageViewModel>, RequestSeoPackageViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Registers the request seam for 11A.17's brand-guide proposal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its prerequisite is the <strong>brand</strong> module rather than the recipe one, which no earlier seam
    /// here can say: the guide, the selected documents and the passages it grounds on are all the brand module's,
    /// and this capability reads no recipe at all. <c>AddContentChannelCatalog</c> comes with that module, and
    /// the channel keys a request may offer are checked against it.
    /// </para>
    /// <para>
    /// 11A.18's acceptance is registered here too, with the request it decides. It deepens that same
    /// prerequisite rather than adding one: requesting a proposal only reads the brand module, and accepting one
    /// writes a guide version through <c>IBrandStyleGuideFacade</c>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAiBrandGuideProposalRequestSeam(this IServiceCollection services)
    {
        AddRequestQuotaGate(services);
        services.AddScoped<IAiBrandGuideProposalRequestBusiness, AiBrandGuideProposalRequestBusiness>();
        services.AddScoped<IAiBrandGuideAcceptanceBusiness, AiBrandGuideAcceptanceBusiness>();
        services.AddScoped<IAiBrandGuideProposalRequestFacade, AiBrandGuideProposalRequestFacade>();
        services.AddScoped<
            IValidator<RequestBrandGuideProposalViewModel>,
            RequestBrandGuideProposalViewModelValidator>();
        services.AddScoped<
            IValidator<AcceptBrandGuideProposalViewModel>,
            AcceptBrandGuideProposalViewModelValidator>();

        return services;
    }

    /// <summary>
    /// Registers 11A.19's brand-context assembler, which grounds a generation in the workspace's brand voice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="AddAiBrandGuideProposalRequestSeam"/> because the callers differ: that one
    /// serves an HTTP route, and this serves task handlers in the worker. A host that runs generations needs
    /// this and not that.
    /// </para>
    /// <para>
    /// Its prerequisite is the <strong>brand</strong> module — the profile, the active guide, the source library
    /// and the indexed passages are all read through its facades — and <c>AddContentChannelCatalog</c>, which
    /// comes with that module and is what a requested channel key is checked against.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAiBrandContext(this IServiceCollection services)
    {
        services.AddScoped<IBrandContextAssembler, BrandContextAssembler>();

        return services;
    }

    /// <summary>
    /// Registers the request seam for AIREC-008's explanation of an existing proposal.
    /// </summary>
    /// <remarks>
    /// No recipe-module prerequisite, unlike every seam above it: <c>AiProposalExplanationRequestBusiness</c>
    /// reads its source through <see cref="Data.IAiOperationDataLayer"/>, the same seam every request business
    /// already depends on, never through <c>IRecipeFacade</c> — the source proposal, not a recipe snapshot, is
    /// what this capability explains.
    /// </remarks>
    public static IServiceCollection AddAiProposalExplanationRequestSeam(this IServiceCollection services)
    {
        AddRequestQuotaGate(services);
        services.AddScoped<IAiProposalExplanationRequestBusiness, AiProposalExplanationRequestBusiness>();
        services.AddScoped<IAiProposalExplanationRequestFacade, AiProposalExplanationRequestFacade>();
        services.AddScoped<
            IValidator<RequestProposalExplanationViewModel>,
            RequestProposalExplanationViewModelValidator>();

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
        services.AddKeyedScoped<IAiTaskHandler, IngredientSubstitutionAiTaskHandler>(
            AiTaskType.IngredientSubstitution);
        services.AddKeyedScoped<IAiTaskHandler, RecipeAdaptationAiTaskHandler>(AiTaskType.RecipeAdaptation);
        services.AddKeyedScoped<IAiTaskHandler, RecipeReviewAiTaskHandler>(AiTaskType.RecipeReview);
        services.AddKeyedScoped<IAiTaskHandler, AiProposalExplanationAiTaskHandler>(AiTaskType.ProposalExplanation);
        services.AddKeyedScoped<IAiTaskHandler, EditorialPackageAiTaskHandler>(AiTaskType.EditorialPackage);
        services.AddKeyedScoped<IAiTaskHandler, SeoPackageAiTaskHandler>(AiTaskType.SeoPackage);
        services.AddKeyedScoped<IAiTaskHandler, BrandGuideProposalAiTaskHandler>(AiTaskType.BrandGuideProposal);
        services.AddScoped<IAiOperationWorker, AiOperationWorker>();

        return services;
    }

    /// <summary>
    /// Registers the account-usage reconciliation (USAGE-002): the one-time backfill and the repeatable drift
    /// check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own call rather than part of <see cref="AddAiOperationWorker"/>, because it is a different
    /// deployment decision: a host may run AI operations without running a sweep over every workspace's
    /// execution metadata, and the one place that does should say so out loud.
    /// </para>
    /// <para>
    /// Requires <c>AddAiUsageModule</c>: the ledger write goes through that module's facade, which is what
    /// makes the pass unable to double-post.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAiUsageReconciliation(
        this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddScoped<AiUsageReconciliationRepository>();
        services.AddScoped<IAiUsageReconciler, AiUsageReconciler>();

        var options = services.AddOptions<AiUsageReconciliationOptions>();

        if (configuration is not null)
        {
            options.Bind(configuration.GetSection(AiUsageReconciliationOptions.SectionName));
        }

        return services;
    }

    /// <summary>
    /// Registers the quota gate every request seam refuses through (USAGE-006).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by each <c>AddAi*Seam</c> rather than by <see cref="AddAiModule"/>, so a host that registers one
    /// route gets it and a host that registers none does not carry it. <c>TryAdd</c> because the seams are
    /// independent and most hosts register several — the repeat is expected, not a mistake.
    /// </para>
    /// <para>
    /// It depends on the AiUsage module's quota facade, so a host registering any request seam must also call
    /// <c>AddAiUsageModule</c>. That is not a new prerequisite: the same host already needed it for the usage
    /// ledger every settled attempt posts to.
    /// </para>
    /// </remarks>
    private static void AddRequestQuotaGate(IServiceCollection services) =>
        services.TryAddScoped<IAiRequestQuotaGate, AiRequestQuotaGate>();
}
