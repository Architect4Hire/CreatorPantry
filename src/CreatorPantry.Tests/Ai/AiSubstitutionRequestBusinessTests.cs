using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AIREC-004's request seam over a real SQLite-backed data layer and the real recipe module: the enabled
/// gate, the recipe, pinned-version and selected-ingredient checks, what is stored, and two-workspace
/// isolation.
/// </summary>
public sealed class AiSubstitutionRequestBusinessTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AiSubstitutionRequestBusinessTests()
    {
        _connection.Open();

        var tasks = new AiTaskOptions();
        tasks.Enabled.Add(AiTaskCatalog.IngredientSubstitution);

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddIngredientModule()
            .AddRecipesModule()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(tasks)
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddAiUsageModule()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<IAiSubstitutionRequestBusiness, AiSubstitutionRequestBusiness>()
            .AddIdempotency(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Now });

        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- the enabled gate --------------------------------------------------------------------------------

    [Fact]
    public async Task A_disabled_task_is_refused_before_anything_is_queued()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var business = new AiSubstitutionRequestBusiness(
            scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>(),
            scope.ServiceProvider.GetRequiredService<IRecipeFacade>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
            new AiTaskOptions(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        var outcome = await business.RequestAsync(
            recipe.RecipeId, Request(recipe), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiSubstitutionRequestErrors.TaskNotEnabled, outcome.Result.Error!.Code);
    }

    // ---- what is queued ----------------------------------------------------------------------------------

    /// <summary>
    /// The scope is the server's, not the request's. <c>Advisory</c> permits no target at all, which is what
    /// makes "this proposes no change to the recipe" a property of the stored row rather than of whichever
    /// handler happens to run it.
    /// </summary>
    [Fact]
    public async Task A_request_queues_an_advisory_operation_against_the_pinned_version()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        var status = outcome.Result.Value!;

        Assert.Equal(AiTaskType.IngredientSubstitution, status.TaskType);
        Assert.Equal(AiOperationScope.Advisory, status.Scope);
        Assert.Equal(recipe.VersionId, status.SourceVersionId);
        Assert.Equal(AiOperationStatus.Requested, status.Status);
    }

    /// <summary>An advisory scope permits nothing, which is the whole of its enforcement.</summary>
    [Fact]
    public void The_advisory_scope_allows_no_target_at_all()
    {
        Assert.Empty(AiPolicy.AllowedTargets(AiOperationScope.Advisory));

        Assert.All(
            Enum.GetValues<AiChangeTargetKind>(),
            target => Assert.Empty(AiPolicy.AllowedFields(AiOperationScope.Advisory, target)));
    }

    [Fact]
    public async Task The_selected_ingredient_and_reason_are_stored_in_the_vocabulary_the_handler_reads()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceA, recipe.RecipeId, Request(recipe, reason: "I am out of buttermilk"));

        var inputs = await LoadInputsAsync(outcome.Result.Value!.AiProposalRequestId);

        Assert.Equal(recipe.IngredientId.ToString(), inputs[AiSubstitutionInputs.IngredientId]);
        Assert.Equal("I am out of buttermilk", inputs[AiSubstitutionInputs.Reason]);
    }

    [Fact]
    public async Task A_request_with_no_reason_stores_none()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe, reason: null));
        var inputs = await LoadInputsAsync(outcome.Result.Value!.AiProposalRequestId);

        Assert.DoesNotContain(AiSubstitutionInputs.Reason, inputs.Keys, StringComparer.Ordinal);
    }

    // ---- the recipe, the version and the line ------------------------------------------------------------

    [Fact]
    public async Task An_unknown_recipe_is_not_found()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, Guid.NewGuid(), Request(recipe));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiSubstitutionRequestErrors.RecipeNotFound, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// The stale-version case. Advice about an ingredient is advice about what it does in a particular
    /// method, so a version the recipe has moved past describes a dish that is no longer there.
    /// </summary>
    [Fact]
    public async Task A_version_that_is_not_the_current_one_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        await EditAsync(WorkspaceA, recipe.RecipeId);

        var outcome = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiSubstitutionRequestErrors.SourceVersionInvalid, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// An ingredient that is not in the pinned version, refused before a provider budget is spent on an
    /// answer about a line nobody could point at.
    /// </summary>
    [Fact]
    public async Task An_ingredient_that_is_not_in_the_version_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceA, recipe.RecipeId, Request(recipe, ingredientId: Guid.NewGuid()));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiSubstitutionRequestErrors.IngredientInvalid, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// A line from another recipe in the same workspace is refused by the same check. The creator can read
    /// both recipes, so nothing is being hidden — the answer would simply have been about the wrong dish.
    /// </summary>
    [Fact]
    public async Task An_ingredient_belonging_to_another_recipe_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var other = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceA, recipe.RecipeId, Request(recipe, ingredientId: other.IngredientId));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiSubstitutionRequestErrors.IngredientInvalid, outcome.Result.Error!.Code);
    }

    // ---- idempotency -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_key_and_the_same_request_replays()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var first = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe), "key-1");
        var second = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe), "key-1");

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Result.Value!.AiProposalRequestId, second.Result.Value!.AiProposalRequestId);
    }

    /// <summary>
    /// The same key naming a different ingredient is a different question, and answering it with the first
    /// answer would be worse than refusing.
    /// </summary>
    [Fact]
    public async Task The_same_key_with_a_different_ingredient_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe), "key-1");

        var second = await RequestAsync(
            WorkspaceA,
            recipe.RecipeId,
            Request(recipe, ingredientId: recipe.SecondIngredientId),
            "key-1");

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    [Fact]
    public async Task The_same_key_with_a_different_reason_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe, reason: "Out of it"), "key-1");

        var second = await RequestAsync(
            WorkspaceA, recipe.RecipeId, Request(recipe, reason: "Reader is allergic"), "key-1");

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    // ---- the status read ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_queued_request_can_be_read_back()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var queued = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));

        var read = await GetAsync(WorkspaceA, recipe.RecipeId, queued.Result.Value!.AiProposalRequestId);

        Assert.True(read.Succeeded, read.Error?.Message);
        Assert.Equal(AiOperationStatus.Requested, read.Value!.Status);
    }

    [Fact]
    public async Task A_request_read_under_the_wrong_recipe_is_not_found()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var other = await SeedRecipeAsync(WorkspaceA);
        var queued = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));

        var read = await GetAsync(WorkspaceA, other.RecipeId, queued.Result.Value!.AiProposalRequestId);

        Assert.False(read.Succeeded);
        Assert.Equal(AiSubstitutionRequestErrors.RequestNotFound, read.Error!.Code);
    }

    // ---- workspace isolation -----------------------------------------------------------------------------

    /// <summary>
    /// Workspace B naming workspace A's recipe. The query filter makes it invisible, and the seam reports it
    /// exactly as it reports a recipe that never existed.
    /// </summary>
    [Fact]
    public async Task A_recipe_in_another_workspace_yields_no_substitution_advice()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceB, recipe.RecipeId, Request(recipe));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiSubstitutionRequestErrors.RecipeNotFound, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// And naming workspace A's ingredient from inside workspace B's own recipe. The ingredient check reads
    /// B's snapshot, so A's line is simply not there — no part of A's content is consulted to say so.
    /// </summary>
    [Fact]
    public async Task An_ingredient_from_another_workspace_cannot_be_named()
    {
        var mine = await SeedRecipeAsync(WorkspaceB);
        var theirs = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceB,
            mine.RecipeId,
            Request(mine, ingredientId: theirs.IngredientId));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiSubstitutionRequestErrors.IngredientInvalid, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task A_substitution_request_from_one_workspace_is_not_found_from_another()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var queued = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));

        var read = await GetAsync(WorkspaceB, recipe.RecipeId, queued.Result.Value!.AiProposalRequestId);

        Assert.False(read.Succeeded);
        Assert.Equal(AiSubstitutionRequestErrors.RequestNotFound, read.Error!.Code);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private sealed record SeededRecipe(Guid RecipeId, Guid VersionId, Guid IngredientId, Guid SecondIngredientId);

    private static RequestIngredientSubstitutionViewModel Request(
        SeededRecipe recipe, string? reason = "I am out of buttermilk", Guid? ingredientId = null) =>
        new()
        {
            SourceVersionId = recipe.VersionId,
            IngredientId = ingredientId ?? recipe.IngredientId,
            Reason = reason,
        };

    private async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid workspaceId, Guid recipeId, RequestIngredientSubstitutionViewModel model, string key = "key-1")
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiSubstitutionRequestBusiness>()
            .RequestAsync(recipeId, model, key, TestContext.Current.CancellationToken);
    }

    private async Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid workspaceId, Guid recipeId, Guid requestId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiSubstitutionRequestBusiness>()
            .GetAsync(recipeId, requestId, TestContext.Current.CancellationToken);
    }

    /// <summary>A recipe with two ingredient lines, so a request can name the wrong one deliberately.</summary>
    private async Task<SeededRecipe> SeedRecipeAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var recipes = scope.ServiceProvider.GetRequiredService<IRecipeFacade>();

        var created = await recipes.CreateAsync(
            UserId,
            new CreateRecipeViewModel
            {
                Title = "Buttermilk Soda Bread",
                IngredientGroups =
                [
                    new RecipeIngredientGroupInputViewModel
                    {
                        Ingredients =
                        [
                            new RecipeIngredientInputViewModel { DisplayText = "400ml buttermilk" },
                            new RecipeIngredientInputViewModel { DisplayText = "1 tsp bicarbonate of soda" },
                        ],
                    },
                ],
            },
            idempotencyKey: null,
            TestContext.Current.CancellationToken);

        Assert.True(created.Result.Succeeded, created.Result.Error?.Message);

        var snapshot = await recipes.GetSnapshotAsync(
            created.Result.Value!.RecipeId,
            created.Result.Value.VersionId,
            TestContext.Current.CancellationToken);

        var lines = snapshot.Value!.Document.IngredientGroups.SelectMany(group => group.Ingredients).ToList();

        return new SeededRecipe(
            created.Result.Value.RecipeId, created.Result.Value.VersionId, lines[0].Id, lines[1].Id);
    }

    /// <summary>An ordinary creator edit, which is what makes the version a request pinned stale.</summary>
    private async Task EditAsync(Guid workspaceId, Guid recipeId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        var recipes = scope.ServiceProvider.GetRequiredService<IRecipeFacade>();
        var detail = await recipes.GetDetailAsync(recipeId, TestContext.Current.CancellationToken);

        var updated = await recipes.UpdateAsync(
            UserId,
            recipeId,
            new UpdateRecipeViewModel
            {
                ExpectedConcurrencyToken = detail.Value!.ConcurrencyToken,
                Headnote = Domain.Managers.Patching.PatchField<string?>.Submitted("Rewritten by the creator."),
            },
            idempotencyKey: null,
            TestContext.Current.CancellationToken);

        Assert.True(updated.Result.Succeeded, updated.Result.Error?.Message);
    }

    private async Task<Dictionary<string, string>> LoadInputsAsync(Guid operationId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var operation = await db.AiOperations.AsNoTracking()
            .SingleAsync(row => row.Id == operationId, TestContext.Current.CancellationToken);

        return JsonSerializer.Deserialize<Dictionary<string, string>>(operation.TaskInputsJson!)!;
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner, "test-account");

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
