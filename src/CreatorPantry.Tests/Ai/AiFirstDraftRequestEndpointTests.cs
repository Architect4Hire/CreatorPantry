extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using FluentValidation;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-002's request route: what a client may ask for, and what it cannot reach.
/// </summary>
public sealed class AiFirstDraftRequestEndpointTests
{
    // ---- field shape -------------------------------------------------------------------------------------

    /// <summary>
    /// The contract's real content is what is <em>absent</em>. A client names a selected concept by id and the
    /// eleven declared brief fields; it cannot name a task, a scope, a recipe, a prompt, a model, a provider
    /// parameter, a tool list, or a workspace, because the type has nowhere to put one.
    /// </summary>
    [Fact]
    public void The_request_carries_only_two_concept_ids_and_the_twelve_declared_brief_fields()
    {
        var fields = typeof(RequestRecipeFirstDraftViewModel).GetProperties()
            .Where(property => property.GetMethod?.IsPublic == true)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "Audience", "AvailableIngredients", "Course", "CreatorStyle", "Cuisine", "DietaryGoals",
                "DishName", "Equipment", "Exclusions", "Season", "Skill", "SourceConceptId",
                "SourceConceptRequestId", "TimeBudget",
            ],
            fields);
    }

    /// <remarks>
    /// "recipe" is absent from this list although the route is about one: there is no recipe yet, and a field
    /// that could name one would make this route able to write to a creator's existing content. 9.4b's
    /// acceptance step is the only thing that ever creates one.
    /// </remarks>
    [Theory]
    [InlineData("task")]
    [InlineData("scope")]
    [InlineData("recipe")]
    [InlineData("prompt")]
    [InlineData("template")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("tool")]
    [InlineData("workspace")]
    [InlineData("system")]
    public void The_request_cannot_name_a_task_a_recipe_or_a_provider_concern(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(RequestRecipeFirstDraftViewModel).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The concept arrives as two ids and nothing else. A client that could post concept prose could present
    /// text the server never generated -- or another workspace's -- as the grounding for a draft.
    /// </summary>
    [Fact]
    public void A_selected_concept_is_named_by_id_and_never_carried_as_text()
    {
        var conceptFields = typeof(RequestRecipeFirstDraftViewModel).GetProperties()
            .Where(property => property.Name.Contains("Concept", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, conceptFields.Length);
        Assert.All(conceptFields, property => Assert.Equal(typeof(Guid?), property.PropertyType));
    }

    // ---- validator ---------------------------------------------------------------------------------------

    [Fact]
    public void A_brief_with_one_field_validates()
    {
        var result = Validate(new RequestRecipeFirstDraftViewModel { Cuisine = "Sichuan" });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void A_fully_populated_brief_validates()
    {
        var result = Validate(new RequestRecipeFirstDraftViewModel
        {
            Audience = "weeknight home cooks",
            Course = "dinner",
            Cuisine = "Sichuan",
            DietaryGoals = "vegetarian",
            AvailableIngredients = "tofu, doubanjiang, scallions",
            Exclusions = "peanuts, shellfish",
            Equipment = "wok, rice cooker",
            Skill = "beginner",
            Season = "winter",
            TimeBudget = "30 minutes",
            CreatorStyle = "playful, kid-friendly",
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void A_selected_concept_with_no_brief_at_all_validates()
    {
        var result = Validate(new RequestRecipeFirstDraftViewModel
        {
            SourceConceptRequestId = Guid.NewGuid(),
            SourceConceptId = Guid.NewGuid(),
        });

        Assert.True(result.IsValid);
    }

    /// <summary>
    /// The one place this validator is stricter than AIREC-001's. "Pitch me anything" is a real request for
    /// concepts; "write me a complete recipe from nothing" is not a creator's recipe in any sense, and it
    /// still spends a provider budget.
    /// </summary>
    [Fact]
    public void A_request_naming_neither_a_concept_nor_any_brief_field_is_rejected()
    {
        var result = Validate(new RequestRecipeFirstDraftViewModel());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_brief_of_nothing_but_whitespace_does_not_count_as_a_brief()
    {
        var result = Validate(new RequestRecipeFirstDraftViewModel { Cuisine = "   ", Course = "\t" });

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void One_source_id_without_the_other_is_rejected(bool hasRequestId, bool hasConceptId)
    {
        var result = Validate(new RequestRecipeFirstDraftViewModel
        {
            SourceConceptRequestId = hasRequestId ? Guid.NewGuid() : null,
            SourceConceptId = hasConceptId ? Guid.NewGuid() : null,
            Cuisine = "Sichuan",
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void An_empty_guid_is_not_a_concept_id()
    {
        var result = Validate(new RequestRecipeFirstDraftViewModel
        {
            SourceConceptRequestId = Guid.NewGuid(),
            SourceConceptId = Guid.Empty,
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_short_field_over_its_bound_is_rejected()
    {
        var result = Validate(new RequestRecipeFirstDraftViewModel
        {
            Cuisine = new string('x', AiPolicy.BriefFieldMaxLength + 1),
        });

        Assert.Contains(
            result.Errors, error => error.PropertyName == nameof(RequestRecipeFirstDraftViewModel.Cuisine));
    }

    [Fact]
    public void A_list_style_field_over_its_bound_is_rejected()
    {
        var result = Validate(new RequestRecipeFirstDraftViewModel
        {
            Exclusions = new string('x', AiPolicy.BriefListFieldMaxLength + 1),
        });

        Assert.Contains(
            result.Errors, error => error.PropertyName == nameof(RequestRecipeFirstDraftViewModel.Exclusions));
    }

    // ---- stored input schema -----------------------------------------------------------------------------

    /// <summary>
    /// The vocabulary is exactly two disjoint sets. Which of them reaches a prompt, and whether the handler
    /// actually renders every rendered key, is proven against the real handler in
    /// <c>RecipeFirstDraftAiTaskHandlerTests</c> -- the side that does the rendering.
    /// </summary>
    [Fact]
    public void The_rendered_and_provenance_keys_together_are_the_whole_vocabulary()
    {
        Assert.Equal(
            AiFirstDraftInputs.Rendered
                .Concat(AiFirstDraftInputs.Provenance)
                .Concat(AiFirstDraftInputs.WorkspaceFacts)
                .Order(StringComparer.Ordinal),
            AiFirstDraftInputs.All.Order(StringComparer.Ordinal));

        Assert.Equal(
            AiFirstDraftInputs.Rendered.Count
                + AiFirstDraftInputs.Provenance.Count
                + AiFirstDraftInputs.WorkspaceFacts.Count,
            AiFirstDraftInputs.All.Count);
    }

    // ---- routes ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Requesting_a_draft_without_a_session_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            Route,
            new RequestRecipeFirstDraftViewModel { Cuisine = "Sichuan" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reading_a_request_without_a_session_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync(
            $"{Route}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- idempotency -------------------------------------------------------------------------------------

    /// <summary>
    /// A missing key is refused at the Facade, before Business (and therefore before anything is queued) --
    /// proven by a business stub that throws if it is ever reached.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_idempotency_key_is_refused_before_business_is_ever_called(string? key)
    {
        IAiFirstDraftRequestFacade facade = new AiFirstDraftRequestFacade(
            new NeverCalledBusiness(),
            new NeverCalledAcceptance(),
            new RequestRecipeFirstDraftViewModelValidator(),
            new AiDraftAcceptanceViewModelValidator(),
            workspaces: null!);

        var outcome = await facade.RequestAsync(
            new RequestRecipeFirstDraftViewModel { Cuisine = "Sichuan" },
            key,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Replayed);
        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, outcome.Result.Error!.Code);
    }

    /// <summary>An invalid request is refused before the key is even looked at, and never reaches Business.</summary>
    [Fact]
    public async Task An_invalid_request_is_refused_before_business_is_ever_called()
    {
        IAiFirstDraftRequestFacade facade = new AiFirstDraftRequestFacade(
            new NeverCalledBusiness(),
            new NeverCalledAcceptance(),
            new RequestRecipeFirstDraftViewModelValidator(),
            new AiDraftAcceptanceViewModelValidator(),
            workspaces: null!);

        var outcome = await facade.RequestAsync(
            new RequestRecipeFirstDraftViewModel(), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiFirstDraftRequestErrors.RequestInvalid, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// The system a draft is pinned to is the resolved workspace's, read facade to facade. The request has no
    /// field that could carry one (see the field list above), so this is the only way a value gets there.
    /// </summary>
    [Theory]
    [InlineData(MeasurementSystem.Metric)]
    [InlineData(MeasurementSystem.UsCustomary)]
    public async Task The_facade_passes_the_resolved_workspaces_measurement_system_to_business(MeasurementSystem system)
    {
        var business = new CapturingBusiness();
        IAiFirstDraftRequestFacade facade = new AiFirstDraftRequestFacade(
            business,
            new NeverCalledAcceptance(),
            new RequestRecipeFirstDraftViewModelValidator(),
            new AiDraftAcceptanceViewModelValidator(),
            new WorkspaceWithSystem(system));

        await facade.RequestAsync(
            new RequestRecipeFirstDraftViewModel { Cuisine = "Sichuan" }, "key-1", TestContext.Current.CancellationToken);

        Assert.Equal(system, business.MeasurementSystem);
    }

    private sealed class CapturingBusiness : IAiFirstDraftRequestBusiness
    {
        public MeasurementSystem? MeasurementSystem { get; private set; }

        public Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
            RequestRecipeFirstDraftViewModel model,
            MeasurementSystem measurementSystem,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            MeasurementSystem = measurementSystem;

            return Task.FromResult(new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(
                    new OperationError("unused", "unused", new Dictionary<string, string[]>())),
                Replayed: false));
        }

        public Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
            Guid requestId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not exercised by this test.");
    }

    /// <summary>Stands in for Tenancy: answers the one read the draft facade makes and nothing else.</summary>
    private sealed class WorkspaceWithSystem(MeasurementSystem system) : IWorkspaceFacade
    {
        public Task<WorkspaceServiceModel> GetCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkspaceServiceModel(
                Guid.NewGuid(), "A", "workspace-a", DateTimeOffset.UnixEpoch, Guid.NewGuid(), WorkspaceRole.Owner, system));

        public Task<IReadOnlyList<MyWorkspaceMembershipServiceModel>> GetMyMembershipsAsync(
            string userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A draft request has no business listing the caller's workspaces.");

        public Task<OperationResult<WorkspaceServiceModel>> CreateAsync(
            string userId, CreateWorkspaceViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A draft request has no business creating a workspace.");

        public Task<OperationResult<WorkspaceServiceModel>> RenameCurrentAsync(
            UpdateWorkspaceViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A draft request has no business renaming a workspace.");

        public Task<OperationResult<WorkspaceServiceModel>> SetMeasurementPreferenceCurrentAsync(
            SetMeasurementPreferenceViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A draft request has no business changing a workspace's settings.");

        public Task<IReadOnlyDictionary<Guid, string>> FindMemberDisplayNamesAsync(
            IReadOnlyCollection<Guid> membershipIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A draft request has no business naming members.");
    }

    private sealed class NeverCalledAcceptance : IAiDraftAcceptanceBusiness
    {
        public Task<OperationResult<AiDraftAcceptanceServiceModel>> AcceptAsync(
            string actorUserId,
            Guid requestId,
            AiDraftAcceptanceViewModel model,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not exercised by this test.");
    }

    private sealed class NeverCalledBusiness : IAiFirstDraftRequestBusiness
    {
        public Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
            RequestRecipeFirstDraftViewModel model,
            CreatorPantry.Domain.Modules.Measurement.Managers.MeasurementSystem measurementSystem,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Business must not be reached by a refused request.");

        public Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
            Guid requestId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not exercised by this test.");
    }

    // ---- error codes -------------------------------------------------------------------------------------

    /// <summary>Every code this seam can return is namespaced and distinct.</summary>
    [Fact]
    public void The_error_codes_are_stable_and_distinct()
    {
        var codes = typeof(AiFirstDraftRequestErrors).GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.StartsWith("ai.", code, StringComparison.Ordinal));
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The two absences a creator must not be able to tell apart both answer 404, and the rest answer 400 --
    /// decided by the code's own suffix, which is what makes the route's documented statuses true.
    /// </summary>
    [Theory]
    [InlineData(nameof(AiFirstDraftRequestErrors.ConceptNotFound), 404)]
    [InlineData(nameof(AiFirstDraftRequestErrors.RequestNotFound), 404)]
    [InlineData(nameof(AiFirstDraftRequestErrors.RequestInvalid), 400)]
    [InlineData(nameof(AiFirstDraftRequestErrors.TaskNotEnabled), 400)]
    [InlineData(nameof(AiFirstDraftRequestErrors.RequestTooLarge), 400)]
    public void Each_error_code_maps_to_the_status_the_route_documents(string name, int expected)
    {
        var code = (string)typeof(AiFirstDraftRequestErrors).GetField(name)!.GetRawConstantValue()!;

        Assert.Equal(expected, ApiService::CreatorPantry.ApiService.Http.ProblemResults.StatusFor(code));
    }

    private static FluentValidation.Results.ValidationResult Validate(RequestRecipeFirstDraftViewModel model) =>
        new RequestRecipeFirstDraftViewModelValidator().Validate(model);

    private const string Route = "/api/v1/workspaces/workspace-a/recipe-draft-requests";
}
