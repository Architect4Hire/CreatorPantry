using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// A real SQL Server for the repository tests, in a throwaway container, with the schema built by
/// <c>Database.Migrate()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>SQL Server rather than SQLite, deliberately.</strong> Everything else in this folder runs on
/// in-memory SQLite, which is right for asserting EF configuration but cannot answer the questions a
/// repository raises: whether a split query returns a correctly assembled aggregate, whether filtered and
/// composite indexes actually serve the reads, or whether the generated SQL is valid at all on the engine
/// this product targets.
/// </para>
/// <para>
/// <strong><c>Migrate()</c> rather than <c>EnsureCreated()</c>, equally deliberately.</strong> Every other
/// test here builds its schema from the model, which means the suite structurally cannot notice a migration
/// that disagrees with the model — it would pass against a schema no deployment ever produces. These tests
/// run the migrations, so the thing production applies is the thing under test.
/// </para>
/// <para>
/// The image tag is pinned to the one the AppHost pins. A test that passes against a different major version
/// than the app runs on is evidence about the wrong database.
/// </para>
/// </remarks>
public sealed class SqlServerRecipeFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-22.04").Build();

    private ServiceProvider? _provider;

    public static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    public static Guid WorkspaceA { get; } = Guid.NewGuid();

    public static Guid WorkspaceB { get; } = Guid.NewGuid();

    public static Guid GramId { get; } = Guid.NewGuid();

    public static Guid FlourId { get; } = Guid.NewGuid();

    public static Guid CuisineId { get; } = Guid.NewGuid();

    /// <summary>A second cuisine, so a cuisine filter can be shown to exclude as well as include.</summary>
    public static Guid OtherCuisineId { get; } = Guid.NewGuid();

    public static Guid CourseId { get; } = Guid.NewGuid();

    public static Guid TagIdA { get; } = Guid.NewGuid();

    public static Guid TagIdB { get; } = Guid.NewGuid();

    /// <summary>
    /// A second tag in workspace A, so a tag filter can be shown to narrow within one workspace rather than
    /// only to separate the two workspaces from each other.
    /// </summary>
    public static Guid SecondTagIdA { get; } = Guid.NewGuid();

    /// <summary>
    /// Two stable authors. <see cref="NewRecipe"/> assigns random membership ids, which is right for the tests
    /// that only need them to exist; a filter on the author needs to be able to name one.
    /// </summary>
    public static Guid AuthorOne { get; } = Guid.NewGuid();

    /// <inheritdoc cref="AuthorOne"/>
    public static Guid AuthorTwo { get; } = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = BuildProvider(interceptor: null);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        await db.Database.MigrateAsync();
        await SeedReferenceDataAsync(db);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>A scope with the workspace context resolved, the way a request arrives.</summary>
    /// <param name="interceptor">
    /// An optional save interceptor, for the tests that need a write to fail. It gets its own provider and
    /// therefore its own DbContext configuration, so an injected failure cannot leak into any other test.
    /// </param>
    public AsyncServiceScope ScopeFor(Guid workspaceId, IInterceptor? interceptor = null)
    {
        var provider = interceptor is null ? _provider! : BuildProvider(interceptor);
        var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner);

        return scope;
    }

    private ServiceProvider BuildProvider(IInterceptor? interceptor) =>
        new ServiceCollection()
            .AddTenancy()
            .AddRecipesModule()

            // The recipe DataLayer records audit entries for lifecycle commands, and the writer needs a clock.
            // Both are shared-kernel services the module depends on rather than registers, exactly as it
            // depends on CreatorPantryDbContext — Program.cs wires them the same way.
            .AddApplicationTime()
            .AddAudit()
            .AddDbContext<CreatorPantryDbContext>(options =>
            {
                options.UseSqlServer(_container.GetConnectionString());

                if (interceptor is not null)
                {
                    options.AddInterceptors(interceptor);
                }
            })
            .BuildServiceProvider(validateScopes: true);

    public static CreatorPantryDbContext Db(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

    /// <summary>
    /// A recipe with several children in each collection, and their sort orders inserted out of order so
    /// that a read returning them in insertion order rather than sorted order is visible.
    /// </summary>
    public static Recipe NewRecipe(string title, Guid tagId)
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
            CuisineId = CuisineId,
        };

        foreach (var groupOrder in (int[])[1, 0])
        {
            var group = new RecipeIngredientGroup
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                Title = $"Group {groupOrder}",
                SortOrder = groupOrder,
            };

            foreach (var lineOrder in (int[])[2, 0, 1])
            {
                group.Ingredients.Add(new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    RecipeId = recipe.Id,
                    RecipeIngredientGroupId = group.Id,
                    SortOrder = lineOrder,
                    DisplayText = $"line {groupOrder}.{lineOrder}",
                    Quantity = 100m + lineOrder,
                    MeasurementUnitId = GramId,
                    MeasurementUnitDimension = MeasurementDimension.Mass,
                    IngredientId = FlourId,
                    MatchStatus = IngredientMatchStatus.Matched,
                });
            }

            recipe.IngredientGroups.Add(group);
        }

        foreach (var groupOrder in (int[])[1, 0])
        {
            var group = new RecipeInstructionGroup
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                Title = $"Steps {groupOrder}",
                SortOrder = groupOrder,
            };

            foreach (var stepOrder in (int[])[1, 0])
            {
                group.Steps.Add(new RecipeInstructionStep
                {
                    Id = Guid.NewGuid(),
                    RecipeId = recipe.Id,
                    RecipeInstructionGroupId = group.Id,
                    SortOrder = stepOrder,
                    Text = $"step {groupOrder}.{stepOrder}",
                });
            }

            recipe.InstructionGroups.Add(group);
        }

        foreach (var order in (int[])[1, 0])
        {
            recipe.Equipment.Add(new RecipeEquipment
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                SortOrder = order,
                DisplayText = $"equipment {order}",
            });

            recipe.AssetLinks.Add(new RecipeAssetLink
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                SortOrder = order,
                MediaAssetId = Guid.NewGuid(),
                Role = order == 0 ? RecipeAssetRole.Hero : RecipeAssetRole.Gallery,
            });
        }

        recipe.Tags.Add(new RecipeTag { RecipeId = recipe.Id, WorkspaceTagId = tagId });

        return recipe;
    }

    private static async Task SeedReferenceDataAsync(CreatorPantryDbContext db)
    {
        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "Workspace A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "Workspace B", Slug = "workspace-b", CreatedAt = Now });

        db.MeasurementUnits.Add(new MeasurementUnit
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
        });

        db.Ingredients.Add(new Ingredient
        {
            Id = FlourId,
            CanonicalName = "all-purpose flour",
            NormalizedName = "all purpose flour",
            SearchText = "all purpose flour",
        });

        db.Cuisines.AddRange(
            new Cuisine { Id = CuisineId, Code = "italian", DisplayName = "Italian" },
            new Cuisine { Id = OtherCuisineId, Code = "thai", DisplayName = "Thai" });

        db.Courses.Add(new Course { Id = CourseId, Code = "dessert", DisplayName = "Dessert" });

        db.WorkspaceTags.AddRange(
            // The same tag name in both workspaces, so isolation cannot pass by the two being distinguishable.
            new WorkspaceTag { Id = TagIdA, WorkspaceId = WorkspaceA, Name = "Weeknight", NormalizedName = "weeknight", CreatedAt = Now },
            new WorkspaceTag { Id = TagIdB, WorkspaceId = WorkspaceB, Name = "Weeknight", NormalizedName = "weeknight", CreatedAt = Now },
            new WorkspaceTag { Id = SecondTagIdA, WorkspaceId = WorkspaceA, Name = "Freezer", NormalizedName = "freezer", CreatedAt = Now });

        await db.SaveChangesAsync();
    }
}
