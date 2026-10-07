using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Vocabulary;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CreatorPantry.Tests.Reference;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// A real SQL Server for the media repository tests, in a throwaway container, with the schema built by
/// <c>Database.Migrate()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A class fixture rather than per-test lifetime.</strong> xUnit builds a new instance of a test class
/// for every test method, so a container owned by the class itself — which is what the <c>*SqlServerTests</c>
/// classes elsewhere do, each having a handful of tests — would start one SQL Server per test. The search
/// tests are a couple of dozen, so the container is shared the way <c>SqlServerRecipeFixture</c> shares one
/// and each test clears the library it is about.
/// </para>
/// <para>
/// <strong><c>Migrate()</c> rather than <c>EnsureCreated()</c>.</strong> The reads under test depend on
/// filtered and composite indexes, which only the migration creates; building the schema from the model would
/// test a database no deployment produces.
/// </para>
/// <para>
/// The image tag is pinned to the one the AppHost pins, for the reason the recipe fixture gives: a test
/// passing against another major version is evidence about the wrong database.
/// </para>
/// </remarks>
public sealed class SqlServerMediaFixture : IAsyncLifetime
{
    public static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public static Guid WorkspaceA { get; } = Guid.NewGuid();

    public static Guid WorkspaceB { get; } = Guid.NewGuid();

    public static Guid CuisineId { get; } = Guid.NewGuid();

    /// <summary>A second cuisine, so a cuisine filter can be shown to exclude as well as include.</summary>
    public static Guid OtherCuisineId { get; } = Guid.NewGuid();

    public static Guid CourseId { get; } = Guid.NewGuid();

    /// <inheritdoc cref="OtherCuisineId"/>
    public static Guid OtherCourseId { get; } = Guid.NewGuid();

    /// <summary>
    /// The same tag name in both workspaces, so isolation cannot pass by the two being distinguishable.
    /// </summary>
    public static Guid TagIdA { get; } = Guid.NewGuid();

    /// <inheritdoc cref="TagIdA"/>
    public static Guid TagIdB { get; } = Guid.NewGuid();

    /// <summary>
    /// A second tag in workspace A, so a tag filter can be shown to narrow within one workspace rather than
    /// only to separate the two workspaces from each other.
    /// </summary>
    public static Guid SecondTagIdA { get; } = Guid.NewGuid();

    /// <summary>The tag belonging to <paramref name="workspaceId"/>, so a helper need not remember which.</summary>
    public static Guid TagIdFor(Guid workspaceId) => workspaceId == WorkspaceB ? TagIdB : TagIdA;

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-22.04").Build();

    private ServiceProvider? _provider;

    public static CreatorPantryDbContext Db(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

    /// <summary>A scope with the workspace context resolved, the way a request arrives.</summary>
    /// <param name="role">
    /// The caller's membership role. Owner by default, because almost every test here is about data rather than
    /// permission; a test of a facade's own role gate passes the role it wants to be refused at, which is the only
    /// way to reach that check — the route policy would otherwise refuse first and the facade would never run.
    /// </param>
    public AsyncServiceScope ScopeFor(Guid workspaceId, WorkspaceRole role = WorkspaceRole.Owner)
    {
        var scope = _provider!.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            role,
            "test-account");

        return scope;
    }

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = new ServiceCollection()
            .AddTenancy()

            // The whole module, so the repository under test is resolved the way the API resolves it rather
            // than constructed by hand. Nothing else in it is ever asked for, so nothing else is built.
            .AddMediaModule()

            // Media's business layer resolves lineage through the Content and Recipes facades (12.9c), and those
            // two pull in the rest: measurement, vocabulary, ingredients, a cache, an outbox, and the AI module's
            // narrow proposal lookup. The set is the one MediaAssetCreateTests arrived at over SQLite — copied
            // rather than rediscovered one DI failure at a time, which is how it was found the first time.
            //
            // Additive for every other test in this folder: they resolve repositories and never reach any of it.
            .AddIdempotency(new ConfigurationBuilder().Build())
            .AddOutbox()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddIngredientModule()
            .AddRecipesModule(new ConfigurationBuilder().Build())
            .AddContentModule()
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiProposalLookupBusiness, AiProposalLookupBusiness>()
            .AddScoped<IAiProposalLookupFacade, AiProposalLookupFacade>()
            .AddOutbox()

            // Shared-kernel services the module depends on rather than registers, exactly as it depends on
            // CreatorPantryDbContext. A data layer taking a logger is the usual cause of an opaque DI failure
            // in a fixture like this one, so logging goes in up front.
            .AddApplicationTime()
            .AddAudit()
            .AddLogging()
            .AddDbContext<CreatorPantryDbContext>(options =>
                options.UseSqlServer(_container.GetConnectionString()))
            .BuildServiceProvider(validateScopes: true);

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

    private static async Task SeedReferenceDataAsync(CreatorPantryDbContext db)
    {
        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "Workspace A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "Workspace B", Slug = "workspace-b", CreatedAt = Now });

        // Platform reference vocabulary: shared, and with no workspace of its own (tenancy.md).
        db.Cuisines.AddRange(
            new Cuisine { Id = CuisineId, Code = "italian", DisplayName = "Italian" },
            new Cuisine { Id = OtherCuisineId, Code = "thai", DisplayName = "Thai" });

        db.Courses.AddRange(
            new Course { Id = CourseId, Code = "dessert", DisplayName = "Dessert" },
            new Course { Id = OtherCourseId, Code = "main", DisplayName = "Main" });

        db.WorkspaceTags.AddRange(
            new WorkspaceTag
            {
                Id = TagIdA, WorkspaceId = WorkspaceA,
                Name = "Weeknight", NormalizedName = "weeknight", CreatedAt = Now,
            },
            new WorkspaceTag
            {
                Id = TagIdB, WorkspaceId = WorkspaceB,
                Name = "Weeknight", NormalizedName = "weeknight", CreatedAt = Now,
            },
            new WorkspaceTag
            {
                Id = SecondTagIdA, WorkspaceId = WorkspaceA,
                Name = "Freezer", NormalizedName = "freezer", CreatedAt = Now,
            });

        await db.SaveChangesAsync();
    }
}
