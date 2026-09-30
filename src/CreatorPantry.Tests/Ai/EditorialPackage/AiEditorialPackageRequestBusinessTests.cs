using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Facade;
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

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// RCPUB-001's request seam over a real SQLite-backed data layer and the real recipe module: the enabled gate,
/// the recipe and pinned-version checks, what is stored, and two-workspace isolation.
/// </summary>
/// <remarks>
/// Mirrors <see cref="AiReviewRequestBusinessTests"/>'s shape, plus what this capability alone has: the approved-only gate,
/// the requested sections, and the brand facts pinned into the operation.
/// </remarks>
public sealed class AiEditorialPackageRequestBusinessTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private const string UserId = "user-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public AiEditorialPackageRequestBusinessTests()
    {
        _connection.Open();

        var tasks = new AiTaskOptions();
        tasks.Enabled.Add(AiTaskCatalog.EditorialPackage);

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddIngredientModule()
            .AddRecipesModule()
            .AddBrandModule()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddSingleton<IClock>(new StoppedClock())
            .AddSingleton(tasks)
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddAiUsageModule()
            .AddApplicationTime()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<IAiRequestQuotaGate, AiRequestQuotaGate>()
            .AddScoped<IAiEditorialPackageRequestBusiness, AiEditorialPackageRequestBusiness>()
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
        var business = new AiEditorialPackageRequestBusiness(
            scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>(),
            scope.ServiceProvider.GetRequiredService<IAiRequestQuotaGate>(),
            scope.ServiceProvider.GetRequiredService<IRecipeFacade>(),
            scope.ServiceProvider.GetRequiredService<IBrandProfileFacade>(),
            scope.ServiceProvider.GetRequiredService<IWorkspaceContext>(),
            new AiTaskOptions(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        var outcome = await business.RequestAsync(
            recipe.RecipeId, Request(recipe), "key-1", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiEditorialPackageRequestErrors.TaskNotEnabled, outcome.Result.Error!.Code);
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

        Assert.Equal(AiTaskType.EditorialPackage, status.TaskType);
        Assert.Equal(AiOperationScope.Advisory, status.Scope);
        Assert.Equal(recipe.VersionId, status.SourceVersionId);
        Assert.Equal(AiOperationStatus.Requested, status.Status);
    }

    // ---- the recipe and the version -----------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_recipe_is_not_found()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, Guid.NewGuid(), Request(recipe));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiEditorialPackageRequestErrors.RecipeNotFound, outcome.Result.Error!.Code);
    }

    /// <summary>The stale-version case. A review is a review of a particular method, not of whatever it becomes next.</summary>
    [Fact]
    public async Task A_version_that_is_not_the_current_one_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        await EditAsync(WorkspaceA, recipe.RecipeId);

        var outcome = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiEditorialPackageRequestErrors.SourceVersionInvalid, outcome.Result.Error!.Code);
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

    /// <summary>The same key naming a different version is a different question.</summary>
    [Fact]
    public async Task The_same_key_with_a_different_version_is_refused()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var other = await SeedRecipeAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe), "key-1");

        var second = await RequestAsync(WorkspaceA, other.RecipeId, Request(other), "key-1");

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
        Assert.Equal(AiEditorialPackageRequestErrors.RequestNotFound, read.Error!.Code);
    }

    // ---- workspace isolation -----------------------------------------------------------------------------

    /// <summary>
    /// Workspace B naming workspace A's recipe. The query filter makes it invisible, and the seam reports it
    /// exactly as it reports a recipe that never existed.
    /// </summary>
    [Fact]
    public async Task A_recipe_in_another_workspace_yields_no_package()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceB, recipe.RecipeId, Request(recipe));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiEditorialPackageRequestErrors.RecipeNotFound, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// The same recipe id existing in both workspaces would be an extraordinary coincidence, so this proves
    /// the isolation the more direct way: workspace B's own recipe, named from workspace B, succeeds, while
    /// the same id read back from workspace A finds nothing — two independent seams agreeing that the recipe
    /// belongs to one workspace and not the other.
    /// </summary>
    [Fact]
    public async Task A_recipe_id_that_exists_in_workspace_b_is_invisible_from_workspace_a()
    {
        var theirs = await SeedRecipeAsync(WorkspaceB);

        var fromOwner = await RequestAsync(WorkspaceB, theirs.RecipeId, Request(theirs));
        Assert.True(fromOwner.Result.Succeeded, fromOwner.Result.Error?.Message);

        var fromOutside = await RequestAsync(WorkspaceA, theirs.RecipeId, Request(theirs));

        Assert.False(fromOutside.Result.Succeeded);
        Assert.Equal(AiEditorialPackageRequestErrors.RecipeNotFound, fromOutside.Result.Error!.Code);
    }

    [Fact]
    public async Task An_editorial_package_request_from_one_workspace_is_not_found_from_another()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        var queued = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));

        var read = await GetAsync(WorkspaceB, recipe.RecipeId, queued.Result.Value!.AiProposalRequestId);

        Assert.False(read.Succeeded);
        Assert.Equal(AiEditorialPackageRequestErrors.RequestNotFound, read.Error!.Code);
    }


    // ---- what this capability alone checks and stores ------------------------------------------------------

    [Fact]
    public async Task A_recipe_that_is_not_approved_is_refused_and_nothing_is_queued()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA, approved: false);

        var outcome = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiEditorialPackageRequestErrors.RecipeNotApproved, outcome.Result.Error!.Code);

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AiOperations.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task No_sections_named_means_all_seven_in_a_stable_order()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));

        var inputs = await StoredInputsAsync(WorkspaceA);

        Assert.Equal(
            "headnote,introduction,tips,substitutions,storageReheating,faq,cta",
            inputs[AiEditorialPackageInputs.Sections]);
    }

    [Fact]
    public async Task Named_sections_are_stored_distinct_and_in_catalogue_order()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        await RequestAsync(WorkspaceA, recipe.RecipeId, new RequestEditorialPackageViewModel
        {
            SourceVersionId = recipe.VersionId,
            Sections = ["CTA", "headnote", "faq", "Headnote"],
        });

        var inputs = await StoredInputsAsync(WorkspaceA);

        Assert.Equal("headnote,faq,cta", inputs[AiEditorialPackageInputs.Sections]);
    }

    [Fact]
    public async Task The_brand_facts_are_pinned_by_value_with_the_revision_they_came_from()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        await SeedBrandAsync(WorkspaceA, name: "Sam's Kitchen", audience: "busy weeknight cooks", locale: "en-GB");

        await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));

        var inputs = await StoredInputsAsync(WorkspaceA);

        Assert.Equal("Sam's Kitchen", inputs[AiEditorialPackageInputs.BrandName]);
        Assert.Equal("busy weeknight cooks", inputs[AiEditorialPackageInputs.Audience]);
        Assert.Equal("en-GB", inputs[AiEditorialPackageInputs.Locale]);
        Assert.Equal("1", inputs[AiEditorialPackageInputs.BrandProfileRevision]);
    }

    [Fact]
    public async Task A_workspace_with_no_brand_profile_still_gets_a_package_and_pins_no_brand()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);

        var outcome = await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);

        var inputs = await StoredInputsAsync(WorkspaceA);
        Assert.False(inputs.ContainsKey(AiEditorialPackageInputs.BrandName));
        Assert.False(inputs.ContainsKey(AiEditorialPackageInputs.BrandProfileRevision));
    }

    [Fact]
    public async Task Another_workspaces_brand_never_reaches_the_operation()
    {
        var recipe = await SeedRecipeAsync(WorkspaceA);
        await SeedBrandAsync(WorkspaceB, name: "B's secret brand", audience: "b only", locale: "fr-FR");

        await RequestAsync(WorkspaceA, recipe.RecipeId, Request(recipe));

        var inputs = await StoredInputsAsync(WorkspaceA);
        Assert.DoesNotContain(inputs.Values, value => value.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.False(inputs.ContainsKey(AiEditorialPackageInputs.BrandName));
    }

    [Fact]
    public async Task The_request_validator_refuses_an_unknown_or_repeated_section_and_an_empty_version()
    {
        var validator = new RequestEditorialPackageViewModelValidator();

        Assert.False((await validator.ValidateAsync(new RequestEditorialPackageViewModel { SourceVersionId = Guid.Empty }, TestContext.Current.CancellationToken)).IsValid);
        Assert.False((await validator.ValidateAsync(new RequestEditorialPackageViewModel { SourceVersionId = Guid.NewGuid(), Sections = ["nonsense"] }, TestContext.Current.CancellationToken)).IsValid);
        Assert.False((await validator.ValidateAsync(new RequestEditorialPackageViewModel { SourceVersionId = Guid.NewGuid(), Sections = ["faq", "FAQ"] }, TestContext.Current.CancellationToken)).IsValid);
        Assert.True((await validator.ValidateAsync(new RequestEditorialPackageViewModel { SourceVersionId = Guid.NewGuid(), Sections = ["faq", "cta"] }, TestContext.Current.CancellationToken)).IsValid);
    }

    private async Task<Dictionary<string, string>> StoredInputsAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        var operation = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AiOperations
            .OrderByDescending(candidate => candidate.RequestedAt)
            .FirstAsync(TestContext.Current.CancellationToken);

        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(operation.TaskInputsJson!)!;
    }

    private async Task SeedBrandAsync(Guid workspaceId, string name, string audience, string locale)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.BrandProfiles.Add(new BrandProfile
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandName = name,
            DefaultAudience = audience,
            Locale = locale,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private sealed record SeededRecipe(Guid RecipeId, Guid VersionId);

    private static RequestEditorialPackageViewModel Request(SeededRecipe recipe) =>
        new() { SourceVersionId = recipe.VersionId };

    private async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        Guid workspaceId, Guid recipeId, RequestEditorialPackageViewModel model, string key = "key-1")
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiEditorialPackageRequestBusiness>()
            .RequestAsync(recipeId, model, key, TestContext.Current.CancellationToken);
    }

    private async Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid workspaceId, Guid recipeId, Guid requestId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IAiEditorialPackageRequestBusiness>()
            .GetAsync(recipeId, requestId, TestContext.Current.CancellationToken);
    }

    private async Task<SeededRecipe> SeedRecipeAsync(Guid workspaceId, bool approved = true)
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

        if (approved)
        {
            // Setup, not behaviour: the editorial state machine has its own tests.
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            (await db.Recipes.SingleAsync(recipe => recipe.Id == created.Result.Value!.RecipeId, TestContext.Current.CancellationToken))
                .Status = RecipeStatus.Approved;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

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
