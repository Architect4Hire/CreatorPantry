using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// What the retention sweep does with renditions (AF.5.4): removes them when their source no longer wants
/// them, leaves them alone otherwise, and never touches an original on the way.
/// </summary>
public sealed partial class StagedImageRetentionTests
{
    [Fact]
    public async Task A_declined_images_renditions_lose_their_bytes_and_their_rows()
    {
        var image = await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);
        await AddRenditionAsync(WorkspaceA, image, MediaRenditionPurpose.Web);
        await AddRenditionAsync(WorkspaceA, image, MediaRenditionPurpose.Thumbnail, MediaRenditionReason.NotSmaller);

        var summary = await SweepAsync();

        // Both rows, though only one of them had bytes to remove.
        Assert.Equal(2, summary.Renditions);
        Assert.Empty(await RenditionsAsync(WorkspaceA));
        Assert.Empty(_store.Keys);

        // The image's own row is history and stays; a rendition's is not, and does not.
        Assert.NotNull((await RowAsync(image)).ObjectDeletedAt);
    }

    [Fact]
    public async Task An_expired_images_renditions_go_in_the_pass_that_expires_it()
    {
        var image = await StageAsync(WorkspaceA, 0);
        await AddRenditionAsync(WorkspaceA, image, MediaRenditionPurpose.Web);

        Advance(MediaPolicy.StagedImageTimeToLive + TimeSpan.FromMinutes(1));

        var summary = await SweepAsync();

        Assert.Equal(1, summary.Expired);
        Assert.Equal(1, summary.Renditions);
        Assert.Empty(await RenditionsAsync(WorkspaceA));
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task A_staged_images_renditions_are_left_alone_and_are_never_orphans()
    {
        var waiting = await StageAsync(WorkspaceA, 0);
        var web = await AddRenditionAsync(WorkspaceA, waiting, MediaRenditionPurpose.Web);
        var original = (await RowAsync(waiting)).ObjectKey;

        // Old enough for the orphan reconciliation to judge, and something else for the sweep to come for.
        Advance(MediaPolicy.OrphanGracePeriod + TimeSpan.FromMinutes(1));
        await StageAsync(WorkspaceA, 1, status: GeneratedImageStatus.Rejected);

        var summary = await SweepAsync();

        Assert.Equal(0, summary.Renditions);
        Assert.Equal(0, summary.Orphans);
        Assert.Single(await RenditionsAsync(WorkspaceA));
        Assert.Contains(web, _store.Keys);
        Assert.Contains(original, _store.Keys);
    }

    [Fact]
    public async Task Unreachable_storage_leaves_a_renditions_row_until_its_bytes_can_be_removed()
    {
        var image = await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);
        await AddRenditionAsync(WorkspaceA, image, MediaRenditionPurpose.Web);
        _store.Unavailable = true;

        Assert.Equal(0, (await SweepAsync()).Renditions);

        // Still there, because its bytes are. A row removed now would leave an object nothing names.
        Assert.Single(await RenditionsAsync(WorkspaceA));

        _store.Unavailable = false;

        Assert.Equal(1, (await SweepAsync()).Renditions);
        Assert.Empty(await RenditionsAsync(WorkspaceA));
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task A_second_sweep_finds_no_renditions_left_to_remove()
    {
        var image = await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);
        await AddRenditionAsync(WorkspaceA, image, MediaRenditionPurpose.Web);

        Assert.Equal(1, (await SweepAsync()).Renditions);

        // Nothing left to do, so the workspace is not even visited.
        var again = await SweepAsync();
        Assert.Equal(0, again.Renditions);
        Assert.Equal(0, again.Workspaces);
    }

    [Fact]
    public async Task A_deleted_library_assets_renditions_are_removed_and_its_original_is_not()
    {
        var deleted = await AddLibraryVersionAsync(WorkspaceA, deletedAt: _clock.UtcNow);
        var live = await AddLibraryVersionAsync(WorkspaceA, deletedAt: null);
        var gone = await AddLibraryRenditionAsync(WorkspaceA, deleted, MediaRenditionPurpose.Web);
        var kept = await AddLibraryRenditionAsync(WorkspaceA, live, MediaRenditionPurpose.Web);

        // The only retention work this workspace has, so the sweep finding it at all is part of the test.
        var summary = await SweepAsync();

        Assert.Equal(1, summary.Workspaces);
        Assert.Equal(1, summary.Renditions);
        Assert.DoesNotContain(gone, _store.Keys);

        // The live asset's rendition, and both originals: a deleted asset keeps its bytes, as it always has.
        Assert.Contains(kept, _store.Keys);
        Assert.Contains(deleted.ObjectKey, _store.Keys);
        Assert.Contains(live.ObjectKey, _store.Keys);
        Assert.Equal(live.AssetId, Assert.Single(await RenditionsAsync(WorkspaceA)).MediaAssetId);
    }

    [Fact]
    public async Task One_workspaces_sweep_never_removes_anothers_renditions()
    {
        var mine = await StageAsync(WorkspaceA, 0, status: GeneratedImageStatus.Rejected);
        var theirs = await StageAsync(WorkspaceB, 0);
        await AddRenditionAsync(WorkspaceA, mine, MediaRenditionPurpose.Web);
        var theirWeb = await AddRenditionAsync(WorkspaceB, theirs, MediaRenditionPurpose.Web);

        var summary = await SweepAsync();

        // Only A had work. B's image is still waiting for its creator, and so is everything made from it.
        Assert.Equal(1, summary.Workspaces);
        Assert.Equal(1, summary.Renditions);
        Assert.Empty(await RenditionsAsync(WorkspaceA));
        Assert.Single(await RenditionsAsync(WorkspaceB));
        Assert.Contains(theirWeb, _store.Keys);
    }

    [Fact]
    public async Task The_rendition_gateway_refuses_a_neighbours_key_and_any_originals_key()
    {
        var mine = await StageAsync(WorkspaceA, 0);
        var theirs = await StageAsync(WorkspaceB, 0);
        var theirWeb = await AddRenditionAsync(WorkspaceB, theirs, MediaRenditionPurpose.Web);
        var myOriginal = (await RowAsync(mine)).ObjectKey;
        var theirOriginal = (await RowAsync(theirs)).ObjectKey;

        await using var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            WorkspaceA, "workspace-a", Guid.NewGuid(), WorkspaceRole.Owner, "acct");
        var gateway = scope.ServiceProvider.GetRequiredService<IMediaRenditionObjectGateway>();
        var token = TestContext.Current.CancellationToken;

        // The exact key, handed to the wrong workspace's gateway. Finding a key is not authorization.
        Assert.False(gateway.Owns(theirWeb));
        Assert.Null(await gateway.OpenReadAsync(theirWeb, token));
        Assert.False(await gateway.DeleteAsync(theirWeb, token));

        // Nor can a rendition be written beside a neighbour's picture.
        var write = await gateway.PutAsync(
            theirOriginal, MediaRenditionPurpose.Thumbnail, new MemoryStream([1, 2, 3]), "image/jpeg", 16, token);
        Assert.Equal(MediaRenditionObjectWriteOutcome.NotOwned, write.Outcome);

        // An original's key is not a rendition's, even this workspace's own: this gateway cannot remove one.
        Assert.False(gateway.Owns(myOriginal));
        Assert.False(await gateway.DeleteAsync(myOriginal, token));

        Assert.Contains(theirWeb, _store.Keys);
        Assert.Contains(myOriginal, _store.Keys);
        Assert.Equal(3, _store.Keys.Count);
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private sealed record LibraryVersion(Guid AssetId, string ObjectKey, string Checksum);

    /// <summary>
    /// A rendition of a staged image: its object beside the image's, and its row. Returns the object key,
    /// or the key it would have had when a reason says there are no bytes.
    /// </summary>
    private async Task<string> AddRenditionAsync(
        Guid workspaceId, Guid imageId, MediaRenditionPurpose purpose, MediaRenditionReason? notCompressed = null)
    {
        var image = await RowAsync(imageId);

        return await AddRenditionRowAsync(
            workspaceId, image.ObjectKey, image.ContentChecksum, purpose, notCompressed,
            rendition => rendition.GeneratedImageId = imageId);
    }

    private Task<string> AddLibraryRenditionAsync(Guid workspaceId, LibraryVersion version, MediaRenditionPurpose purpose) =>
        AddRenditionRowAsync(
            workspaceId, version.ObjectKey, version.Checksum, purpose, notCompressed: null,
            rendition =>
            {
                rendition.MediaAssetId = version.AssetId;
                rendition.MediaAssetVersionNumber = 1;
            });

    private async Task<string> AddRenditionRowAsync(
        Guid workspaceId,
        string sourceObjectKey,
        string sourceChecksum,
        MediaRenditionPurpose purpose,
        MediaRenditionReason? notCompressed,
        Action<MediaRendition> source)
    {
        Assert.True(MediaRenditionObjectKey.TryFor(sourceObjectKey, purpose, out var key));

        var rendition = new MediaRendition
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Purpose = purpose,
            Status = notCompressed is null ? MediaRenditionStatus.Ready : MediaRenditionStatus.NotCompressed,
            NotCompressedReason = notCompressed,
            SourceContentChecksum = sourceChecksum,
            CreatedAt = _clock.UtcNow,
        };

        source(rendition);

        if (notCompressed is null)
        {
            var write = await _store.PutAsync(
                key.Container,
                key.ObjectKey,
                new MemoryStream([0xFF, 0xD8, 0xFF, (byte)purpose, 0xFF, 0xD9]),
                "image/jpeg",
                MediaPolicy.ImageMaxBytes,
                TestContext.Current.CancellationToken);

            rendition.ObjectKey = key.ObjectKey;
            rendition.MediaType = "image/jpeg";
            rendition.SizeBytes = write.Object!.SizeBytes;
            rendition.ContentChecksum = write.Object.ContentChecksum;
            rendition.Width = 4;
            rendition.Height = 3;
        }

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.MediaRenditions.Add(rendition);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return key.ObjectKey;
    }

    /// <summary>A library asset with one stored version, optionally already deleted.</summary>
    private async Task<LibraryVersion> AddLibraryVersionAsync(Guid workspaceId, DateTimeOffset? deletedAt)
    {
        var assetId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var key = MediaAssetObjectKey.For(workspaceId, assetId, 1);

        var write = await _store.PutAsync(
            MediaAssetObjectKey.Container,
            key,
            new MemoryStream(Brand.BrandSourceSampleFiles.Png()),
            "image/png",
            MediaPolicy.ImageMaxBytes,
            TestContext.Current.CancellationToken);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var now = _clock.UtcNow;

        db.MediaAssets.Add(new MediaAsset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            Title = "Soda bread hero",
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = 1,
            DeletedAt = deletedAt,
            DeletedByMembershipId = deletedAt is null ? null : actor,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        });

        db.MediaAssetVersions.Add(new MediaAssetVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            MediaAssetId = assetId,
            VersionNumber = 1,
            MediaType = "image/png",
            SizeBytes = write.Object!.SizeBytes,
            Width = 4,
            Height = 3,
            ContentChecksum = write.Object.ContentChecksum,
            ObjectKey = key,
            Source = MediaAssetVersionSource.Upload,
            CreatedByMembershipId = actor,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new LibraryVersion(assetId, key, write.Object.ContentChecksum);
    }

    /// <summary>Reads past the filters, because a test asserting isolation has to see both sides of it.</summary>
    private async Task<IReadOnlyList<MediaRendition>> RenditionsAsync(Guid workspaceId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .MediaRenditions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(rendition => rendition.WorkspaceId == workspaceId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
