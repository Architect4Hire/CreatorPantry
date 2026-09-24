using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The facade's own responsibilities: who may call it, what it refuses before anything is written, which
/// references it confirms with the modules that own them, and how a replayed key behaves.
/// </summary>
public sealed class RecipeFacadeTests
{
    private const string UserId = "user-1";

    private readonly RecordingRecipeBusiness _business = new();
    private readonly StubReferenceModules _references = new();
    private readonly StubWorkspaceDirectory _workspaces = new();
    private readonly ReplayingIdempotency _idempotency = new();

    private IRecipeFacade Facade(WorkspaceRole role = WorkspaceRole.Contributor) =>
        Facade(new StubWorkspaceContext(role));

    private IRecipeFacade Facade(StubWorkspaceContext workspace) =>
        new ServiceCollection()
            .AddSingleton<FluentValidation.IValidator<CreateRecipeViewModel>>(new CreateRecipeViewModelValidator())
            .AddSingleton<FluentValidation.IValidator<UpdateRecipeViewModel>>(new UpdateRecipeViewModelValidator())
            .AddSingleton<FluentValidation.IValidator<RecipeSearchViewModel>>(new RecipeSearchViewModelValidator())
            .AddSingleton<FluentValidation.IValidator<RecipeVersionHistoryViewModel>>(
                new RecipeVersionHistoryViewModelValidator())
            .AddSingleton<FluentValidation.IValidator<RecipeVersionComparisonViewModel>>(
                new RecipeVersionComparisonViewModelValidator())
            .AddSingleton<FluentValidation.IValidator<RestoreRecipeVersionViewModel>>(
                new RestoreRecipeVersionViewModelValidator())
            .AddSingleton<FluentValidation.IValidator<DuplicateRecipeViewModel>>(
                new DuplicateRecipeViewModelValidator())
            .AddSingleton<FluentValidation.IValidator<RecipeLifecycleViewModel>>(
                new RecipeLifecycleViewModelValidator())
            .AddSingleton<IRecipeBusiness>(_business)
            .AddSingleton<IWorkspaceContext>(workspace)
            .AddSingleton<IVocabularyFacade>(_references)
            .AddSingleton<IMeasurementFacade>(_references)
            .AddSingleton<IWorkspaceFacade>(_workspaces)
            .AddSingleton<IIdempotentCommandExecutor>(_idempotency)
            .AddSingleton<IRecipeFacade, RecipeFacade>()
            .BuildServiceProvider()
            .GetRequiredService<IRecipeFacade>();

    private Task<IdempotentOutcome<CreatedRecipeServiceModel>> CreateAsync(
        CreateRecipeViewModel model, WorkspaceRole role = WorkspaceRole.Contributor, string? key = null) =>
        Facade(role).CreateAsync(UserId, model, key, TestContext.Current.CancellationToken);

    // ---- Authorization ----

    [Theory]
    [InlineData(WorkspaceRole.Contributor)]
    [InlineData(WorkspaceRole.Editor)]
    [InlineData(WorkspaceRole.Owner)]
    public async Task Contributor_and_above_may_create(WorkspaceRole role)
    {
        var result = await CreateAsync(new CreateRecipeViewModel { Title = "Cake" }, role);

        Assert.True(result.Result.Succeeded);
    }

    [Fact]
    public async Task A_viewer_may_not_create()
    {
        var result = await CreateAsync(new CreateRecipeViewModel { Title = "Cake" }, WorkspaceRole.Viewer);

        Assert.False(result.Result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeForbidden, result.Result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task Authorization_is_decided_before_the_payload_is_examined()
    {
        // A viewer sending a broken body learns they may not do this at all, not which field is wrong.
        var result = await CreateAsync(new CreateRecipeViewModel { Title = null }, WorkspaceRole.Viewer);

        Assert.Equal(RecipeErrorCodes.RecipeForbidden, result.Result.Error!.Code);
    }

    // ---- Validation ----

    [Fact]
    public async Task An_invalid_body_is_refused_with_field_errors()
    {
        var result = await CreateAsync(new CreateRecipeViewModel { Title = "   " });

        Assert.False(result.Result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeInvalidRequest, result.Result.Error!.Code);
        Assert.Contains("title", result.Result.Error.FieldErrors.Keys);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task A_refused_request_never_reaches_the_idempotency_executor()
    {
        // Only committed outcomes are replayable, so a rejected request must leave the key unused.
        await CreateAsync(new CreateRecipeViewModel { Title = null }, key: "key-1");

        Assert.Equal(0, _idempotency.Calls);
    }

    // ---- Cross-module reference checks ----

    [Fact]
    public async Task An_unknown_cuisine_is_refused_rather_than_left_to_the_foreign_key()
    {
        _references.UsableVocabulary = false;

        var result = await CreateAsync(new CreateRecipeViewModel { Title = "Cake", CuisineId = Guid.NewGuid() });

        // Without this check the id would reach the database and fail as a DbUpdateException — a 500 where
        // the honest answer names the field.
        Assert.False(result.Result.Succeeded);
        Assert.Contains("cuisineId", result.Result.Error!.FieldErrors.Keys);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task Every_bad_reference_is_reported_at_once()
    {
        _references.UsableVocabulary = false;
        _references.UnitDimension = null;

        var result = await CreateAsync(new CreateRecipeViewModel
        {
            Title = "Cake",
            CuisineId = Guid.NewGuid(),
            CourseId = Guid.NewGuid(),
            PrimaryTechniqueId = Guid.NewGuid(),
            YieldQuantity = 12m,
            YieldUnitId = Guid.NewGuid(),
        });

        // One round trip for the creator rather than four corrections in sequence.
        Assert.Equal(
            (string[])["courseId", "cuisineId", "primaryTechniqueId", "yieldUnitId"],
            result.Result.Error!.FieldErrors.Keys.Order());
    }

    [Fact]
    public async Task The_resolved_unit_dimension_is_handed_to_business()
    {
        _references.UnitDimension = MeasurementDimension.Count;

        await CreateAsync(new CreateRecipeViewModel
        {
            Title = "Cake",
            YieldQuantity = 12m,
            YieldUnitId = Guid.NewGuid(),
        });

        // Business cannot look this up — the Measurement module owns it — so the facade resolving and passing
        // it is the whole reason that parameter exists.
        Assert.Equal(MeasurementDimension.Count, _business.YieldUnitDimension);
    }

    [Fact]
    public async Task No_reference_ids_means_no_cross_module_calls()
    {
        await CreateAsync(new CreateRecipeViewModel { Title = "Cake" });

        Assert.Equal(0, _references.VocabularyLookups);
        Assert.Equal(0, _references.UnitLookups);
    }

    // ---- Instruction reference checks ----

    [Fact]
    public async Task An_unknown_technique_in_a_step_is_refused()
    {
        _references.UsableVocabulary = false;

        var result = await CreateAsync(new CreateRecipeViewModel
        {
            Title = "Cake",
            Instructions = [new RecipeInstructionGroupInputViewModel
            {
                Steps = [new RecipeInstructionStepInputViewModel { Text = "Sear.", TechniqueId = Guid.NewGuid() }],
            }],
        });

        Assert.False(result.Result.Succeeded);
        Assert.Contains("techniqueId", result.Result.Error!.FieldErrors.Keys);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task A_temperature_unit_that_does_not_measure_temperature_is_refused()
    {
        _references.UnitDimension = MeasurementDimension.Count;

        var result = await CreateAsync(new CreateRecipeViewModel
        {
            Title = "Cake",
            Instructions = [new RecipeInstructionGroupInputViewModel
            {
                Steps = [new RecipeInstructionStepInputViewModel { Text = "Bake.", TemperatureValue = 180m, TemperatureUnitId = Guid.NewGuid() }],
            }],
        });

        Assert.False(result.Result.Succeeded);
        Assert.Contains("temperatureUnitId", result.Result.Error!.FieldErrors.Keys);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task A_real_temperature_unit_passes()
    {
        _references.UnitDimension = MeasurementDimension.Temperature;

        var result = await CreateAsync(new CreateRecipeViewModel
        {
            Title = "Cake",
            Instructions = [new RecipeInstructionGroupInputViewModel
            {
                Steps = [new RecipeInstructionStepInputViewModel { Text = "Bake.", TemperatureValue = 180m, TemperatureUnitId = Guid.NewGuid() }],
            }],
        });

        Assert.True(result.Result.Succeeded);
    }

    [Fact]
    public async Task No_instructions_means_no_instruction_reference_lookups()
    {
        await CreateAsync(new CreateRecipeViewModel { Title = "Cake" });

        Assert.Equal(0, _references.VocabularyLookups);
        Assert.Equal(0, _references.UnitLookups);
    }

    // ---- Idempotency ----

    [Fact]
    public async Task A_first_request_runs_and_is_not_a_replay()
    {
        var result = await CreateAsync(new CreateRecipeViewModel { Title = "Cake" }, key: "key-1");

        Assert.True(result.Result.Succeeded);
        Assert.False(result.Replayed);
        Assert.Equal(1, _business.Calls);
    }

    [Fact]
    public async Task A_replay_returns_the_original_outcome_and_creates_no_second_version()
    {
        var first = await CreateAsync(new CreateRecipeViewModel { Title = "Cake" }, key: "key-1");
        var second = await CreateAsync(new CreateRecipeViewModel { Title = "Cake" }, key: "key-1");

        Assert.True(second.Replayed);
        Assert.Equal(first.Result.Value!.RecipeId, second.Result.Value!.RecipeId);
        Assert.Equal(first.Result.Value.VersionId, second.Result.Value.VersionId);

        // The restriction this phase exists under: a replay must not run the operation again, because running
        // it again is what would write version 2.
        Assert.Equal(1, _business.Calls);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_payload_is_a_conflict()
    {
        await CreateAsync(new CreateRecipeViewModel { Title = "Cake" }, key: "key-1");
        var reused = await CreateAsync(new CreateRecipeViewModel { Title = "A different cake" }, key: "key-1");

        Assert.False(reused.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, reused.Result.Error!.Code);
        Assert.Equal(1, _business.Calls);
    }

    [Fact]
    public async Task Requests_without_a_key_each_run()
    {
        await CreateAsync(new CreateRecipeViewModel { Title = "Cake" });
        await CreateAsync(new CreateRecipeViewModel { Title = "Cake" });

        Assert.Equal(2, _business.Calls);
    }

    // ---- The detail read ----

    [Theory]
    [InlineData(WorkspaceRole.Viewer)]
    [InlineData(WorkspaceRole.Contributor)]
    [InlineData(WorkspaceRole.Editor)]
    [InlineData(WorkspaceRole.Owner)]
    public async Task Every_member_may_read_a_recipe(WorkspaceRole role)
    {
        var recipeId = Guid.NewGuid();
        _business.Detail = Detail(recipeId);

        var result = await Facade(role).GetDetailAsync(recipeId, TestContext.Current.CancellationToken);

        // Including a Viewer: reading is the floor of membership, and there is no role beneath it to refuse.
        Assert.True(result.Succeeded);
        Assert.Equal(recipeId, result.Value!.Id);
    }

    [Fact]
    public async Task The_recipe_asked_for_is_the_one_looked_up()
    {
        var recipeId = Guid.NewGuid();
        _business.Detail = Detail(recipeId);

        await Facade().GetDetailAsync(recipeId, TestContext.Current.CancellationToken);

        Assert.Equal(recipeId, _business.RequestedRecipeId);
    }

    [Fact]
    public async Task An_unreadable_recipe_is_reported_as_not_found()
    {
        _business.Detail = null;

        var result = await Facade().GetDetailAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task A_read_consults_no_other_module_and_no_idempotency_record()
    {
        _business.Detail = Detail(Guid.NewGuid());

        await Facade().GetDetailAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        // A read verifies no references and commits nothing, so neither the vocabulary and measurement
        // modules nor the idempotency executor has any part in it.
        Assert.Equal(0, _references.VocabularyLookups);
        Assert.Equal(0, _references.UnitLookups);
        Assert.Equal(0, _idempotency.Calls);
    }

    /// <summary>
    /// A detail model built through the real mapper rather than by hand.
    /// </summary>
    /// <remarks>
    /// Every property of <see cref="RecipeDetailServiceModel"/> is <c>required</c> — deliberately, so the
    /// published schema marks them present — which makes a hand-written literal here twenty-eight lines of
    /// noise for a test that only cares that the value passes through unaltered.
    /// </remarks>
    // ---- The update seam ----

    private const string Token = "AQIDBAUGBwg=";

    private static PatchField<T> Set<T>(T value) => PatchField<T>.Submitted(value);

    private static UpdateRecipeViewModel Edit() => new() { ExpectedConcurrencyToken = Token };

    private Task<IdempotentOutcome<RecipeDetailServiceModel>> UpdateAsync(
        UpdateRecipeViewModel model,
        WorkspaceRole role = WorkspaceRole.Contributor,
        string? key = null,
        Guid? recipeId = null)
    {
        var id = recipeId ?? Guid.NewGuid();
        _business.Detail = Detail(id);

        return Facade(role).UpdateAsync(UserId, id, model, key, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(WorkspaceRole.Contributor)]
    [InlineData(WorkspaceRole.Editor)]
    [InlineData(WorkspaceRole.Owner)]
    public async Task Contributor_and_above_may_edit(WorkspaceRole role)
    {
        // The same bar as creating one. Editing a recipe someone else started is a collaboration, not an
        // escalation; who made each change is recorded on the version it wrote.
        var result = await UpdateAsync(Edit() with { Title = Set<string?>("Cake") }, role);

        Assert.True(result.Result.Succeeded);
    }

    [Fact]
    public async Task A_viewer_may_not_edit()
    {
        var result = await UpdateAsync(Edit() with { Title = Set<string?>("Cake") }, WorkspaceRole.Viewer);

        Assert.False(result.Result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeForbidden, result.Result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task An_edit_without_a_token_never_reaches_business()
    {
        var result = await UpdateAsync(new UpdateRecipeViewModel { Title = Set<string?>("Cake") });

        Assert.Contains("expectedConcurrencyToken", result.Result.Error!.FieldErrors.Keys);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task Only_submitted_references_are_checked()
    {
        _references.UsableVocabulary = false;

        var result = await UpdateAsync(Edit() with { Title = Set<string?>("Cake") });

        // Re-verifying a reference the edit never mentioned would refuse an edit because of a cuisine
        // retired after the recipe was written — an answer the creator can do nothing about.
        Assert.True(result.Result.Succeeded);
        Assert.Equal(0, _references.VocabularyLookups);
    }

    [Fact]
    public async Task A_submitted_reference_that_is_no_longer_offered_is_refused()
    {
        _references.UsableVocabulary = false;

        var result = await UpdateAsync(Edit() with { CuisineId = Set<Guid?>(Guid.NewGuid()) });

        Assert.False(result.Result.Succeeded);
        Assert.Contains("cuisineId", result.Result.Error!.FieldErrors.Keys);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task Clearing_a_reference_looks_nothing_up()
    {
        var result = await UpdateAsync(Edit() with { CuisineId = Set<Guid?>(null) });

        // There is no id to verify when the caller is removing one.
        Assert.True(result.Result.Succeeded);
        Assert.Equal(0, _references.VocabularyLookups);
    }

    [Fact]
    public async Task A_submitted_unit_dimension_is_handed_to_business()
    {
        _references.UnitDimension = MeasurementDimension.Mass;

        await UpdateAsync(Edit() with { YieldUnitId = Set<Guid?>(Guid.NewGuid()) });

        Assert.Equal(MeasurementDimension.Mass, _business.YieldUnitDimension);
    }

    [Fact]
    public async Task An_unsubmitted_unit_hands_down_no_dimension()
    {
        _references.UnitDimension = MeasurementDimension.Mass;

        await UpdateAsync(Edit() with { Title = Set<string?>("Cake") });

        // Null here means "the unit is not changing", and Business uses the dimension already stored.
        Assert.Null(_business.YieldUnitDimension);
        Assert.Equal(0, _references.UnitLookups);
    }

    [Fact]
    public async Task The_canonical_edit_reaches_business_with_absence_intact()
    {
        await UpdateAsync(Edit() with { Headnote = Set<string?>(null) });

        var patch = _business.Patch!;
        Assert.True(patch.Headnote.IsSubmitted);
        Assert.Null(patch.Headnote.Value);
        Assert.False(patch.Title.IsSubmitted);
    }

    [Fact]
    public async Task A_replayed_edit_returns_the_original_outcome_and_writes_no_second_version()
    {
        var recipeId = Guid.NewGuid();
        var edit = Edit() with { Title = Set<string?>("Cake") };

        var first = await UpdateAsync(edit, key: "key-1", recipeId: recipeId);
        var second = await UpdateAsync(edit, key: "key-1", recipeId: recipeId);

        // Without this, a client whose successful edit timed out retries and is told the token is stale —
        // indistinguishable from a real collaborator conflict.
        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(1, _business.Calls);
    }

    [Fact]
    public async Task The_same_key_for_a_different_edit_is_reported_as_reuse()
    {
        var recipeId = Guid.NewGuid();

        await UpdateAsync(Edit() with { Title = Set<string?>("Cake") }, key: "key-1", recipeId: recipeId);
        var second = await UpdateAsync(Edit() with { Title = Set<string?>("Tart") }, key: "key-1", recipeId: recipeId);

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    [Fact]
    public async Task The_same_edit_against_a_different_state_is_not_a_replay()
    {
        var recipeId = Guid.NewGuid();

        await UpdateAsync(Edit() with { Title = Set<string?>("Cake") }, key: "key-1", recipeId: recipeId);
        var second = await UpdateAsync(
            new UpdateRecipeViewModel { ExpectedConcurrencyToken = "CAcGBQQDAgE=", Title = Set<string?>("Cake") },
            key: "key-1",
            recipeId: recipeId);

        // A caller who re-read the recipe and reused their key is asking for something else: the same words
        // applied to a recipe that has moved.
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    [Fact]
    public async Task An_edit_without_a_key_simply_runs()
    {
        var result = await UpdateAsync(Edit() with { Title = Set<string?>("Cake") });

        Assert.True(result.Result.Succeeded);
        Assert.False(result.Replayed);
        Assert.Equal(1, _business.Calls);
    }

    private static RecipeDetailServiceModel Detail(Guid recipeId)
    {
        var recipe = new Domain.Modules.Recipes.Data.Entities.Recipe
        {
            Id = recipeId,
            Title = "Olive oil cake",
            RowVersion = [1, 2, 3, 4, 5, 6, 7, 8],
        };

        return RecipeDetailMapper.ToDetail(new TaggedRecipe(new CompleteRecipe(recipe, null), []));
    }

    // ---- Search ----

    /// <summary>
    /// The workspace and the caller's membership are taken from the resolved context, never from the request —
    /// which has no field for either. The workspace goes into the cursor's scope so a cursor cannot cross
    /// workspaces; the membership is what <c>mine</c> filters on.
    /// </summary>
    [Fact]
    public async Task The_workspace_and_the_membership_come_from_the_resolved_context()
    {
        var workspaceId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        var workspace = new StubWorkspaceContext(WorkspaceRole.Viewer, workspaceId, membershipId);

        var result = await Facade(workspace).SearchAsync(
            new RecipeSearchViewModel(Mine: true), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal([membershipId], _business.SearchCriteria!.Filters.CreatorMembershipIds);

        // The scope the criteria carries is the one built for this workspace, so a cursor minted from it cannot
        // be replayed against another.
        Assert.Equal(
            RecipeSearchScope.Build(
                workspaceId, RecipeSearchSort.RecentlyUpdated, _business.SearchCriteria.Filters),
            _business.SearchCriteria.Scope);
    }

    /// <summary>
    /// A search has no role gate: Viewer is the lowest role there is, so a resolved context already is the
    /// authorization. The route's own policy is what keeps an outsider out.
    /// </summary>
    [Fact]
    public async Task Every_member_including_a_viewer_may_search()
    {
        foreach (var role in (WorkspaceRole[])[WorkspaceRole.Viewer, WorkspaceRole.Contributor, WorkspaceRole.Owner])
        {
            var result = await Facade(role).SearchAsync(
                new RecipeSearchViewModel(), TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded);
        }
    }

    [Fact]
    public async Task A_query_that_fails_shape_validation_is_refused_with_the_search_code()
    {
        var result = await Facade().SearchAsync(
            new RecipeSearchViewModel(Search: new string('a', 200)), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.SearchInvalidRequest, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    /// <summary>
    /// A cursor that cannot be decoded gets the cursor's own code rather than the generic one, so a paging client
    /// knows to start the list again instead of retrying a cursor that will never be accepted.
    /// </summary>
    [Fact]
    public async Task An_undecodable_cursor_is_refused_with_the_cursor_code()
    {
        var result = await Facade().SearchAsync(
            new RecipeSearchViewModel(Cursor: "%%%"), TestContext.Current.CancellationToken);

        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task An_unparseable_filter_is_refused_before_business_is_called()
    {
        var result = await Facade().SearchAsync(
            new RecipeSearchViewModel(Status: "Published"), TestContext.Current.CancellationToken);

        Assert.Equal(RecipeErrorCodes.SearchInvalidRequest, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task A_valid_query_reaches_business_once_and_returns_its_page()
    {
        _business.Page = new RecipeSearchPageServiceModel([], "next", 12);

        var result = await Facade().SearchAsync(
            new RecipeSearchViewModel(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("next", result.Value!.NextCursor);
        Assert.Equal(12, result.Value.TotalCount);
        Assert.Equal(1, _business.Calls);
    }

    // ---- Version history ----

    /// <summary>
    /// The workspace comes from the resolved context and the recipe from the route; the request has a field for
    /// neither. Both reach the criteria through the scope, which is what a cursor is bound to.
    /// </summary>
    [Fact]
    public async Task A_history_query_is_scoped_to_the_resolved_workspace_and_the_routes_recipe()
    {
        var workspaceId = Guid.NewGuid();
        var recipeId = Guid.NewGuid();
        var workspace = new StubWorkspaceContext(WorkspaceRole.Viewer, workspaceId, Guid.NewGuid());

        var result = await Facade(workspace).GetVersionHistoryAsync(
            recipeId, new RecipeVersionHistoryViewModel(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(recipeId, _business.HistoryCriteria!.RecipeId);
        Assert.Equal(
            RecipeVersionHistoryScope.Build(workspaceId, recipeId), _business.HistoryCriteria.Scope);
    }

    /// <summary>
    /// No role gate: Viewer is the lowest role there is, so a resolved context already is the authorization.
    /// Someone who may read a recipe may read how it came to say what it says.
    /// </summary>
    [Fact]
    public async Task Every_member_including_a_viewer_may_read_a_history()
    {
        foreach (var role in (WorkspaceRole[])[WorkspaceRole.Viewer, WorkspaceRole.Contributor, WorkspaceRole.Owner])
        {
            var result = await Facade(role).GetVersionHistoryAsync(
                Guid.NewGuid(), new RecipeVersionHistoryViewModel(), TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded);
        }
    }

    [Fact]
    public async Task An_undecodable_history_cursor_is_refused_before_business_is_called()
    {
        var result = await Facade().GetVersionHistoryAsync(
            Guid.NewGuid(), new RecipeVersionHistoryViewModel(Cursor: "%%%"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, result.Error!.Code);
        Assert.Contains("cursor", result.Error.FieldErrors.Keys);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task A_history_cursor_from_another_recipe_is_refused_before_business_is_called()
    {
        var workspace = new StubWorkspaceContext(WorkspaceRole.Viewer, Guid.NewGuid(), Guid.NewGuid());
        var cursor = CreatorPantry.Domain.Managers.Paging.ReferenceCursor.Encode(
            "3", Guid.NewGuid().ToString("D"), RecipeVersionHistoryScope.Build(Guid.NewGuid(), Guid.NewGuid()));

        var result = await Facade(workspace).GetVersionHistoryAsync(
            Guid.NewGuid(), new RecipeVersionHistoryViewModel(Cursor: cursor), TestContext.Current.CancellationToken);

        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    /// <summary>
    /// An out-of-range page size is clamped rather than refused, so a client cannot fail a read by asking for
    /// too much — the rule every paged route in the platform follows.
    /// </summary>
    [Fact]
    public async Task An_oversized_history_page_reaches_business_clamped()
    {
        var result = await Facade().GetVersionHistoryAsync(
            Guid.NewGuid(), new RecipeVersionHistoryViewModel(Limit: 10_000), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(
            CreatorPantry.Domain.Managers.Paging.ReferencePolicy.MaxPageSize, _business.HistoryCriteria!.Limit);
    }

    [Fact]
    public async Task A_valid_history_query_reaches_business_once_and_returns_its_page()
    {
        _business.HistoryPage = new CursorPageServiceModel<RecipeVersionHistoryServiceModel>([], "next");

        var result = await Facade().GetVersionHistoryAsync(
            Guid.NewGuid(), new RecipeVersionHistoryViewModel(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("next", result.Value!.NextCursor);
        Assert.Equal(1, _business.Calls);
    }

    // ---- Version comparison ----

    [Theory]
    [InlineData(null, 2)]
    [InlineData(1, null)]
    [InlineData(0, 2)]
    [InlineData(1, -3)]
    public async Task A_query_that_does_not_name_two_version_numbers_is_refused_before_business_is_called(
        int? from, int? to)
    {
        var result = await Facade().CompareVersionsAsync(
            Guid.NewGuid(), new RecipeVersionComparisonViewModel(from, to), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.ComparisonInvalidRequest, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task A_valid_comparison_query_reaches_business_once_with_the_route_recipe_and_both_numbers()
    {
        var recipeId = Guid.NewGuid();

        var result = await Facade().CompareVersionsAsync(
            recipeId, new RecipeVersionComparisonViewModel(3, 7), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(recipeId, _business.RequestedRecipeId);
        Assert.Equal((3, 7), _business.ComparedVersions);
        Assert.Equal(1, _business.Calls);
    }

    /// <summary>
    /// Accepted, not refused: the answer is a comparison with nothing in it, which a client that preselects
    /// the same version on both sides should render rather than handle as an error.
    /// </summary>
    [Fact]
    public async Task Comparing_a_version_with_itself_is_accepted()
    {
        var result = await Facade().CompareVersionsAsync(
            Guid.NewGuid(), new RecipeVersionComparisonViewModel(2, 2), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// No role gate: Viewer is the lowest role there is, so a resolved context already is the authorization.
    /// Someone who may read a recipe may read what changed between two of its versions.
    /// </summary>
    [Fact]
    public async Task Every_member_including_a_viewer_may_compare_versions()
    {
        foreach (var role in (WorkspaceRole[])[WorkspaceRole.Viewer, WorkspaceRole.Contributor, WorkspaceRole.Owner])
        {
            var result = await Facade(role).CompareVersionsAsync(
                Guid.NewGuid(), new RecipeVersionComparisonViewModel(1, 2), TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded);
        }
    }

    // ---- Naming the author of a version ----

    private static RecipeVersionHistoryServiceModel HistoryEntry(Guid id, int versionNumber) => new()
    {
        Id = id,
        VersionNumber = versionNumber,
        Source = RecipeVersionSource.CreatorEdit,
        Readiness = RecipeVersionReadiness.Draft,
        Reason = null,
        CreatedAt = DateTimeOffset.UnixEpoch,
        CreatedByName = null,
        ParentVersionId = null,
        RestoredFromVersionId = null,
        AiProposalId = null,
    };

    private Task<OperationResult<CursorPageServiceModel<RecipeVersionHistoryServiceModel>>> HistoryAsync() =>
        Facade().GetVersionHistoryAsync(
            Guid.NewGuid(), new RecipeVersionHistoryViewModel(), TestContext.Current.CancellationToken);

    /// <summary>
    /// The enrichment this facade performs after Business rather than before it. Business cannot name a
    /// membership — that needs Tenancy and Auth beyond it — so it hands the ids up and the facade spends
    /// them.
    /// </summary>
    [Fact]
    public async Task A_history_entry_is_published_with_its_authors_display_name()
    {
        var versionId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();

        _business.HistoryPage = new CursorPageServiceModel<RecipeVersionHistoryServiceModel>(
            [HistoryEntry(versionId, 1)], null);
        _business.HistoryAuthors[versionId] = membershipId;
        _workspaces.Names[membershipId] = "Sam Okafor";

        var result = await HistoryAsync();

        Assert.Equal("Sam Okafor", Assert.Single(result.Value!.Items).CreatedByName);
    }

    /// <summary>
    /// One lookup for the page, not one per row. The distinct memberships of a page of history are usually
    /// one or two people, and asking per entry would turn a read into N.
    /// </summary>
    [Fact]
    public async Task One_lookup_names_every_entry_on_the_page()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var membershipId = Guid.NewGuid();

        _business.HistoryPage = new CursorPageServiceModel<RecipeVersionHistoryServiceModel>(
            [HistoryEntry(first, 2), HistoryEntry(second, 1)], null);
        _business.HistoryAuthors[first] = membershipId;
        _business.HistoryAuthors[second] = membershipId;
        _workspaces.Names[membershipId] = "Sam Okafor";

        var result = await HistoryAsync();

        Assert.Equal(1, _workspaces.Lookups);
        Assert.Single(_workspaces.RequestedMembershipIds!);
        Assert.All(result.Value!.Items, entry => Assert.Equal("Sam Okafor", entry.CreatedByName));
    }

    /// <summary>
    /// A version outlives the membership that wrote it by design — authorship is recorded so it survives
    /// someone leaving. The honest answer is an unnamed author, not a failed read of the creator's history.
    /// </summary>
    [Fact]
    public async Task An_author_who_has_left_leaves_the_name_null_rather_than_failing()
    {
        var versionId = Guid.NewGuid();

        _business.HistoryPage = new CursorPageServiceModel<RecipeVersionHistoryServiceModel>(
            [HistoryEntry(versionId, 1)], null);
        _business.HistoryAuthors[versionId] = Guid.NewGuid();

        var result = await HistoryAsync();

        Assert.True(result.Succeeded);
        Assert.Null(Assert.Single(result.Value!.Items).CreatedByName);
    }

    /// <summary>An empty page asks nobody anything.</summary>
    [Fact]
    public async Task An_empty_page_looks_up_no_names()
    {
        _business.HistoryPage = new CursorPageServiceModel<RecipeVersionHistoryServiceModel>([], null);

        Assert.True((await HistoryAsync()).Succeeded);
        Assert.Equal(0, _workspaces.Lookups);
    }

    // ---- Duplicating a recipe ----

    private Task<IdempotentOutcome<CreatedRecipeServiceModel>> DuplicateAsync(
        DuplicateRecipeViewModel model,
        WorkspaceRole role = WorkspaceRole.Contributor,
        string? key = null,
        Guid? recipeId = null) =>
        Facade(role).DuplicateAsync(
            UserId, recipeId ?? Guid.NewGuid(), model, key, TestContext.Current.CancellationToken);

    private static DuplicateRecipeViewModel Copy() => new() { Title = "Olive oil and rosemary cake" };

    /// <summary>
    /// Contributor, the same bar as creating one from nothing — and deliberately not the Editor bar a
    /// restore carries, because copying takes nothing away from anybody.
    /// </summary>
    [Theory]
    [InlineData(WorkspaceRole.Contributor)]
    [InlineData(WorkspaceRole.Editor)]
    [InlineData(WorkspaceRole.Owner)]
    public async Task Contributor_and_above_may_duplicate(WorkspaceRole role)
    {
        var result = await DuplicateAsync(Copy(), role);

        Assert.True(result.Result.Succeeded);
    }

    [Fact]
    public async Task A_viewer_may_not_duplicate()
    {
        var result = await DuplicateAsync(Copy(), WorkspaceRole.Viewer);

        Assert.False(result.Result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeForbidden, result.Result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task A_duplicate_without_a_title_is_refused_before_business()
    {
        var result = await DuplicateAsync(new DuplicateRecipeViewModel { Title = "   " });

        Assert.False(result.Result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeInvalidRequest, result.Result.Error!.Code);
        Assert.Contains("title", result.Result.Error.FieldErrors.Keys);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task A_source_version_below_one_is_refused_before_business()
    {
        var result = await DuplicateAsync(Copy() with { SourceVersionNumber = 0 });

        Assert.False(result.Result.Succeeded);
        Assert.Contains("sourceVersionNumber", result.Result.Error!.FieldErrors.Keys);
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task A_title_is_canonicalized_before_it_is_handed_down()
    {
        await DuplicateAsync(Copy() with { Title = "  Olive oil and rosemary cake  " });

        Assert.Equal("Olive oil and rosemary cake", _business.Duplicate!.Title);
    }

    /// <summary>
    /// "Copy whatever is current" survives canonicalization as <c>null</c> rather than being resolved here —
    /// resolving it needs a read, and the facade has none.
    /// </summary>
    [Fact]
    public async Task An_unspecified_source_version_stays_unspecified()
    {
        await DuplicateAsync(Copy());

        Assert.Null(_business.Duplicate!.SourceVersionNumber);
    }

    /// <summary>
    /// A duplicate names no vocabulary, so the facade consults neither module — unlike a create or an edit.
    /// Every id the copy writes comes from the source recipe's own archive.
    /// </summary>
    [Fact]
    public async Task A_duplicate_verifies_no_references()
    {
        await DuplicateAsync(Copy());

        Assert.Equal(0, _references.VocabularyLookups);
        Assert.Equal(0, _references.UnitLookups);
    }

    [Fact]
    public async Task A_replayed_duplicate_returns_the_original_outcome_and_runs_once()
    {
        var recipeId = Guid.NewGuid();

        var first = await DuplicateAsync(Copy(), key: "copy-key-1", recipeId: recipeId);
        var second = await DuplicateAsync(Copy(), key: "copy-key-1", recipeId: recipeId);

        Assert.True(first.Result.Succeeded);
        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(1, _business.Calls);

        // The same recipe both times: a replay must not hand back a second copy's id.
        Assert.Equal(first.Result.Value!.RecipeId, second.Result.Value!.RecipeId);
    }

    /// <summary>
    /// Two copies under different names are two recipes the creator meant to have, so one key cannot cover
    /// both — a replay that returned the first would quietly lose the second.
    /// </summary>
    [Fact]
    public async Task One_key_cannot_replay_across_titles()
    {
        var recipeId = Guid.NewGuid();

        await DuplicateAsync(Copy(), key: "copy-key-2", recipeId: recipeId);
        var second = await DuplicateAsync(
            Copy() with { Title = "A different copy" }, key: "copy-key-2", recipeId: recipeId);

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    [Fact]
    public async Task One_key_cannot_replay_across_source_versions()
    {
        var recipeId = Guid.NewGuid();

        await DuplicateAsync(Copy() with { SourceVersionNumber = 1 }, key: "copy-key-3", recipeId: recipeId);
        var second = await DuplicateAsync(
            Copy() with { SourceVersionNumber = 2 }, key: "copy-key-3", recipeId: recipeId);

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    /// <summary>
    /// And "current" is not the same request as the number that happens to be current, because the first says
    /// the creator did not care which version it was.
    /// </summary>
    [Fact]
    public async Task One_key_cannot_replay_current_as_a_named_version()
    {
        var recipeId = Guid.NewGuid();

        await DuplicateAsync(Copy(), key: "copy-key-4", recipeId: recipeId);
        var second = await DuplicateAsync(
            Copy() with { SourceVersionNumber = 1 }, key: "copy-key-4", recipeId: recipeId);

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    [Fact]
    public async Task One_key_cannot_replay_across_source_recipes()
    {
        await DuplicateAsync(Copy(), key: "copy-key-5", recipeId: Guid.NewGuid());
        var second = await DuplicateAsync(Copy(), key: "copy-key-5", recipeId: Guid.NewGuid());

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    // ---- Restoring a version ----

    private Task<IdempotentOutcome<RecipeDetailServiceModel>> RestoreAsync(
        RestoreRecipeVersionViewModel model,
        WorkspaceRole role = WorkspaceRole.Editor,
        string? key = null,
        Guid? recipeId = null,
        int versionNumber = 2)
    {
        var id = recipeId ?? Guid.NewGuid();
        _business.Detail = Detail(id);

        return Facade(role).RestoreVersionAsync(
            UserId, id, versionNumber, model, key, TestContext.Current.CancellationToken);
    }

    private static RestoreRecipeVersionViewModel Restore() =>
        new() { ExpectedConcurrencyToken = Token };

    /// <summary>
    /// Editor and above, one step higher than editing: a restore discards every change made since the chosen
    /// version, in one request and without naming them.
    /// </summary>
    [Theory]
    [InlineData(WorkspaceRole.Editor)]
    [InlineData(WorkspaceRole.Owner)]
    public async Task Editor_and_above_may_restore(WorkspaceRole role)
    {
        var result = await RestoreAsync(Restore(), role);

        Assert.True(result.Result.Succeeded);
    }

    [Theory]
    [InlineData(WorkspaceRole.Viewer)]
    [InlineData(WorkspaceRole.Contributor)]
    public async Task Below_editor_may_not_restore(WorkspaceRole role)
    {
        var result = await RestoreAsync(Restore(), role);

        Assert.False(result.Result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeForbidden, result.Result.Error!.Code);

        // Refused before anything below is reached, so a caller who may not do this learns that rather than
        // which of their ids is wrong.
        Assert.Equal(0, _business.Calls);
    }

    [Fact]
    public async Task A_restore_without_a_token_is_refused_before_business()
    {
        var result = await RestoreAsync(new RestoreRecipeVersionViewModel());

        Assert.False(result.Result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeInvalidRequest, result.Result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    /// <summary>
    /// The version comes from the route and is handed down as it arrived. Nothing canonicalizes it, because
    /// there is nothing to reduce — but it must reach Business, or every restore would target the same
    /// version.
    /// </summary>
    [Fact]
    public async Task The_route_version_reaches_business()
    {
        await RestoreAsync(Restore(), versionNumber: 7);

        Assert.Equal(7, _business.RestoredVersionNumber);
    }

    [Fact]
    public async Task A_reason_is_canonicalized_before_it_is_handed_down()
    {
        await RestoreAsync(Restore() with { Reason = "  Tuesday's edit broke it.  " });

        Assert.Equal("Tuesday's edit broke it.", _business.Restore!.Reason);
    }

    /// <summary>
    /// A restore names no vocabulary, so the facade consults neither module — unlike a create or an edit,
    /// where every submitted id is verified before Business is reached. The ids a restore writes come from
    /// the recipe's own archive.
    /// </summary>
    [Fact]
    public async Task A_restore_verifies_no_references()
    {
        await RestoreAsync(Restore());

        Assert.Equal(0, _references.VocabularyLookups);
        Assert.Equal(0, _references.UnitLookups);
    }

    [Fact]
    public async Task A_replayed_restore_returns_the_original_outcome_and_runs_once()
    {
        var recipeId = Guid.NewGuid();

        var first = await RestoreAsync(Restore(), key: "key-1", recipeId: recipeId);
        var second = await RestoreAsync(Restore(), key: "key-1", recipeId: recipeId);

        Assert.True(first.Result.Succeeded);
        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(1, _business.Calls);
    }

    /// <summary>
    /// The same key against a different version is a different request, and is reported as key reuse rather
    /// than answered with the earlier restore.
    /// </summary>
    [Fact]
    public async Task One_key_cannot_replay_across_versions()
    {
        var recipeId = Guid.NewGuid();

        await RestoreAsync(Restore(), key: "key-1", recipeId: recipeId, versionNumber: 2);
        var second = await RestoreAsync(Restore(), key: "key-1", recipeId: recipeId, versionNumber: 5);

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    [Fact]
    public async Task One_key_cannot_replay_across_recipes()
    {
        await RestoreAsync(Restore(), key: "key-1", recipeId: Guid.NewGuid());
        var second = await RestoreAsync(Restore(), key: "key-1", recipeId: Guid.NewGuid());

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    /// <summary>
    /// A caller who re-read the recipe and re-sent the same key is asking for a different restore — one
    /// composed against a different state — and is told the key was reused.
    /// </summary>
    [Fact]
    public async Task One_key_cannot_replay_across_states()
    {
        var recipeId = Guid.NewGuid();

        await RestoreAsync(Restore(), key: "key-1", recipeId: recipeId);
        var second = await RestoreAsync(
            Restore() with { ExpectedConcurrencyToken = "CAcGBQQDAgE=" }, key: "key-1", recipeId: recipeId);

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    /// <summary>
    /// A restore with no key runs unprotected rather than being refused — the same choice the create and
    /// edit routes make, and safe here because a repeat is either a stale-token conflict or a no-op.
    /// </summary>
    [Fact]
    public async Task A_restore_without_a_key_runs_once_unprotected()
    {
        var result = await RestoreAsync(Restore());

        Assert.True(result.Result.Succeeded);
        Assert.False(result.Replayed);
        Assert.Equal(1, _business.Calls);
    }

    private sealed class RecordingRecipeBusiness : IRecipeBusiness
    {
        public int Calls { get; private set; }

        public MeasurementDimension? YieldUnitDimension { get; private set; }

        public CanonicalCreateRecipe? Input { get; private set; }

        public Guid? RequestedRecipeId { get; private set; }

        /// <summary>What <see cref="GetDetailAsync"/> answers with; null makes it report not-found.</summary>
        public RecipeDetailServiceModel? Detail { get; set; }

        public Task<OperationResult<RecipeDetailServiceModel>> GetDetailAsync(
            Guid recipeId, CancellationToken cancellationToken)
        {
            Calls++;
            RequestedRecipeId = recipeId;

            return Task.FromResult(Detail is null
                ? OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
                    RecipeErrorCodes.RecipeNotFound, "That recipe could not be found.", new Dictionary<string, string[]>()))
                : OperationResult<RecipeDetailServiceModel>.Success(Detail));
        }

        /// <summary>What <see cref="SearchAsync"/> answers with.</summary>
        public RecipeSearchPageServiceModel Page { get; set; } = new([], null, null);

        /// <summary>The criteria the facade translated, so a test can assert what it built.</summary>
        public RecipeSearchCriteria? SearchCriteria { get; private set; }

        public Task<RecipeSearchPageServiceModel> SearchAsync(
            RecipeSearchCriteria criteria,
            CancellationToken cancellationToken)
        {
            Calls++;
            SearchCriteria = criteria;

            return Task.FromResult(Page);
        }

        /// <summary>What <see cref="GetVersionHistoryAsync"/> answers with.</summary>
        public CursorPageServiceModel<RecipeVersionHistoryServiceModel> HistoryPage { get; set; } = new([], null);

        /// <summary>The criteria the facade translated, so a test can assert what it built.</summary>
        public RecipeVersionHistoryCriteria? HistoryCriteria { get; private set; }

        /// <summary>The membership behind each entry, which the facade exchanges for a display name.</summary>
        public Dictionary<Guid, Guid> HistoryAuthors { get; } = [];

        public Task<OperationResult<RecipeVersionHistoryPageResult>> GetVersionHistoryAsync(
            RecipeVersionHistoryCriteria criteria,
            CancellationToken cancellationToken)
        {
            Calls++;
            HistoryCriteria = criteria;

            return Task.FromResult(OperationResult<RecipeVersionHistoryPageResult>.Success(
                new RecipeVersionHistoryPageResult(HistoryPage, HistoryAuthors)));
        }

        /// <summary>The version numbers the facade handed down, so a test can assert what it passed.</summary>
        public (int From, int To)? ComparedVersions { get; private set; }

        public Task<OperationResult<RecipeVersionComparisonServiceModel>> CompareVersionsAsync(
            Guid recipeId,
            int fromVersionNumber,
            int toVersionNumber,
            CancellationToken cancellationToken)
        {
            Calls++;
            RequestedRecipeId = recipeId;
            ComparedVersions = (fromVersionNumber, toVersionNumber);

            return Task.FromResult(OperationResult<RecipeVersionComparisonServiceModel>.Success(
                new RecipeVersionComparisonServiceModel
                {
                    From = Side(fromVersionNumber),
                    To = Side(toVersionNumber),
                    Comparison = RecipeComparer.Compare(EmptyDocument, EmptyDocument),
                }));
        }

        private static readonly RecipeSnapshotDocument EmptyDocument = new()
        {
            SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            Recipe = new RecipeSnapshotHeader { Title = "Olive oil cake" },
        };

        private static RecipeVersionComparisonSideServiceModel Side(int versionNumber) => new()
        {
            VersionId = Guid.NewGuid(),
            VersionNumber = versionNumber,
            Source = RecipeVersionSource.CreatorEdit,
            Readiness = RecipeVersionReadiness.Draft,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

        public Task<OperationResult<CreatedRecipeServiceModel>> CreateAsync(
            CanonicalCreateRecipe input, MeasurementDimension? yieldUnitDimension, CancellationToken cancellationToken)
        {
            Calls++;
            YieldUnitDimension = yieldUnitDimension;
            Input = input;

            return Task.FromResult(OperationResult<CreatedRecipeServiceModel>.Success(new CreatedRecipeServiceModel(
                Guid.NewGuid(), input.Title, input.Status, Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch)));
        }

        /// <summary>The patch the facade handed down, reduced to its meaning.</summary>
        public CanonicalRecipePatch? Patch { get; private set; }

        public Task<OperationResult<RecipeDetailServiceModel>> UpdateAsync(
            Guid recipeId,
            CanonicalRecipePatch patch,
            MeasurementDimension? submittedYieldUnitDimension,
            CancellationToken cancellationToken)
        {
            Calls++;
            RequestedRecipeId = recipeId;
            YieldUnitDimension = submittedYieldUnitDimension;
            Patch = patch;

            return Task.FromResult(Detail is null
                ? OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
                    RecipeErrorCodes.RecipeNotFound, "That recipe could not be found.", new Dictionary<string, string[]>()))
                : OperationResult<RecipeDetailServiceModel>.Success(Detail));
        }

        /// <summary>The lifecycle command the facade called, and the token it passed down.</summary>
        public (string Command, string ActorUserId, string? Token)? Lifecycle { get; private set; }

        public Task<OperationResult<RecipeDetailServiceModel>> ArchiveAsync(
            Guid recipeId,
            string actorUserId,
            string? expectedConcurrencyToken,
            CancellationToken cancellationToken) =>
            LifecycleAsync("archive", recipeId, actorUserId, expectedConcurrencyToken);

        public Task<OperationResult<RecipeDetailServiceModel>> UnarchiveAsync(
            Guid recipeId,
            string actorUserId,
            string? expectedConcurrencyToken,
            CancellationToken cancellationToken) =>
            LifecycleAsync("unarchive", recipeId, actorUserId, expectedConcurrencyToken);

        private Task<OperationResult<RecipeDetailServiceModel>> LifecycleAsync(
            string command, Guid recipeId, string actorUserId, string? expectedConcurrencyToken)
        {
            Calls++;
            RequestedRecipeId = recipeId;
            Lifecycle = (command, actorUserId, expectedConcurrencyToken);

            return Task.FromResult(Detail is null
                ? OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
                    RecipeErrorCodes.RecipeNotFound, "That recipe could not be found.", new Dictionary<string, string[]>()))
                : OperationResult<RecipeDetailServiceModel>.Success(Detail));
        }

        /// <summary>The duplicate the facade handed down, reduced to its meaning.</summary>
        public CanonicalDuplicateRecipe? Duplicate { get; private set; }

        public Task<OperationResult<CreatedRecipeServiceModel>> DuplicateAsync(
            Guid recipeId,
            CanonicalDuplicateRecipe request,
            CancellationToken cancellationToken)
        {
            Calls++;
            RequestedRecipeId = recipeId;
            Duplicate = request;

            return Task.FromResult(OperationResult<CreatedRecipeServiceModel>.Success(new CreatedRecipeServiceModel(
                Guid.NewGuid(), request.Title, RecipeStatus.Draft, Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch)));
        }

        /// <summary>The restore the facade handed down, reduced to its meaning.</summary>
        public CanonicalRestoreRecipeVersion? Restore { get; private set; }

        /// <summary>The version number the facade passed from the route.</summary>
        public int? RestoredVersionNumber { get; private set; }

        public Task<OperationResult<RecipeDetailServiceModel>> RestoreVersionAsync(
            Guid recipeId,
            int versionNumber,
            CanonicalRestoreRecipeVersion request,
            CancellationToken cancellationToken)
        {
            Calls++;
            RequestedRecipeId = recipeId;
            RestoredVersionNumber = versionNumber;
            Restore = request;

            return Task.FromResult(Detail is null
                ? OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
                    RecipeErrorCodes.RecipeNotFound, "That recipe could not be found.", new Dictionary<string, string[]>()))
                : OperationResult<RecipeDetailServiceModel>.Success(Detail));
        }
    }

    /// <summary>
    /// Stands in for Tenancy, which is the only place a membership can be turned into a person's name.
    /// </summary>
    /// <remarks>
    /// Only the one method is implemented; the rest throw, because a facade that reached for them would be
    /// reaching past what the history read needs and the test should say so loudly rather than pass.
    /// </remarks>
    private sealed class StubWorkspaceDirectory : IWorkspaceFacade
    {
        /// <summary>The display name per membership this stub knows about.</summary>
        public Dictionary<Guid, string> Names { get; } = [];

        public int Lookups { get; private set; }

        /// <summary>The ids the last lookup asked for, so a test can assert it asked once for the distinct set.</summary>
        public IReadOnlyCollection<Guid>? RequestedMembershipIds { get; private set; }

        public Task<IReadOnlyDictionary<Guid, string>> FindMemberDisplayNamesAsync(
            IReadOnlyCollection<Guid> membershipIds,
            CancellationToken cancellationToken)
        {
            Lookups++;
            RequestedMembershipIds = membershipIds;

            // Filtered, because the point of the real read is that a membership it cannot see comes back
            // missing rather than as an error.
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                Names.Where(pair => membershipIds.Contains(pair.Key)).ToDictionary());
        }

        public Task<IReadOnlyList<MyWorkspaceMembershipServiceModel>> GetMyMembershipsAsync(
            string userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A recipe read has no business listing the caller's workspaces.");

        public Task<OperationResult<WorkspaceServiceModel>> CreateAsync(
            string userId, CreateWorkspaceViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A recipe read has no business creating a workspace.");

        public Task<WorkspaceServiceModel> GetCurrentAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A recipe read has no business reading the workspace itself.");

        public Task<OperationResult<WorkspaceServiceModel>> RenameCurrentAsync(
            UpdateWorkspaceViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException("A recipe read has no business renaming a workspace.");
    }

    /// <summary>Stands in for the two modules the facade consults, and counts that it consulted them.</summary>
    private sealed class StubReferenceModules : IVocabularyFacade, IMeasurementFacade
    {
        public bool UsableVocabulary { get; set; } = true;

        public MeasurementDimension? UnitDimension { get; set; } = MeasurementDimension.Count;

        public int VocabularyLookups { get; private set; }

        public int UnitLookups { get; private set; }

        public Task<bool> IsUsableAsync(VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken)
        {
            VocabularyLookups++;
            return Task.FromResult(UsableVocabulary);
        }

        public Task<MeasurementDimension?> FindUsableUnitDimensionAsync(Guid unitId, CancellationToken cancellationToken)
        {
            UnitLookups++;
            return Task.FromResult(UnitDimension);
        }

        // The list and resolve endpoints are not exercised here; the facade under test never calls them.
        public Task<OperationResult<Domain.Managers.Paging.CursorPageServiceModel<Domain.Modules.Measurement.Managers.MeasurementUnitServiceModel>>> ListUnitsAsync(
            Domain.Modules.Measurement.Managers.MeasurementUnitQueryViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<Domain.Modules.Measurement.Managers.UnitMatchResult>>> ResolveCandidatesAsync(
            IReadOnlyList<string> candidateTexts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<Domain.Managers.Paging.CursorPageServiceModel<Domain.Modules.Vocabulary.Managers.ReferenceEntryServiceModel>>> ListFoodCategoriesAsync(
            Domain.Modules.Vocabulary.Managers.ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<Domain.Managers.Paging.CursorPageServiceModel<Domain.Modules.Vocabulary.Managers.ReferenceEntryServiceModel>>> ListCuisinesAsync(
            Domain.Modules.Vocabulary.Managers.ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<Domain.Managers.Paging.CursorPageServiceModel<Domain.Modules.Vocabulary.Managers.ReferenceEntryServiceModel>>> ListCoursesAsync(
            Domain.Modules.Vocabulary.Managers.ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<Domain.Managers.Paging.CursorPageServiceModel<Domain.Modules.Vocabulary.Managers.CookingTechniqueServiceModel>>> ListTechniquesAsync(
            Domain.Modules.Vocabulary.Managers.ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<Domain.Managers.Paging.CursorPageServiceModel<Domain.Modules.Vocabulary.Managers.ReferenceEntryServiceModel>>> ListEquipmentTypesAsync(
            Domain.Modules.Vocabulary.Managers.ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<Domain.Managers.Paging.CursorPageServiceModel<Domain.Modules.Vocabulary.Managers.DescribedReferenceEntryServiceModel>>> ListDietaryProfilesAsync(
            Domain.Modules.Vocabulary.Managers.ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<Domain.Managers.Paging.CursorPageServiceModel<Domain.Modules.Vocabulary.Managers.DescribedReferenceEntryServiceModel>>> ListAllergensAsync(
            Domain.Modules.Vocabulary.Managers.ReferenceQueryViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// An in-memory stand-in for the idempotency executor: same key and same fingerprint replays, same key
    /// with a different fingerprint conflicts, no key always runs.
    /// </summary>
    private sealed class ReplayingIdempotency : IIdempotentCommandExecutor
    {
        private readonly Dictionary<string, (string Fingerprint, object Value)> _committed = new(StringComparer.Ordinal);

        public int Calls { get; private set; }

        public async Task<IdempotentOutcome<T>> ExecuteAsync<T>(
            IdempotentCommand command,
            Func<CancellationToken, Task<OperationResult<T>>> operation,
            CancellationToken cancellationToken)
        {
            Calls++;

            if (command.Key is null)
            {
                return new IdempotentOutcome<T>(await operation(cancellationToken), Replayed: false);
            }

            var fingerprint = System.Text.Json.JsonSerializer.Serialize(command.Fingerprint);

            if (_committed.TryGetValue(command.Key, out var existing))
            {
                return existing.Fingerprint == fingerprint
                    ? new IdempotentOutcome<T>(OperationResult<T>.Success((T)existing.Value), Replayed: true)
                    : new IdempotentOutcome<T>(
                        OperationResult<T>.Failure(new OperationError(
                            IdempotencyPolicy.KeyReusedCode,
                            "That idempotency key was already used with a different request.",
                            new Dictionary<string, string[]>())),
                        Replayed: false);
            }

            var result = await operation(cancellationToken);

            if (result.Succeeded)
            {
                _committed[command.Key] = (fingerprint, result.Value!);
            }

            return new IdempotentOutcome<T>(result, Replayed: false);
        }
    }

    /// <summary>
    /// The ids default to fresh values, because most tests only need them to exist. A test that asserts what the
    /// facade took <em>from the context</em> rather than from the request has to be able to name them.
    /// </summary>
    private sealed class StubWorkspaceContext(
        WorkspaceRole role,
        Guid? workspaceId = null,
        Guid? membershipId = null) : IWorkspaceContext
    {
        public bool IsResolved => true;

        public Guid WorkspaceId { get; } = workspaceId ?? Guid.NewGuid();

        public string WorkspaceSlug => "workspace-a";

        public Guid MembershipId { get; } = membershipId ?? Guid.NewGuid();

        public WorkspaceRole Role { get; } = role;
    }
}
