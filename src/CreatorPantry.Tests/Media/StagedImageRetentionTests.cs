using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Media;

#pragma warning disable MEAI001

/// <summary>
/// The staged-image retention sweep: expiring what nobody chose, removing the bytes of what nobody will
/// read again, and reconciling storage against the rows that own it (IMG-005, IMG-006).
/// </summary>
/// <remarks>
/// Over SQLite with an in-memory object store and a movable clock. Nothing sleeps: a fortnight passes by
/// moving the clock, and the store records writes against the same clock so an orphan's age is something
/// a test can set rather than wait for.
/// </remarks>
public sealed partial class StagedImageRetentionTests : IDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryPrivateObjectStore _store = new();
    private readonly ServiceProvider _provider;

    public StagedImageRetentionTests()
    {
        _connection.Open();
        _store.Now = _clock.UtcNow;

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock>(_clock)
            .AddApplicationTime()
            .AddTenancy()
            .AddAudit()
            .AddIdempotency(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Idempotency:FingerprintKey"] = Convert.ToBase64String(new byte[32]),
                })
                .Build())
            .AddMediaModule()
            .AddGeneratedImageWorker()
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>());

        services.RemoveAll<IImageGenerator>();
        services.AddSingleton<IImageGenerator>(new FakeImageGenerator());
        services.RemoveAll<IPrivateObjectStore>();
        services.AddSingleton<IPrivateObjectStore>(_store);
        services.RemoveAll<IMalwareScanGateway>();
        services.AddSingleton<IMalwareScanGateway>(new FakeMalwareScanGateway());

        _provider = services.BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "workspace-a", CreatedAt = _clock.UtcNow },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "workspace-b", CreatedAt = _clock.UtcNow });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    // ---- expiry ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_staged_image_nobody_chose_expires_once_its_deadline_passes_and_then_loses_its_bytes()
    {
        var image = await StageAsync(WorkspaceA, 0);

        // Nothing is due yet, and a sweep that found work here would be deleting a creator's images early.
        Assert.Equal(0, (await SweepAsync()).Expired);
        Assert.Single(_store.Keys);

        Advance(MediaPolicy.StagedImageTimeToLive + TimeSpan.FromMinutes(1));

        var first = await SweepAsync();

        // Expired and purged in one pass: expiry moves rows into the purge queue the same pass drains.
        Assert.Equal(1, first.Expired);
        Assert.Equal(1, first.Purged);
        Assert.Empty(_store.Keys);

        var row = await RowAsync(image);
        Assert.Equal(GeneratedImageStatus.Expired, row.Status);
        Assert.NotNull(row.ObjectDeletedAt);
    }

    [Fact]
    public async Task A_kept_image_is_never_expired_however_long_it_sits()
    {
        var image = await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Kept);

        Advance(TimeSpan.FromDays(365));
        var summary = await SweepAsync();

        // Its deadline passed long ago, and the deadline is a historical fact rather than a thing to act
        // on once a creator has chosen the image (12.6).
        Assert.Equal(0, summary.Expired);

        // Nor are its bytes purged, even though 12.9a made a kept image's staging copy collectable: this
        // one is kept with no DAM version naming it, so nothing else holds a copy and the sweep leaves it
        // alone. Unreachable in production — only a committed asset sets Kept — and the point is that the
        // sweep checks rather than trusts.
        Assert.Equal(0, summary.Purged);
        Assert.Equal(GeneratedImageStatus.Kept, (await RowAsync(image)).Status);
        Assert.Single(_store.Keys);
    }

    [Fact]
    public async Task A_declined_image_loses_its_bytes_without_waiting_for_a_deadline()
    {
        var image = await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);

        var summary = await SweepAsync();

        Assert.Equal(0, summary.Expired);
        Assert.Equal(1, summary.Purged);
        Assert.Empty(_store.Keys);

        // The row stays. A creator who generated four and declined three should still see that they did.
        var row = await RowAsync(image);
        Assert.Equal(GeneratedImageStatus.Rejected, row.Status);
        Assert.NotNull(row.ObjectDeletedAt);
    }

    // ---- idempotence and partial failure ---------------------------------------------------------------

    [Fact]
    public async Task A_second_sweep_finds_nothing_left_to_do()
    {
        await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);
        await StageAsync(WorkspaceA, 1, status: GeneratedImageStatus.Rejected);

        Assert.Equal(2, (await SweepAsync()).Purged);

        // Every step queries for work that still needs doing, so running again does it zero times rather
        // than twice. This is also what makes a crash halfway through cost nothing.
        var second = await SweepAsync();

        Assert.Equal(0, second.Expired);
        Assert.Equal(0, second.Purged);
        Assert.Equal(0, second.Orphans);
    }

    [Fact]
    public async Task Unreachable_storage_leaves_the_row_saying_its_bytes_are_still_there()
    {
        var image = await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);
        _store.Unavailable = true;

        Assert.Equal(0, (await SweepAsync()).Purged);

        // Not marked deleted, because they are not. A row claiming otherwise would take the image out of
        // the purge queue for good and leave the bytes behind forever.
        Assert.Null((await RowAsync(image)).ObjectDeletedAt);

        _store.Unavailable = false;

        Assert.Equal(1, (await SweepAsync()).Purged);
        Assert.NotNull((await RowAsync(image)).ObjectDeletedAt);
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task Bytes_that_are_already_gone_still_settle_the_row()
    {
        var image = await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);

        // A delete that happened and whose row never recorded it — the crash this column exists to
        // survive. The sweep deletes again, the store says there was nothing there, and the row settles.
        await _store.DeleteAsync(
            GeneratedImageObjectKey.Container,
            (await RowAsync(image)).ObjectKey,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, (await SweepAsync()).Purged);
        Assert.NotNull((await RowAsync(image)).ObjectDeletedAt);
    }

    // ---- orphan reconciliation -------------------------------------------------------------------------

    [Fact]
    public async Task An_object_no_row_owns_is_removed_once_it_is_old_enough_to_judge()
    {
        // Exactly what a worker that wrote an object and died before committing its row leaves behind.
        var orphan = await WriteOrphanAsync(WorkspaceA, Guid.NewGuid(), 0);

        // Something real for the sweep to find the workspace by: reconciliation works from storage, but
        // the pass only visits workspaces the database says have work.
        await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);

        // Too recent to judge: an in-flight image is indistinguishable from an orphan until the window
        // closes, and a sweep without it would race the generation job.
        Assert.Equal(0, (await SweepAsync()).Orphans);
        Assert.Contains(orphan, _store.Keys);

        Advance(MediaPolicy.OrphanGracePeriod + TimeSpan.FromMinutes(1));
        await StageAsync(WorkspaceA, 1, status: GeneratedImageStatus.Rejected);

        Assert.Equal(1, (await SweepAsync()).Orphans);
        Assert.DoesNotContain(orphan, _store.Keys);
    }

    [Fact]
    public async Task An_object_a_row_owns_is_never_removed_however_old_it_is()
    {
        var image = await StageAsync(WorkspaceA, 0);
        var key = (await RowAsync(image)).ObjectKey;

        Advance(MediaPolicy.OrphanGracePeriod + TimeSpan.FromDays(1));

        // Old enough to judge and owned, so the judgement is "leave it alone". The ownership check is
        // asked of the database rather than assumed from the key's shape.
        await StageAsync(WorkspaceA, 1, status: GeneratedImageStatus.Rejected);

        Assert.Equal(0, (await SweepAsync()).Orphans);
        Assert.Contains(key, _store.Keys);
        Assert.Equal(GeneratedImageStatus.Staged, (await RowAsync(image)).Status);
    }

    /// <summary>
    /// The case a sampling sweep would never find: an orphan past the first page of a prefix.
    /// </summary>
    /// <remarks>
    /// Keys are a pair of GUIDs, so key order is effectively random and an orphan is as likely to sort
    /// last as first. An earlier version took one page and sorted it by age, which passed every test here
    /// because the fake sorted by age too — and would have left this orphan in storage forever. The
    /// orphan's key is forced to sort last so the walk has to reach the end to find it.
    /// </remarks>
    [Fact]
    public async Task An_orphan_past_the_first_page_is_still_found()
    {
        // Enough owned objects to fill more than one reconciliation page, all sorting before the orphan.
        // From one: index zero would be Guid.Empty, which the key builder rightly refuses.
        for (var index = 1; index <= MediaPolicy.ReconciliationPageSize + 5; index++)
        {
            await StageAsync(WorkspaceA, 0, operationId: OperationSorting(index));
        }

        var orphan = await WriteOrphanAsync(
            WorkspaceA, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), 0);

        // Something for the sweep to find the workspace by, and the age window has to have closed.
        Advance(MediaPolicy.OrphanGracePeriod + TimeSpan.FromMinutes(1));
        await StageAsync(WorkspaceA, 1, status: GeneratedImageStatus.Rejected);

        Assert.Equal(1, (await SweepAsync()).Orphans);
        Assert.DoesNotContain(orphan, _store.Keys);

        // And not one owned object went with it.
        Assert.Equal(MediaPolicy.ReconciliationPageSize + 5, _store.Keys.Count);
    }

    [Fact]
    public async Task Each_workspace_removes_its_own_orphan_and_only_its_own()
    {
        var mine = await WriteOrphanAsync(WorkspaceA, Guid.NewGuid(), 0);
        var theirs = await WriteOrphanAsync(WorkspaceB, Guid.NewGuid(), 0);

        Advance(MediaPolicy.OrphanGracePeriod + TimeSpan.FromMinutes(1));

        // Both workspaces have database work, so both are visited and both reconcile. Each must see only
        // its own prefix — with only one workspace swept, the gateway's own key check would mask a
        // prefix-scoping mistake.
        await StageAsync(WorkspaceA, 1, status: GeneratedImageStatus.Rejected);
        await StageAsync(WorkspaceB, 1, status: GeneratedImageStatus.Rejected);

        var summary = await SweepAsync();

        Assert.Equal(2, summary.Workspaces);
        Assert.Equal(2, summary.Orphans);
        Assert.DoesNotContain(mine, _store.Keys);
        Assert.DoesNotContain(theirs, _store.Keys);
    }

    [Fact]
    public async Task A_gateway_refuses_a_key_that_belongs_to_another_workspace()
    {
        var theirs = await WriteOrphanAsync(WorkspaceB, Guid.NewGuid(), 0);

        await using var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            WorkspaceA, "workspace-a", Guid.NewGuid(), WorkspaceRole.Owner, "acct");

        // The exact key, handed to the wrong workspace's gateway. Finding a key is not authorization.
        Assert.False(await scope.ServiceProvider.GetRequiredService<IGeneratedImageObjectGateway>()
            .DeleteAsync(theirs, TestContext.Current.CancellationToken));
        Assert.Contains(theirs, _store.Keys);
    }

    /// <summary>A row pointing somewhere this workspace could not have written. Corrupt, and finite.</summary>
    [Fact]
    public async Task A_row_whose_key_is_not_this_workspaces_is_settled_rather_than_retried_forever()
    {
        var image = await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);
        var theirs = await WriteOrphanAsync(WorkspaceB, Guid.NewGuid(), 0);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var row = await db.GeneratedImages
                .IgnoreQueryFilters()
                .FirstAsync(candidate => candidate.Id == image, TestContext.Current.CancellationToken);

            row.ObjectKey = theirs;

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, (await SweepAsync()).Purged);

        // Settled, so the purge queue drains rather than retrying this row every hour for good.
        Assert.NotNull((await RowAsync(image)).ObjectDeletedAt);

        // And the neighbour's object is untouched, which is the half that actually matters.
        Assert.Contains(theirs, _store.Keys);
    }

    [Fact]
    public async Task The_store_refuses_to_list_a_whole_container()
    {
        // Scoping is the gateway's job; this is the guard that makes the careless case throw.
        await Assert.ThrowsAsync<ArgumentException>(() => _store.ListAsync(
            GeneratedImageObjectKey.Container, string.Empty, 10, null, TestContext.Current.CancellationToken));
    }

    // ---- workspace isolation ---------------------------------------------------------------------------

    [Fact]
    public async Task One_workspaces_sweep_never_reaches_anothers_bytes()
    {
        var mine = await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);
        var theirs = await StageAsync(WorkspaceB, 0);

        var summary = await SweepAsync();

        // One workspace had work and the other did not, so only one row moved and only one object went.
        Assert.Equal(1, summary.Purged);
        Assert.NotNull((await RowAsync(mine)).ObjectDeletedAt);

        var neighbour = await RowAsync(theirs);
        Assert.Equal(GeneratedImageStatus.Staged, neighbour.Status);
        Assert.Null(neighbour.ObjectDeletedAt);
        Assert.Contains(neighbour.ObjectKey, _store.Keys);
    }

    [Fact]
    public async Task A_neighbours_orphan_is_not_this_workspaces_to_remove()
    {
        // An orphan under B's prefix, old enough to judge.
        var neighbourOrphan = await WriteOrphanAsync(WorkspaceB, Guid.NewGuid(), 0);

        Advance(MediaPolicy.OrphanGracePeriod + TimeSpan.FromMinutes(1));

        // Only A has database work, so only A's prefix is reconciled. B's objects are not in the listing
        // A sees at all — the gateway lists this workspace's prefix and nothing else.
        await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);

        Assert.Equal(0, (await SweepAsync()).Orphans);
        Assert.Contains(neighbourOrphan, _store.Keys);
    }

    [Fact]
    public async Task Two_workspaces_with_work_are_each_swept_on_their_own_rows()
    {
        var a = await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);
        var b = await StageAsync(WorkspaceB, 0);

        Advance(MediaPolicy.StagedImageTimeToLive + TimeSpan.FromMinutes(1));

        var summary = await SweepAsync();

        Assert.Equal(2, summary.Workspaces);

        // A's was declined and B's merely ran out of time, and the sweep records the difference.
        Assert.Equal(GeneratedImageStatus.Rejected, (await RowAsync(a)).Status);
        Assert.Equal(GeneratedImageStatus.Expired, (await RowAsync(b)).Status);
        Assert.Empty(_store.Keys);
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private void Advance(TimeSpan by)
    {
        _clock.Advance(by);
        _store.Now = _clock.UtcNow;
    }

    private async Task<StagedImageRetentionPassSummary> SweepAsync()
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IStagedImageRetentionWorker>()
            .RunAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>An object under a workspace's prefix with no row behind it.</summary>
    private async Task<string> WriteOrphanAsync(Guid workspaceId, Guid operationId, int variantIndex)
    {
        var key = GeneratedImageObjectKey.For(workspaceId, operationId, variantIndex);

        await _store.PutAsync(
            GeneratedImageObjectKey.Container,
            key,
            new MemoryStream(BrandSourceSampleFiles.Png()),
            "image/png",
            MediaPolicy.ImageMaxBytes,
            TestContext.Current.CancellationToken);

        return key;
    }

    /// <summary>An operation id whose hex sorts in <paramref name="index"/> order, for paging tests.</summary>
    private static Guid OperationSorting(int index) => Guid.Parse($"00000000-0000-0000-0000-{index:D12}");

    private async Task<Guid> StageAsync(
        Guid workspaceId,
        int variantIndex,
        GeneratedImageStatus status = GeneratedImageStatus.Staged,
        Guid? operationId = null)
    {
        operationId ??= Guid.NewGuid();
        var key = GeneratedImageObjectKey.For(workspaceId, operationId.Value, variantIndex);

        var write = await _store.PutAsync(
            GeneratedImageObjectKey.Container,
            key,
            new MemoryStream(BrandSourceSampleFiles.Png()),
            "image/png",
            MediaPolicy.ImageMaxBytes,
            TestContext.Current.CancellationToken);

        var now = _clock.UtcNow;
        var imageId = Guid.NewGuid();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId.Value,
            WorkspaceId = workspaceId,
            Status = GeneratedImageOperationStatus.Succeeded,
            PromptText = "a bowl of soup on a wooden table",
            VariantCount = MediaPolicy.MaxVariantsPerOperation,
            IdempotencyKey = operationId.Value.ToString("N"),
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = now,
            StatusChangedAt = now,
            AvailableAt = now,
        });

        db.GeneratedImages.Add(new GeneratedImage
        {
            Id = imageId,
            WorkspaceId = workspaceId,
            GeneratedImageOperationId = operationId.Value,
            VariantIndex = variantIndex,
            Status = status,
            ObjectKey = key,
            MediaType = "image/png",
            Width = 4,
            Height = 3,
            SizeBytes = write.Object!.SizeBytes,
            ContentChecksum = write.Object.ContentChecksum,
            ProviderName = "fake",
            ModelName = "fake-image-1",
            RetentionExpiresAt = now + MediaPolicy.StagedImageTimeToLive,
            CreatedAt = now,
            StatusChangedAt = now,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return imageId;
    }

    /// <summary>
    /// Reads past the filters, because a test asserting isolation has to see both sides of it.
    /// </summary>
    private async Task<GeneratedImage> RowAsync(Guid imageId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .GeneratedImages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(image => image.Id == imageId, TestContext.Current.CancellationToken);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
