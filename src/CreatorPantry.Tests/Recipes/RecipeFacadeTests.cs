using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
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
    private readonly ReplayingIdempotency _idempotency = new();

    private IRecipeFacade Facade(WorkspaceRole role = WorkspaceRole.Contributor) =>
        new ServiceCollection()
            .AddSingleton<FluentValidation.IValidator<CreateRecipeViewModel>>(new CreateRecipeViewModelValidator())
            .AddSingleton<FluentValidation.IValidator<UpdateRecipeViewModel>>(new UpdateRecipeViewModelValidator())
            .AddSingleton<IRecipeBusiness>(_business)
            .AddSingleton<IWorkspaceContext>(new StubWorkspaceContext(role))
            .AddSingleton<IVocabularyFacade>(_references)
            .AddSingleton<IMeasurementFacade>(_references)
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

        // The list endpoints are not exercised here; the facade under test never calls them.
        public Task<OperationResult<Domain.Managers.Paging.CursorPageServiceModel<Domain.Modules.Measurement.Managers.MeasurementUnitServiceModel>>> ListUnitsAsync(
            Domain.Modules.Measurement.Managers.MeasurementUnitQueryViewModel model, CancellationToken cancellationToken) =>
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

    private sealed class StubWorkspaceContext(WorkspaceRole role) : IWorkspaceContext
    {
        public bool IsResolved => true;

        public Guid WorkspaceId { get; } = Guid.NewGuid();

        public string WorkspaceSlug => "workspace-a";

        public Guid MembershipId { get; } = Guid.NewGuid();

        public WorkspaceRole Role { get; } = role;
    }
}
