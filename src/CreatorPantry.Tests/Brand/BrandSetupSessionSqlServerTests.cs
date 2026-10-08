using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The setup session against a real SQL Server: the migration, a row version that really moves, and the two
/// guards (unique index, row version) that SQLite cannot show.
/// </summary>
public sealed class BrandSetupSessionSqlServerTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-22.04").Build();

    private ServiceProvider? _provider;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddAudit()
            .AddIdempotency(new ConfigurationBuilder().Build())
            .AddBrandModule()

            // The brand profile facade asks the Media module whether a submitted logo is in this workspace's
            // library (12.10k), so that lookup has to be resolvable wherever the brand module is composed.
            .AddMediaModule()
            .AddLogging()
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()))
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await db.Database.MigrateAsync();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Now });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    private AsyncServiceScope ScopeFor(Guid workspaceId, string account = "user-1")
    {
        var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId, workspaceId == WorkspaceA ? "workspace-a" : "workspace-b", Guid.NewGuid(), WorkspaceRole.Owner, account);

        return scope;
    }

    private static SaveBrandSetupSessionViewModel Model(string draft = "{}") => new()
    {
        CurrentStep = "goals",
        FurthestStep = "goals",
        CompletedSteps = [],
        SkippedSteps = [],
        DraftJson = draft,
    };

    private static BrandSetupSession NewSession(string userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        CreatedUtc = Now,
        UpdatedUtc = Now,
    };

    private static AuditEntry AuditOf(BrandSetupSession session) => new(
        session.UserId, BrandAuditActions.SetupSessionStarted, BrandAuditActions.SetupSessionResourceType,
        session.Id.ToString("D"), Guid.NewGuid(), "test");

    [Fact]
    public async Task The_migration_creates_the_table_and_an_old_token_is_a_conflict_once_the_row_version_moves()
    {
        var ct = TestContext.Current.CancellationToken;
        string first;
        await using (var scope = ScopeFor(WorkspaceA))
        {
            var created = await scope.ServiceProvider.GetRequiredService<IBrandSetupSessionFacade>().SaveAsync(Model(), null, ct);
            Assert.True(created.Succeeded, created.Error?.Message);
            Assert.True(created.Value.Created);
            first = created.Value.Session.RowVersion;
        }

        string second;
        await using (var scope = ScopeFor(WorkspaceA))
        {
            var updated = await scope.ServiceProvider.GetRequiredService<IBrandSetupSessionFacade>().SaveAsync(Model("{\"v\":2}"), first, ct);
            Assert.True(updated.Succeeded, updated.Error?.Message);
            second = updated.Value.Session.RowVersion;
        }

        Assert.NotEqual(first, second);

        await using (var scope = ScopeFor(WorkspaceA))
        {
            var stale = await scope.ServiceProvider.GetRequiredService<IBrandSetupSessionFacade>().SaveAsync(Model("{\"v\":3}"), first, ct);

            Assert.Equal(BrandErrorCodes.SetupSessionConflict, stale.Error?.Code);
            var row = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().BrandSetupSessions.SingleAsync(ct);
            Assert.Equal("{\"v\":2}", row.DraftJson);
        }
    }

    [Fact]
    public async Task Two_racing_first_saves_for_one_user_cannot_both_create()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var first = ScopeFor(WorkspaceA, "racer");
        await using var second = ScopeFor(WorkspaceA, "racer");
        var firstLayer = first.ServiceProvider.GetRequiredService<IBrandSetupSessionDataLayer>();
        var secondLayer = second.ServiceProvider.GetRequiredService<IBrandSetupSessionDataLayer>();
        var a = NewSession("racer");
        var b = NewSession("racer");

        Assert.True(await firstLayer.CreateAsync(a, AuditOf(a), ct));
        Assert.False(await secondLayer.CreateAsync(b, AuditOf(b), ct));

        await using var check = ScopeFor(WorkspaceA, "racer");
        Assert.Single(await check.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().BrandSetupSessions.ToListAsync(ct));
    }

    [Fact]
    public async Task The_same_user_id_may_hold_a_session_in_each_workspace()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var workspace in new[] { WorkspaceA, WorkspaceB })
        {
            await using var scope = ScopeFor(workspace, "shared-user");
            var session = NewSession("shared-user");
            Assert.True(await scope.ServiceProvider.GetRequiredService<IBrandSetupSessionDataLayer>().CreateAsync(session, AuditOf(session), ct));
        }

        await using var inA = ScopeFor(WorkspaceA, "shared-user");
        var rows = await inA.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().BrandSetupSessions.ToListAsync(ct);
        Assert.Equal(WorkspaceA, Assert.Single(rows).WorkspaceId);
    }

    [Fact]
    public async Task Of_two_writers_who_read_the_same_version_the_second_loses()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = ScopeFor(WorkspaceA, "writer"))
        {
            var session = NewSession("writer");
            Assert.True(await seed.ServiceProvider.GetRequiredService<IBrandSetupSessionDataLayer>().CreateAsync(session, AuditOf(session), ct));
        }

        await using var first = ScopeFor(WorkspaceA, "writer");
        await using var second = ScopeFor(WorkspaceA, "writer");
        var firstLayer = first.ServiceProvider.GetRequiredService<IBrandSetupSessionDataLayer>();
        var secondLayer = second.ServiceProvider.GetRequiredService<IBrandSetupSessionDataLayer>();

        var firstCopy = (await firstLayer.GetForUpdateAsync("writer", ct))!;
        var secondCopy = (await secondLayer.GetForUpdateAsync("writer", ct))!;

        firstCopy.DraftJson = "{\"winner\":true}";
        Assert.True(await firstLayer.UpdateAsync(firstCopy, null, ct));

        secondCopy.DraftJson = "{\"loser\":true}";
        Assert.False(await secondLayer.UpdateAsync(secondCopy, null, ct));

        await using var check = ScopeFor(WorkspaceA, "writer");
        var row = await check.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().BrandSetupSessions.SingleAsync(ct);
        Assert.Equal("{\"winner\":true}", row.DraftJson);
    }
}
