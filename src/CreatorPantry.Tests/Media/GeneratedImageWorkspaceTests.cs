using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// That the generated-image workspace is a workspace: two creators, the same ids, and nothing crossing.
/// </summary>
/// <remarks>
/// <para>
/// 12.6 lands the tables and one read, so what can be tested is exactly that read and the ownership the rows
/// carry — which is the part worth proving before anything writes them. A staged image is the most private
/// thing this product holds on a creator's behalf: unpublished work they have not chosen yet.
/// </para>
/// <para>
/// A single-workspace test is not isolation coverage (tenancy.md), so every case here seeds both workspaces
/// and asks from each. The real SQL Server questions — the DDL, the filtered indexes, the cascade through a
/// restricted pin — are <c>PromptRecordSqlServerTests</c>'s, because that is where the migration is applied.
/// </para>
/// </remarks>
public sealed class GeneratedImageWorkspaceTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceA = Guid.NewGuid();

    private static readonly Guid WorkspaceB = Guid.NewGuid();

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private ServiceProvider _provider = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync(Ct);

        _provider = new ServiceCollection()

            // 12.8 gave the Media module a gateway and a data layer that log, so the module no longer
            // composes without logging. Nothing here reads a log; this is what lets the container build.
            .AddLogging()
            .AddTenancy()
            .AddAudit()
            .AddMediaModule()
            .AddSingleton<IClock>(new StoppedClock())
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await db.Database.EnsureCreatedAsync(Ct);

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = Now },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = Now });
        await db.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private AsyncServiceScope ScopeFor(Guid workspaceId)
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            WorkspaceRole.Owner,
            "user-1");

        return scope;
    }

    /// <summary>
    /// One request and one staged image for it, inserted with no <c>WorkspaceId</c> set by hand.
    /// </summary>
    /// <remarks>
    /// The omission is deliberate: ownership comes from the resolved context through
    /// <c>WorkspaceOwnershipInterceptor</c>, so a seed that filled it in would prove nothing about where the
    /// value comes from. 12.6 lands the tables and the facade that stages an image arrives with 12.7's worker,
    /// so a direct insert is the only writer there is.
    /// </remarks>
    private async Task<(Guid OperationId, Guid ImageId)> SeedAsync(
        Guid workspaceId, int variantIndex = 0, string? idempotencyKey = null)
    {
        await using var scope = ScopeFor(workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var operationId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId,
            Status = GeneratedImageOperationStatus.Succeeded,
            PromptText = "Overhead shot of soda bread on linen, soft window light.",
            VariantCount = 1,
            IdempotencyKey = idempotencyKey ?? $"image-{operationId}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,
        });

        db.GeneratedImages.Add(new GeneratedImage
        {
            Id = imageId,
            GeneratedImageOperationId = operationId,
            VariantIndex = variantIndex,
            Status = GeneratedImageStatus.Staged,
            ObjectKey = $"staging/{workspaceId:N}/{imageId:N}.png",
            MediaType = "image/png",
            Width = 1024,
            Height = 1024,
            SizeBytes = 2048,
            ContentChecksum = "sha256:" + new string('a', 64),
            ProviderName = "test-provider",
            ModelName = "test-model",
            RetentionExpiresAt = Now + MediaPolicy.StagedImageTimeToLive,
            CreatedAt = Now,
            StatusChangedAt = Now,
        });

        await db.SaveChangesAsync(Ct);

        return (operationId, imageId);
    }

    [Fact]
    public async Task Ownership_is_stamped_from_the_resolved_workspace_rather_than_set_by_a_writer()
    {
        var (operationId, imageId) = await SeedAsync(WorkspaceA);

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var operation = await db.GeneratedImageOperations.AsNoTracking()
            .SingleAsync(row => row.Id == operationId, Ct);
        var image = await db.GeneratedImages.AsNoTracking().SingleAsync(row => row.Id == imageId, Ct);

        Assert.Equal(WorkspaceA, operation.WorkspaceId);
        Assert.Equal(WorkspaceA, image.WorkspaceId);
    }

    [Fact]
    public async Task Nothing_can_be_staged_before_a_workspace_is_resolved()
    {
        // The interceptor refuses the insert rather than inventing an owner, which is what makes 12.7's worker
        // a loud failure if it ever queues an image without resolving the workspace it is generating for.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = Guid.NewGuid(),
            Status = GeneratedImageOperationStatus.Requested,
            PromptText = "No workspace resolved.",
            VariantCount = 1,
            IdempotencyKey = "orphan",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,
        });

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task One_workspaces_staged_images_are_not_in_the_others_set()
    {
        var mine = await SeedAsync(WorkspaceA);
        var theirs = await SeedAsync(WorkspaceB);

        await using (var a = ScopeFor(WorkspaceA))
        {
            var db = a.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

            Assert.Equal(1, await db.GeneratedImages.CountAsync(Ct));
            Assert.Equal(1, await db.GeneratedImageOperations.CountAsync(Ct));
            Assert.False(await db.GeneratedImages.AnyAsync(row => row.Id == theirs.ImageId, Ct));
            Assert.False(await db.GeneratedImageOperations.AnyAsync(row => row.Id == theirs.OperationId, Ct));
        }

        await using var b = ScopeFor(WorkspaceB);
        var other = b.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.False(await other.GeneratedImages.AnyAsync(row => row.Id == mine.ImageId, Ct));
        Assert.False(await other.GeneratedImageOperations.AnyAsync(row => row.Id == mine.OperationId, Ct));
    }

    /// <summary>
    /// The one read this module offers, asked from both sides.
    /// </summary>
    /// <remarks>
    /// It takes a bare <c>Guid</c> and returns a bare <c>bool</c>, so nothing about its signature would stop a
    /// later change adding a workspace predicate, an <c>IgnoreQueryFilters</c>, or a read that answers for any
    /// workspace at all. It is also the seam a prompt record's immutable pin is validated through, so a leak
    /// here would be written permanently into another module's table.
    /// </remarks>
    [Fact]
    public async Task The_lookup_answers_only_for_the_resolved_workspace()
    {
        var mine = await SeedAsync(WorkspaceA);
        var theirs = await SeedAsync(WorkspaceB);

        await using (var a = ScopeFor(WorkspaceA))
        {
            var lookup = a.ServiceProvider.GetRequiredService<IGeneratedImageLookupFacade>();

            Assert.True(await lookup.ExistsAsync(mine.ImageId, Ct));

            // The neighbour's image and an id that was never issued are the same answer, so the lookup cannot
            // be used to ask what another workspace holds (tenancy.md).
            Assert.False(await lookup.ExistsAsync(theirs.ImageId, Ct));
            Assert.False(await lookup.ExistsAsync(Guid.NewGuid(), Ct));
            Assert.False(await lookup.ExistsAsync(Guid.Empty, Ct));
        }

        await using var b = ScopeFor(WorkspaceB);
        var theirLookup = b.ServiceProvider.GetRequiredService<IGeneratedImageLookupFacade>();

        Assert.True(await theirLookup.ExistsAsync(theirs.ImageId, Ct));
        Assert.False(await theirLookup.ExistsAsync(mine.ImageId, Ct));
    }

    [Fact]
    public async Task The_lookup_cannot_be_asked_before_a_workspace_is_resolved()
    {
        // It fails closed rather than answering for every workspace at once.
        var mine = await SeedAsync(WorkspaceA);

        await using var scope = _provider.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IGeneratedImageLookupFacade>();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => lookup.ExistsAsync(mine.ImageId, Ct));
    }

    [Fact]
    public async Task The_same_idempotency_key_is_a_separate_request_in_each_workspace()
    {
        // Workspace-relative uniqueness (tenancy.md): two creators generating at the same moment must not
        // collide, and one workspace must not be able to discover a key another used by having its own write
        // refused. Reusing it inside one workspace is what the unique index is for, below.
        await SeedAsync(WorkspaceA, idempotencyKey: "same-key");
        await SeedAsync(WorkspaceB, idempotencyKey: "same-key");

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.Equal(1, await db.GeneratedImageOperations.CountAsync(Ct));
    }

    [Fact]
    public async Task Reusing_one_workspaces_idempotency_key_is_refused_by_the_index()
    {
        // The guarantee that makes a lost response cost nothing: a retry loses the index rather than buying a
        // second generation, which is the most expensive mistake this product can make.
        await SeedAsync(WorkspaceA, idempotencyKey: "same-key");

        await Assert.ThrowsAsync<DbUpdateException>(() => SeedAsync(WorkspaceA, idempotencyKey: "same-key"));
    }

    /// <summary>
    /// Both sets carry a query filter at all, asserted on the model rather than inferred from a passing read.
    /// </summary>
    /// <remarks>
    /// The reads above would also pass if the filter were gone and the ids simply happened not to collide,
    /// which is why this asks the model directly. <c>WorkspaceOwnershipConvention</c> applies it from
    /// <see cref="IWorkspaceOwned"/>, so the failure this catches is an entity configured in a way that opts
    /// out of the convention — a <c>HasQueryFilter(null)</c>, or a base type the convention does not see.
    /// </remarks>
    [Theory]
    [InlineData(typeof(GeneratedImage))]
    [InlineData(typeof(GeneratedImageOperation))]
    public void Both_sets_are_filtered_by_workspace(Type entity)
    {
        using var scope = _provider.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Model;

        Assert.NotEmpty(model.FindEntityType(entity)!.GetDeclaredQueryFilters());
    }

    [Fact]
    public async Task A_neighbours_image_cannot_be_loaded_to_be_changed()
    {
        // The filter is what makes a cross-workspace update impossible rather than merely refused: there is no
        // row to track, so a status move or a delete has nothing to act on. Asked through the status a creator
        // would move — keeping or rejecting an image — because that is 12.8's write and this is its floor.
        var theirs = await SeedAsync(WorkspaceB);

        await using var scope = ScopeFor(WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.Null(await db.GeneratedImages.SingleOrDefaultAsync(row => row.Id == theirs.ImageId, Ct));
        Assert.Null(await db.GeneratedImageOperations
            .SingleOrDefaultAsync(row => row.Id == theirs.OperationId, Ct));

        // And an ExecuteUpdate over the whole set reaches none of it, which is the shape a sweep or a bulk
        // status move would take. Nothing in this workspace to update, and the neighbour's row untouched.
        var moved = await db.GeneratedImages.ExecuteUpdateAsync(
            setters => setters.SetProperty(row => row.Status, GeneratedImageStatus.Rejected), Ct);

        Assert.Equal(0, moved);

        await using var b = ScopeFor(WorkspaceB);
        var other = b.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var untouched = await other.GeneratedImages.AsNoTracking()
            .SingleAsync(row => row.Id == theirs.ImageId, Ct);

        Assert.Equal(GeneratedImageStatus.Staged, untouched.Status);
    }

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }
}
