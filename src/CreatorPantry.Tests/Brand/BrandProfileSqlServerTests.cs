using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using CreatorPantry.Tests.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The brand profile against a real SQL Server, for the three things SQLite cannot show: a row version that
/// actually moves, the lost-race paths in the data layer, and constraints written in SQL Server's dialect.
/// </summary>
/// <remarks>
/// The race is made deterministic rather than timed: two scopes each read the profile at the same row version,
/// then write one after the other, so the second is always the loser and the test never depends on scheduling.
/// </remarks>
public sealed class BrandProfileSqlServerTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

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

    private AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider!.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId, workspaceId == WorkspaceA ? "workspace-a" : "workspace-b", Guid.NewGuid(), WorkspaceRole.Owner, "acct");

        return scope;
    }

    private static BrandProfile NewProfile(string name = "Sam's Kitchen") => new()
    {
        Id = Guid.NewGuid(),
        BrandName = name,
        CreatedAt = Now,
        UpdatedAt = Now,
        CreatedByMembershipId = Guid.NewGuid(),
        UpdatedByMembershipId = Guid.NewGuid(),
    };

    private static BrandProfileRevision RevisionOf(BrandProfile profile) => new()
    {
        Id = Guid.NewGuid(),
        BrandProfileId = profile.Id,
        Revision = profile.Revision,
        SchemaVersion = 1,
        Document = "{}",
        ChangedByMembershipId = profile.UpdatedByMembershipId,
        CreatedAt = Now,
    };

    private static AuditEntry AuditOf(BrandProfile profile) => new(
        "user", BrandAuditActions.Updated, BrandAuditActions.ResourceType, profile.Id.ToString("D"),
        Guid.NewGuid(), "test", null, profile.Revision.ToString());

    private async Task<BrandProfile> SeedAsync(Guid workspaceId)
    {
        await using var scope = ScopeFor(workspaceId);
        var layer = scope.ServiceProvider.GetRequiredService<IBrandProfileDataLayer>();
        var profile = NewProfile();

        Assert.True(await layer.CreateAsync(profile, RevisionOf(profile), AuditOf(profile), TestContext.Current.CancellationToken));

        return profile;
    }

    [Fact]
    public async Task The_migrations_create_the_brand_tables_on_a_real_server()
    {
        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // Reaching here means MigrateAsync accepted the filtered index, rowversion and check constraints.
        Assert.Empty(await db.BrandProfiles.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.BrandProfileRevisions.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_row_version_moves_on_a_real_update_so_an_old_token_is_a_conflict()
    {
        var cancellation = TestContext.Current.CancellationToken;
        string firstToken;
        await using (var scope = ScopeFor(WorkspaceA))
        {
            var facade = scope.ServiceProvider.GetRequiredService<IBrandProfileFacade>();
            var created = await facade.CreateAsync("user", new CreateBrandProfileViewModel { BrandName = "Sam's Kitchen" }, null, cancellation);
            Assert.True(created.Result.Succeeded);
            firstToken = created.Result.Value!.ConcurrencyToken;
        }

        string secondToken;
        await using (var scope = ScopeFor(WorkspaceA))
        {
            var facade = scope.ServiceProvider.GetRequiredService<IBrandProfileFacade>();
            var edited = await facade.UpdateAsync(
                "user",
                new UpdateBrandProfileViewModel
                {
                    ExpectedConcurrencyToken = firstToken,
                    Locale = CreatorPantry.Domain.Managers.Patching.PatchField<string?>.Submitted("en-US"),
                },
                null,
                cancellation);
            Assert.True(edited.Result.Succeeded, edited.Result.Error?.Message);
            secondToken = edited.Result.Value!.ConcurrencyToken;
        }

        Assert.NotEqual(firstToken, secondToken);

        await using (var scope = ScopeFor(WorkspaceA))
        {
            var facade = scope.ServiceProvider.GetRequiredService<IBrandProfileFacade>();
            var stale = await facade.UpdateAsync(
                "user",
                new UpdateBrandProfileViewModel
                {
                    ExpectedConcurrencyToken = firstToken,
                    Locale = CreatorPantry.Domain.Managers.Patching.PatchField<string?>.Submitted("fr-FR"),
                },
                null,
                cancellation);

            Assert.Equal(BrandErrorCodes.Conflict, stale.Result.Error?.Code);
        }
    }

    [Fact]
    public async Task Of_two_writers_who_read_the_same_version_the_second_loses_and_leaves_nothing_behind()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(WorkspaceA);

        await using var first = ScopeFor(WorkspaceA);
        await using var second = ScopeFor(WorkspaceA);
        var firstLayer = first.ServiceProvider.GetRequiredService<IBrandProfileDataLayer>();
        var secondLayer = second.ServiceProvider.GetRequiredService<IBrandProfileDataLayer>();

        var firstCopy = (await firstLayer.GetForUpdateAsync(cancellation))!;
        var secondCopy = (await secondLayer.GetForUpdateAsync(cancellation))!;

        firstCopy.BrandName = "Winner";
        firstCopy.Revision = 2;
        Assert.True(await firstLayer.UpdateAsync(firstCopy, RevisionOf(firstCopy), AuditOf(firstCopy), cancellation));

        secondCopy.BrandName = "Loser";
        secondCopy.Revision = 2;
        Assert.False(await secondLayer.UpdateAsync(secondCopy, RevisionOf(secondCopy), AuditOf(secondCopy), cancellation));

        // Nothing staged for the next save on the losing scope to commit.
        Assert.Empty(second.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().ChangeTracker.Entries());

        await using var check = ScopeFor(WorkspaceA);
        var db = check.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Equal("Winner", (await db.BrandProfiles.SingleAsync(cancellation)).BrandName);
        Assert.Equal([1, 2], (await db.BrandProfileRevisions.OrderBy(r => r.Revision).ToListAsync(cancellation)).Select(r => r.Revision));
        Assert.Equal(seeded.Id, (await db.BrandProfiles.SingleAsync(cancellation)).Id);
    }

    [Fact]
    public async Task Of_two_creates_in_one_workspace_the_second_is_refused_and_nothing_is_left_staged()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await SeedAsync(WorkspaceB);

        await using var scope = ScopeFor(WorkspaceB);
        var layer = scope.ServiceProvider.GetRequiredService<IBrandProfileDataLayer>();
        var other = NewProfile("Second");

        Assert.False(await layer.CreateAsync(other, RevisionOf(other), AuditOf(other), cancellation));
        Assert.Empty(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().ChangeTracker.Entries());

        await using var check = ScopeFor(WorkspaceB);
        var db = check.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Single(await db.BrandProfiles.ToListAsync(cancellation));
        Assert.Single(await db.BrandProfileRevisions.ToListAsync(cancellation));
    }

    [Fact]
    public async Task A_blank_name_and_a_second_primary_logo_are_refused_by_the_server()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var workspaceA = WorkspaceA;

        await using (var scope = ScopeFor(workspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.BrandProfiles.Add(NewProfile("   "));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellation));
        }

        var profile = await SeedAsync(workspaceA);

        // 12.9 gave BrandAssetLink the composite foreign key its configuration had promised, so a logo now
        // has to name a real asset of this workspace.
        var asset = SeededMediaAsset.For(workspaceA);

        await using (var scope = ScopeFor(workspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.MediaAssets.Add(asset);
            await db.SaveChangesAsync(cancellation);
        }

        BrandAssetLink Logo(BrandAssetRole role, int order) => new()
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceA, BrandProfileId = profile.Id,
            MediaAssetId = asset.Id, Role = role, SortOrder = order,
        };

        await using (var scope = ScopeFor(workspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.BrandAssetLinks.AddRange(Logo(BrandAssetRole.PrimaryLogo, 0), Logo(BrandAssetRole.AlternateLogo, 1), Logo(BrandAssetRole.AlternateLogo, 2));
            await db.SaveChangesAsync(cancellation);
        }

        await using (var scope = ScopeFor(workspaceA))
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.BrandAssetLinks.Add(Logo(BrandAssetRole.PrimaryLogo, 3));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(cancellation));
        }
    }
}
