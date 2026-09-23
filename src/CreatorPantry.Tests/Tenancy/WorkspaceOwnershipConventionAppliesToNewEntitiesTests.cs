using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Tenancy;

/// <summary>
/// Proves the filter is applied "by convention": a brand-new entity type this file invents, that
/// <see cref="WorkspaceOwnershipConvention"/> and <see cref="CreatorPantryDbContext"/> have never heard of
/// and that has no <c>HasQueryFilter</c> configured anywhere, is still covered automatically the moment it
/// implements <see cref="IWorkspaceOwned"/> — because discovery happens by scanning the model, not by a
/// per-entity configuration list.
/// </summary>
public sealed class WorkspaceOwnershipConventionAppliesToNewEntitiesTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;

    public WorkspaceOwnershipConventionAppliesToNewEntitiesTests()
    {
        _connection.Open();
        _services = new ServiceCollection()
            .AddTenancy()
            .AddDbContext<NewlyDiscoveredEntityDbContext>(options => options.UseSqlite(_connection))
            .BuildServiceProvider(validateScopes: true);

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<NewlyDiscoveredEntityDbContext>().Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task An_undeclared_workspace_owned_entity_throws_before_resolution()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NewlyDiscoveredEntityDbContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Widgets.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_undeclared_workspace_owned_entity_is_scoped_once_resolved()
    {
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();

        using (var seedScope = _services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<NewlyDiscoveredEntityDbContext>();
            db.Widgets.AddRange(
                new Widget { Id = Guid.NewGuid(), WorkspaceId = workspaceA, Name = "A's widget" },
                new Widget { Id = Guid.NewGuid(), WorkspaceId = workspaceB, Name = "B's widget" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(workspaceA, "workspace-a", Guid.NewGuid(), WorkspaceRole.Owner);

        var visible = await scope.ServiceProvider.GetRequiredService<NewlyDiscoveredEntityDbContext>()
            .Widgets.ToListAsync(TestContext.Current.CancellationToken);

        var only = Assert.Single(visible);
        Assert.Equal("A's widget", only.Name);
    }

    [Fact]
    public async Task Two_concurrently_resolved_scopes_never_see_each_others_workspace()
    {
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();

        using (var seedScope = _services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<NewlyDiscoveredEntityDbContext>();
            db.Widgets.AddRange(
                new Widget { Id = Guid.NewGuid(), WorkspaceId = workspaceA, Name = "A's widget" },
                new Widget { Id = Guid.NewGuid(), WorkspaceId = workspaceB, Name = "B's widget" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Two independent scopes — the same shape as two concurrent requests — each resolved to a
        // different workspace, interleaved rather than run one after the other.
        using var scopeA = _services.CreateScope();
        using var scopeB = _services.CreateScope();
        scopeA.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(workspaceA, "workspace-a", Guid.NewGuid(), WorkspaceRole.Owner);
        scopeB.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(workspaceB, "workspace-b", Guid.NewGuid(), WorkspaceRole.Owner);

        var dbA = scopeA.ServiceProvider.GetRequiredService<NewlyDiscoveredEntityDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<NewlyDiscoveredEntityDbContext>();

        // Queried out of order (B before A, then A again) to rule out one scope's resolved workspace
        // leaking into the other via any shared/cached state in the query filter.
        var visibleToB = await dbB.Widgets.ToListAsync(TestContext.Current.CancellationToken);
        var visibleToA = await dbA.Widgets.ToListAsync(TestContext.Current.CancellationToken);
        var visibleToAAgain = await dbA.Widgets.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("B's widget", Assert.Single(visibleToB).Name);
        Assert.Equal("A's widget", Assert.Single(visibleToA).Name);
        Assert.Equal("A's widget", Assert.Single(visibleToAAgain).Name);
    }

    /// <summary>A stand-in for "some future recipe/content/media entity" — implements the marker and nothing else.</summary>
    private sealed class Widget : IWorkspaceOwned
    {
        public Guid Id { get; set; }
        public Guid WorkspaceId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// A DbContext with no knowledge of <see cref="CreatorPantryDbContext"/>'s entities. It applies the same
    /// production convention method and configures no filter of its own, proving the convention — not this
    /// context — is what does the work.
    /// </summary>
    private sealed class NewlyDiscoveredEntityDbContext(DbContextOptions<NewlyDiscoveredEntityDbContext> options, IWorkspaceContext? workspaceContext = null)
        : DbContext(options), IWorkspaceIdSource
    {
        public DbSet<Widget> Widgets => Set<Widget>();

        Guid? IWorkspaceIdSource.CurrentWorkspaceIdOrNull => workspaceContext is { IsResolved: true } context ? context.WorkspaceId : null;

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            WorkspaceOwnershipConvention.Apply(builder, this);
        }
    }
}
