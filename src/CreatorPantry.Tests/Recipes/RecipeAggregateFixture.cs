using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Two workspaces over one in-memory SQLite database, each able to open a scope resolved to itself. The
/// same pattern <c>WorkspaceConstraintTests</c> uses, with tenancy registered — without it no workspace is
/// ever resolved and every query against a workspace-owned set throws before it can assert anything.
/// </summary>
/// <remarks>
/// SQLite rather than SQL Server because these assertions are about the EF configuration — check
/// constraints, unique and filtered indexes, cascade behaviour, the composite keys — all of which SQLite
/// enforces. What it cannot prove is that SQL Server accepts the resulting DDL; that is the migration's own
/// verification, against a real database.
/// </remarks>
internal sealed class RecipeAggregateFixture : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    public RecipeAggregateFixture()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteRowVersionModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        // Workspaces and reference rows are not workspace-owned, so they seed before anything is resolved.
        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "Workspace A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "Workspace B", Slug = "workspace-b", CreatedAt = Now });

        db.MeasurementUnits.AddRange(
            new MeasurementUnit
            {
                Id = GramId,
                Code = "g",
                DisplayName = "gram",
                PluralName = "grams",
                Abbreviation = "g",
                Dimension = MeasurementDimension.Mass,
                System = MeasurementSystem.Metric,
                BaseUnitFactor = 1m,
                DisplayPrecision = 1,
            },
            new MeasurementUnit
            {
                Id = CelsiusId,
                Code = "c",
                DisplayName = "degree Celsius",
                PluralName = "degrees Celsius",
                Abbreviation = "°C",
                Dimension = MeasurementDimension.Temperature,
                System = MeasurementSystem.Metric,
                // Null by CK_MeasurementUnits_Factor_Dimension: temperature is affine, not multiplicative.
                BaseUnitFactor = null,
                DisplayPrecision = 0,
            });

        db.MeasurementUnits.Add(new MeasurementUnit
        {
            Id = EachId,
            Code = "each",
            DisplayName = "each",
            PluralName = "each",
            Abbreviation = "ea",
            Dimension = MeasurementDimension.Count,
            System = MeasurementSystem.Neutral,
            BaseUnitFactor = 1m,
            DisplayPrecision = 0,
        });

        db.Ingredients.Add(new Ingredient
        {
            Id = FlourId,
            CanonicalName = "all-purpose flour",
            NormalizedName = "all purpose flour",
            SearchText = "all purpose flour",
        });

        // One tag per workspace, deliberately sharing a name. Tags are workspace-owned, so each workspace gets
        // its own row with its own id — a single shared tag would quietly make every isolation assertion
        // about tags meaningless, and the shared name is what proves the unique index is workspace-relative.
        db.WorkspaceTags.AddRange(
            new WorkspaceTag { Id = TagIdA, WorkspaceId = WorkspaceA, Name = "Weeknight", NormalizedName = "weeknight", CreatedAt = Now },
            new WorkspaceTag { Id = TagIdB, WorkspaceId = WorkspaceB, Name = "Weeknight", NormalizedName = "weeknight", CreatedAt = Now });

        // The vocabulary a fully populated recipe references. Global reference data, so it seeds here
        // alongside the units rather than per workspace.
        db.Cuisines.Add(new Cuisine { Id = CuisineId, Code = "italian", DisplayName = "Italian" });
        db.Courses.Add(new Course { Id = CourseId, Code = "dessert", DisplayName = "Dessert" });
        db.CookingTechniques.Add(new CookingTechnique { Id = TechniqueId, Code = "bake", DisplayName = "Bake" });
        db.EquipmentTypes.Add(new EquipmentType { Id = EquipmentTypeId, Code = "cake-pan", DisplayName = "Cake pan" });

        db.SaveChanges();
    }

    public static Guid WorkspaceA { get; } = Guid.NewGuid();

    public static Guid WorkspaceB { get; } = Guid.NewGuid();

    public static Guid GramId { get; } = Guid.NewGuid();

    public static Guid CelsiusId { get; } = Guid.NewGuid();

    public static Guid FlourId { get; } = Guid.NewGuid();

    public static Guid EachId { get; } = Guid.NewGuid();

    public static Guid CuisineId { get; } = Guid.NewGuid();

    public static Guid CourseId { get; } = Guid.NewGuid();

    public static Guid TechniqueId { get; } = Guid.NewGuid();

    public static Guid EquipmentTypeId { get; } = Guid.NewGuid();

    /// <summary>Workspace A's "Weeknight" tag. Workspace B has its own row with the same name.</summary>
    public static Guid TagIdA { get; } = Guid.NewGuid();

    /// <inheritdoc cref="TagIdA"/>
    public static Guid TagIdB { get; } = Guid.NewGuid();

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// A recipe with every archivable field set to a distinct, non-default value.
    /// </summary>
    /// <remarks>
    /// <see cref="NewRecipe"/> leaves most optional fields null, which makes it useless for proving the
    /// snapshot is complete: a mapper that dropped <c>StorageNotes</c> would round-trip null to null and
    /// look correct. Everything here is deliberately non-default so that anything still sitting at its
    /// default after a capture was never written. Adding a field to an entity means adding it here too —
    /// <c>RecipeSnapshotCompletenessTests</c> fails until it is archived, which is the prompt to do so.
    /// </remarks>
    public static Recipe FullyPopulatedRecipe()
    {
        var recipe = NewRecipe("Fully populated cake");

        recipe.Description = "A cake with every field filled in.";
        recipe.Headnote = "The one my grandmother made, every autumn, without writing it down.";
        recipe.Notes = "Halve the sugar if the plums are very ripe.";
        recipe.StorageNotes = "Keeps three days under a cloth. Not a preservation claim.";
        recipe.AttributionText = "Adapted from my grandmother's card.";
        recipe.SourceUrl = "https://example.com/olive-oil-cake";
        recipe.CuisineId = CuisineId;
        recipe.CourseId = CourseId;
        recipe.PrimaryTechniqueId = TechniqueId;
        recipe.PrepTimeMinutes = 20;
        recipe.CookTimeMinutes = 25;
        recipe.RestTimeMinutes = 60;
        recipe.TotalTimeMinutes = 75;
        recipe.YieldText = "makes 12 muffins";
        recipe.YieldQuantity = 12m;
        recipe.YieldUnitId = EachId;
        recipe.YieldUnitDimension = MeasurementDimension.Count;
        recipe.Status = RecipeStatus.Ready;

        var line = recipe.IngredientGroups.Single().Ingredients.Single();
        line.IngredientNameText = "all-purpose flour";
        line.QuantityUpper = 260m;
        line.IsOptional = true;
        line.ScalingBehavior = IngredientScaling.Fixed;

        var step = recipe.InstructionGroups.Single().Steps.Single();
        step.TechniqueId = TechniqueId;
        step.Note = "The centre should spring back, not the edges.";

        var equipment = recipe.Equipment.Single();
        equipment.EquipmentTypeId = EquipmentTypeId;
        equipment.IsOptional = true;
        equipment.Note = "A loaf tin also works.";

        // Server-generated in production, and never at its default on a recipe that has been saved. Set here
        // because the detail read publishes it as an opaque concurrency token, so a completeness test has to
        // be able to tell "mapped" from "left at its default".
        recipe.RowVersion = [1, 2, 3, 4, 5, 6, 7, 8];

        recipe.AssetLinks.Single().Caption = "The cake, still warm.";
        recipe.Tags.Add(new RecipeTag { RecipeId = recipe.Id, WorkspaceTagId = TagIdA });

        // Non-zero positions too, so a SortOrder the mapper dropped is not indistinguishable from the first
        // item's legitimate zero.
        recipe.IngredientGroups.Single().SortOrder = 3;
        line.SortOrder = 3;
        recipe.InstructionGroups.Single().SortOrder = 3;
        step.SortOrder = 3;
        equipment.SortOrder = 3;
        recipe.AssetLinks.Single().SortOrder = 3;

        return recipe;
    }

    /// <summary>A fresh scope with the workspace context already resolved, the way a request arrives.</summary>
    public AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner);

        return scope;
    }

    /// <summary>
    /// Wraps an in-memory recipe as a loaded aggregate, for the mapping tests that build one by hand.
    /// </summary>
    /// <remarks>
    /// The one place in the tests that asserts completeness without a repository having proved it — which is
    /// exactly the claim <see cref="CompleteRecipe"/> makes and cannot enforce. It is acceptable here because
    /// these recipes are constructed whole, in the same file, a few lines above the call. It would not be
    /// acceptable anywhere that loads from a database, and <c>RecipeRepositoryTests</c> covers that path with
    /// an aggregate the repository actually returned.
    /// </remarks>
    public static CompleteRecipe Complete(Recipe recipe) => new(recipe, null);

    /// <summary>A scope no workspace was ever resolved for, the way a background job starts out.</summary>
    public AsyncServiceScope UnresolvedScope() => _provider.CreateAsyncScope();

    public static CreatorPantryDbContext Db(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

    /// <summary>
    /// A complete recipe with one of every child. WorkspaceId is left unset throughout: stamping it is
    /// <see cref="WorkspaceOwnershipInterceptor"/>'s job, and a test that set it by hand would stop proving
    /// that ownership arrives from the resolved context rather than from whoever built the object.
    /// </summary>
    public static Recipe NewRecipe(string title)
    {
        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = title,
            Status = RecipeStatus.Draft,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        var ingredientGroup = new RecipeIngredientGroup
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            Title = "For the dough",
            SortOrder = 0,
        };

        ingredientGroup.Ingredients.Add(new RecipeIngredient
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            RecipeIngredientGroupId = ingredientGroup.Id,
            SortOrder = 0,
            DisplayText = "2 cups (240 g) all-purpose flour, sifted",
            Quantity = 240m,
            MeasurementUnitId = GramId,
            MeasurementUnitDimension = MeasurementDimension.Mass,
            IngredientId = FlourId,
            MatchStatus = IngredientMatchStatus.Matched,
            PreparationNote = "sifted",
        });

        var instructionGroup = new RecipeInstructionGroup
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            Title = "Bake",
            SortOrder = 0,
        };

        instructionGroup.Steps.Add(new RecipeInstructionStep
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            RecipeInstructionGroupId = instructionGroup.Id,
            SortOrder = 0,
            Text = "Bake at 180 °C until the top springs back, about 25 minutes.",
            DurationMinutes = 25,
            TemperatureValue = 180m,
            TemperatureUnitId = CelsiusId,
            TemperatureUnitDimension = MeasurementDimension.Temperature,
        });

        recipe.IngredientGroups.Add(ingredientGroup);
        recipe.InstructionGroups.Add(instructionGroup);

        recipe.Equipment.Add(new RecipeEquipment
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            SortOrder = 0,
            DisplayText = "9-inch round cake pan",
        });

        recipe.AssetLinks.Add(new RecipeAssetLink
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            SortOrder = 0,
            MediaAssetId = Guid.NewGuid(),
            Role = RecipeAssetRole.Hero,
        });

        return recipe;
    }
}
