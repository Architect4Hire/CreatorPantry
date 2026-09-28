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
/// AIREC-005's request seam over a real SQLite-backed data layer and the real recipe module: the enabled
/// gate, the recipe and pinned-version checks, the yield pre-check, what is stored on the operation — always
/// <see cref="AiOperationScope.WholeRecipe"/>, never a client choice — and two-workspace isolation.
/// </summary>
public sealed class AiAdaptationRequestBusinessTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AiAdaptationRequestBusinessTests()
    {
        _connection.Open();

        var tasks = new AiTaskOptions();
        tasks.Enabled.Add(AiTaskCatalog.RecipeAdaptation);

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
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<IAiAdaptationRequestBusiness, AiAdaptationRequestBusiness>()
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
        var business = new AiAdaptationRequestBusiness(
            scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>(),
            scope.ServiceProvider.GetRequiredService<IRecipeFacade>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
            new AiTaskOptions(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        var outcome = await business.RequestAsync(
            recipe.RecipeId, Request(recipe.VersionId), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiAdaptationRequestErrors.TaskNotEnabled, outcome.Result.Error!.Code);
    }

    // ---- the recipe and its pinned version ---------------------------------------------------------------

    [Fact]
    public async Task A_request_queues_an_operation_scoped_to_the_whole_recipe()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId));

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        var status = outcome.Result.Value!;

        Assert.Equal(AiTaskType.RecipeAdaptation, status.TaskType);
        Assert.Equal(AiOperationScope.WholeRecipe, status.Scope);
        Assert.Equal(recipe.VersionId, status.SourceVersionId);
        Assert.Equal(AiOperationStatus.Requested, status.Status);
    }

    /// <summary>
    /// The client cannot choose a scope for this task — there is no field to choose one on. Every operation
    /// this business queues is <see cref="AiOperationScope.WholeRecipe"/>, whatever goal was declared.
    /// </summary>
    [Theory]
    [InlineData(AiAdaptationGoal.Dietary)]
    [InlineData(AiAdaptationGoal.Equipment)]
    [InlineData(AiAdaptationGoal.SkillLevel)]
    public async Task Every_goal_is_scoped_to_the_whole_recipe(AiAdaptationGoal goal)
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId, goal: goal, detail: "x"));

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
        Assert.Equal(AiOperationScope.WholeRecipe, outcome.Result.Value!.Scope);
    }

    [Fact]
    public async Task An_unknown_recipe_is_not_found()
    {
        await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceA,
            new SeededRecipe(Guid.NewGuid(), Guid.NewGuid()),
            Request(Guid.NewGuid()));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiAdaptationRequestErrors.RecipeNotFound, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// The stale-version case, for the same reason AIREC-003 and AIREC-004 check it: an adaptation computed
    /// against a version the recipe has moved past would describe content that is no longer there.
    /// </summary>
    [Fact]
    public async Task A_version_that_is_not_the_current_one_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var stale = recipe.VersionId;

        await EditAsync(WorkspaceA, recipe.RecipeId);

        var outcome = await RequestAsync(WorkspaceA, recipe, Request(stale));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiAdaptationRequestErrors.SourceVersionInvalid, outcome.Result.Error!.Code);
    }

    // ---- the yield pre-check -------------------------------------------------------------------------------

    /// <summary>
    /// A yield goal against a recipe with no structured yield cannot resolve a target-yield multiplier, and is
    /// refused before an operation is queued rather than discovered by the worker after a provider budget is
    /// spent computing a diff for an operation that could never have run.
    /// </summary>
    [Fact]
    public async Task A_target_yield_against_an_unstructured_recipe_yield_is_refused_before_queuing()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceA, recipe, Request(recipe.VersionId, goal: AiAdaptationGoal.Yield, targetYieldQuantity: 24m));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiAdaptationRequestErrors.YieldTargetInvalid, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task A_yield_multiplier_needs_no_structured_recipe_yield_to_be_accepted()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceA, recipe, Request(recipe.VersionId, goal: AiAdaptationGoal.Yield, multiplier: 2m));

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);
    }

    // ---- what is stored ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_goal_and_its_detail_are_stored_in_the_vocabulary_the_handler_reads()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceA, recipe, Request(recipe.VersionId, goal: AiAdaptationGoal.Dietary, detail: "gluten-free"));

        var inputs = await LoadInputsAsync(outcome.Result.Value!.AiProposalRequestId);

        Assert.Equal("Dietary", inputs[AiAdaptationInputs.Goal]);
        Assert.Equal("gluten-free", inputs[AiAdaptationInputs.GoalDetail]);
    }

    [Fact]
    public async Task A_yield_targets_multiplier_is_stored_and_no_detail_is_when_none_was_given()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(
            WorkspaceA, recipe, Request(recipe.VersionId, goal: AiAdaptationGoal.Yield, detail: null, multiplier: 3m));

        var inputs = await LoadInputsAsync(outcome.Result.Value!.AiProposalRequestId);

        Assert.Equal("3", inputs[AiAdaptationInputs.TargetMultiplier]);
        Assert.DoesNotContain(AiAdaptationInputs.GoalDetail, inputs.Keys);
    }

    /// <summary>
    /// The scope lives on the operation's own column, which is what the validator reads when the answer comes
    /// back. Storing it in the inputs as well would give a later reader two places to disagree.
    /// </summary>
    [Fact]
    public async Task The_scope_is_not_duplicated_into_the_stored_inputs()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId));
        var inputs = await LoadInputsAsync(outcome.Result.Value!.AiProposalRequestId);

        Assert.DoesNotContain("scope", inputs.Keys, StringComparer.OrdinalIgnoreCase);
    }

    // ---- idempotency -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_key_and_the_same_request_replays()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var first = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId), "key-1");
        var second = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId), "key-1");

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Result.Value!.AiProposalRequestId, second.Result.Value!.AiProposalRequestId);
    }

    [Fact]
    public async Task The_same_key_with_a_different_goal_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId, goal: AiAdaptationGoal.Dietary, detail: "vegan"), "key-1");
        var second = await RequestAsync(
            WorkspaceA, recipe, Request(recipe.VersionId, goal: AiAdaptationGoal.Equipment, detail: "no oven"), "key-1");

        Assert.False(second.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, second.Result.Error!.Code);
    }

    // ---- the status read ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_queued_request_can_be_read_back()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var queued = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId));

        var read = await GetAsync(WorkspaceA, recipe.RecipeId, queued.Result.Value!.AiProposalRequestId);

        Assert.True(read.Succeeded, read.Error?.Message);
        Assert.Equal(AiOperationStatus.Requested, read.Value!.Status);
    }

    /// <summary>A request read through a different recipe's route is not that route's resource.</summary>
    [Fact]
    public async Task A_request_read_under_the_wrong_recipe_is_not_found()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var other = await SeedRecipeAsync(WorkspaceA);
        var queued = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId));

        var read = await GetAsync(WorkspaceA, other.RecipeId, queued.Result.Value!.AiProposalRequestId);

        Assert.False(read.Succeeded);
        Assert.Equal(AiAdaptationRequestErrors.RequestNotFound, read.Error!.Code);
    }

    // ---- workspace isolation -----------------------------------------------------------------------------

    /// <summary>
    /// Workspace B naming workspace A's recipe. The query filter makes it invisible, and the seam reports it
    /// exactly as it reports a recipe that never existed.
    /// </summary>
    [Fact]
    public async Task A_recipe_in_another_workspace_cannot_be_adapted()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceB, recipe, Request(recipe.VersionId));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiAdaptationRequestErrors.RecipeNotFound, outcome.Result.Error!.Code);
    }

    [Fact]
    public async Task An_adaptation_request_from_one_workspace_is_not_found_from_another()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var queued = await RequestAsync(WorkspaceA, recipe, Request(recipe.VersionId));

        var read = await GetAsync(WorkspaceB, recipe.RecipeId, queued.Result.Value!.AiProposalRequestId);

        Assert.False(read.Succeeded);
        Assert.Equal(AiAdaptationRequestErrors.RequestNotFound, read.Error!.Code);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private sealed record SeededRecipe(Guid RecipeId, Guid VersionId);

    private static RequestRecipeAdaptationViewModel Request(
        Guid versionId,
        AiAdaptationGoal goal = AiAdaptationGoal.Dietary,
        string? detail = "gluten-free",
        decimal? multiplier = null,
        decimal? targetYieldQuantity = null) =>
        new()
        {
            SourceVersionId = versionId,
            Goal = goal,
            GoalDetail = detail,
            TargetMultiplier = multiplier,
            TargetYieldQuantity = targetYieldQuantity,
        };

    private async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid workspaceId, SeededRecipe recipe, RequestRecipeAdaptationViewModel model, string key = "key-1")
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiAdaptationRequestBusiness>()
            .RequestAsync(recipe.RecipeId, model, key, TestContext.Current.CancellationToken);
    }

    private async Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid workspaceId, Guid recipeId, Guid requestId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiAdaptationRequestBusiness>()
            .GetAsync(recipeId, requestId, TestContext.Current.CancellationToken);
    }

    private async Task<SeededRecipe> SeedRecipeAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        var created = await scope.ServiceProvider.GetRequiredService<IRecipeFacade>().CreateAsync(
            UserId,
            new CreateRecipeViewModel { Title = "Weeknight Mapo Tofu", Headnote = "As written." },
            idempotencyKey: null,
            TestContext.Current.CancellationToken);

        Assert.True(created.Result.Succeeded, created.Result.Error?.Message);

        return new SeededRecipe(created.Result.Value!.RecipeId, created.Result.Value.VersionId);
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
            WorkspaceRole.Owner);

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
