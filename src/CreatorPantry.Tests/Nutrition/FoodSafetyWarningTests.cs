using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Nutrition;
using CreatorPantry.Domain.Modules.Nutrition.Facade;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Nutrition;

/// <summary>
/// The food-safety warnings end to end (14.5): a real recipe version written through the Recipes module and read
/// back through its facade, the real technique vocabulary and ingredient categories, the host's real evidence
/// coverage, and two-workspace isolation.
/// </summary>
public sealed class FoodSafetyWarningTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2037, 10, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid SousVideId = Guid.NewGuid();
    private static readonly Guid BakeId = Guid.NewGuid();
    private static readonly Guid BeefId = Guid.NewGuid();
    private const string UserId = "user-1";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public FoodSafetyWarningTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddOutbox()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddIngredientModule()
            .AddRecipesModule()
            .AddNutritionModule()
            .AddLogging()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddSingleton<IClock>(new StoppedClock())
            .AddApplicationTime()
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

        var meat = new FoodCategory { Id = Guid.NewGuid(), Code = "meat-and-poultry", DisplayName = "Meat & Poultry" };
        db.FoodCategories.Add(meat);
        db.Ingredients.Add(new Ingredient
        {
            Id = BeefId, CanonicalName = "Beef", NormalizedName = "beef", SearchText = "beef", FoodCategoryId = meat.Id,
        });

        db.CookingTechniques.AddRange(
            new CookingTechnique { Id = SousVideId, Code = "sous-vide", DisplayName = "Sous-Vide", RequiresSafetyCaution = true },
            new CookingTechnique { Id = BakeId, Code = "bake", DisplayName = "Bake" });

        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task A_saved_version_is_read_back_whole_and_every_warning_says_verification_is_required()
    {
        var (recipeId, versionId) = await CreateAsync(WorkspaceA, new CreateRecipeViewModel
        {
            Title = "Sous vide steak",
            StorageNotes = "Keeps for 2 weeks.",
            IngredientGroups = [Lines(("2 steaks", BeefId))],
            Instructions = [Steps(("Cook sous vide at 57 °C for 2 hours, then sear to medium-rare.", SousVideId))],
        });

        // A viewer may read them: warnings are for anyone who can see the recipe.
        var result = Value(await DetectAsync(WorkspaceA, recipeId, versionId, WorkspaceRole.Viewer));

        Assert.Equal((recipeId, versionId, 1), (result.RecipeId, result.RecipeVersionId, result.VersionNumber));
        Assert.Equal(
            [
                (FoodSafetyCategory.SousVide, FoodSafetyConfidence.Detected),
                (FoodSafetyCategory.RawOrUndercookedProtein, FoodSafetyConfidence.Possible),
                (FoodSafetyCategory.UnsupportedSafetyClaim, FoodSafetyConfidence.Possible),
            ],
            result.Warnings.Select(warning => (warning.Category, warning.Confidence)));

        var sousVide = result.Warnings[0];
        Assert.Contains(sousVide.Triggers, trigger => trigger is { Field: FoodSafetyField.StepTechnique, Kind: FoodSafetyTriggerKind.Technique, Matched: "sous-vide" });
        Assert.Contains(sousVide.Triggers, trigger => trigger is { Field: FoodSafetyField.Step, Kind: FoodSafetyTriggerKind.Text });

        // No food-safety source is approved (B-33), and this host's evidence gateway says so.
        Assert.All(result.Warnings, warning =>
        {
            Assert.Equal(FoodSafetyEvidenceStatus.NoApprovedSource, warning.Evidence.Status);
            Assert.True(warning.Evidence.VerificationRequired);
        });
        Assert.Equal(FoodSafetyWarningPolicy.Disclaimer, result.Disclaimer);
    }

    [Fact]
    public async Task Another_workspaces_version_is_not_found_and_each_workspace_reads_only_its_own()
    {
        var (aRecipe, aVersion) = await CreateAsync(WorkspaceA, new CreateRecipeViewModel
        {
            Title = "Classic steak tartare",
            IngredientGroups = [Lines(("200 g beef fillet", BeefId))],
        });
        var (bRecipe, bVersion) = await CreateAsync(WorkspaceB, new CreateRecipeViewModel
        {
            Title = "Weeknight tray bake",
            IngredientGroups = [Lines(("500 g potatoes", null))],
            Instructions = [Steps(("Bake at 200°C for 40 minutes.", BakeId))],
        });

        Assert.Equal(NutritionErrorCodes.EstimateRecipeNotFound, (await DetectAsync(WorkspaceB, aRecipe, aVersion)).Error?.Code);
        Assert.Equal(NutritionErrorCodes.EstimateRecipeNotFound, (await DetectAsync(WorkspaceA, bRecipe, bVersion)).Error?.Code);

        Assert.Equal(FoodSafetyCategory.RawOrUndercookedProtein, Assert.Single(Value(await DetectAsync(WorkspaceA, aRecipe, aVersion)).Warnings).Category);
        Assert.Empty(Value(await DetectAsync(WorkspaceB, bRecipe, bVersion)).Warnings);
    }

    [Fact]
    public async Task A_version_that_is_not_there_is_not_found()
    {
        var (recipeId, _) = await CreateAsync(WorkspaceA, new CreateRecipeViewModel { Title = "Plain rice" });

        Assert.Equal(NutritionErrorCodes.EstimateRecipeNotFound, (await DetectAsync(WorkspaceA, recipeId, Guid.NewGuid())).Error?.Code);
        Assert.Equal(NutritionErrorCodes.EstimateRecipeNotFound, (await DetectAsync(WorkspaceA, Guid.NewGuid(), Guid.NewGuid())).Error?.Code);
    }

    [Fact]
    public async Task An_ingredient_retired_after_the_version_was_saved_still_counts_as_what_the_version_names()
    {
        var (recipeId, versionId) = await CreateAsync(WorkspaceA, new CreateRecipeViewModel
        {
            Title = "Beef carpaccio",
            IngredientGroups = [Lines(("150 g beef fillet", BeefId))],
        });

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var beef = await db.Ingredients.SingleAsync(ingredient => ingredient.Id == BeefId, TestContext.Current.CancellationToken);
            beef.IsActive = false;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var warning = Assert.Single(Value(await DetectAsync(WorkspaceA, recipeId, versionId)).Warnings);

        Assert.Equal((FoodSafetyCategory.RawOrUndercookedProtein, FoodSafetyConfidence.Detected), (warning.Category, warning.Confidence));
        Assert.Contains(warning.Triggers, trigger => trigger is { Kind: FoodSafetyTriggerKind.IngredientCategory, Matched: "150 g beef fillet" });
    }

    // ---- Helpers ----

    private async Task<(Guid RecipeId, Guid VersionId)> CreateAsync(Guid workspaceId, CreateRecipeViewModel model)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId, WorkspaceRole.Owner);

        var created = await scope.ServiceProvider.GetRequiredService<IRecipeFacade>()
            .CreateAsync(UserId, model, idempotencyKey: null, TestContext.Current.CancellationToken);

        Assert.True(created.Result.Succeeded, $"{created.Result.Error?.Code}: {created.Result.Error?.Message}");
        return (created.Result.Value!.RecipeId, created.Result.Value.VersionId);
    }

    private async Task<OperationResult<FoodSafetyWarningsServiceModel>> DetectAsync(
        Guid workspaceId, Guid recipeId, Guid versionId, WorkspaceRole role = WorkspaceRole.Editor)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId, role);

        return await scope.ServiceProvider.GetRequiredService<IFoodSafetyWarningFacade>()
            .DetectAsync(recipeId, versionId, TestContext.Current.CancellationToken);
    }

    private static RecipeIngredientGroupInputViewModel Lines(params (string Text, Guid? IngredientId)[] lines) => new()
    {
        Ingredients = [.. lines.Select(line => new RecipeIngredientInputViewModel { DisplayText = line.Text, IngredientId = line.IngredientId })],
    };

    private static RecipeInstructionGroupInputViewModel Steps(params (string Text, Guid? TechniqueId)[] steps) => new()
    {
        Steps = [.. steps.Select(step => new RecipeInstructionStepInputViewModel { Text = step.Text, TechniqueId = step.TechniqueId })],
    };

    private static T Value<T>(OperationResult<T> result)
    {
        Assert.True(result.Succeeded, $"{result.Error?.Code}: {result.Error?.Message}");
        return result.Value!;
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId, WorkspaceRole role) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            role,
            "test-account");

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
