using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// Keeping a staged image carries the renditions it already has to the library version made from it,
/// rather than making them again (AF.5.4) — and never fails, or keeps a wrong one, because of them.
/// </summary>
public sealed partial class MediaAssetCreateTests
{
    [Fact]
    public async Task Keeping_a_staged_image_carries_its_renditions_to_the_new_version()
    {
        var staged = await StageAsync(WorkspaceA);
        var web = await AddStagedRenditionAsync(staged, MediaRenditionPurpose.Web);
        await AddStagedRenditionAsync(staged, MediaRenditionPurpose.Thumbnail, MediaRenditionReason.NotSmaller);

        var created = await KeepAsync(WorkspaceA, staged.ImageId);

        var version = await VersionAsync(created.Id);
        var carried = await RenditionsOfAsync(created.Id);
        var carriedWeb = Assert.Single(carried, rendition => rendition.Purpose == MediaRenditionPurpose.Web);
        var carriedThumbnail = Assert.Single(carried, rendition => rendition.Purpose == MediaRenditionPurpose.Thumbnail);
        Assert.Equal(2, carried.Count);

        // The same bytes, beside the new version, described by what the store measured of the copy.
        Assert.Equal(MediaRenditionStatus.Ready, carriedWeb.Status);
        Assert.Equal(MediaRenditionObjectKey.For(version.ObjectKey, MediaRenditionPurpose.Web), carriedWeb.ObjectKey);
        Assert.Equal(web.ContentChecksum, carriedWeb.ContentChecksum);
        Assert.Equal(web.SizeBytes, carriedWeb.SizeBytes);
        Assert.Equal((web.Width, web.Height), (carriedWeb.Width, carriedWeb.Height));
        Assert.Equal("image/jpeg", carriedWeb.MediaType);
        Assert.Contains(carriedWeb.ObjectKey, _store.Keys);

        // Made from the picture the version holds, which is what makes carrying it legitimate.
        Assert.Equal(version.ContentChecksum, carriedWeb.SourceContentChecksum);

        // An outcome is carried too, so "not compressed" is not worked out a second time.
        Assert.Equal(MediaRenditionStatus.NotCompressed, carriedThumbnail.Status);
        Assert.Equal(MediaRenditionReason.NotSmaller, carriedThumbnail.NotCompressedReason);
        Assert.Null(carriedThumbnail.ObjectKey);

        // The staged side is untouched by the keep, exactly as the staged original is.
        Assert.Equal(2, (await RenditionsOfImageAsync(staged.ImageId)).Count);
        Assert.Contains(web.ObjectKey, _store.Keys);
    }

    [Fact]
    public async Task Retention_then_removes_the_staged_renditions_and_leaves_the_carried_ones()
    {
        var staged = await StageAsync(WorkspaceA);
        var web = await AddStagedRenditionAsync(staged, MediaRenditionPurpose.Web);
        var created = await KeepAsync(WorkspaceA, staged.ImageId);
        var version = await VersionAsync(created.Id);

        await using var scope = _provider.CreateAsyncScope();
        await ResolveServiceAsync(scope, WorkspaceA);

        var summary = await scope.ServiceProvider.GetRequiredService<IStagedImageFacade>().RunRetentionAsync(Token);

        Assert.Equal(1, summary.Renditions);
        Assert.Empty(await RenditionsOfImageAsync(staged.ImageId));
        Assert.DoesNotContain(web.ObjectKey, _store.Keys);

        // What is left is the library's: the version and the rendition that came with it.
        Assert.Single(await RenditionsOfAsync(created.Id));
        Assert.Equal(
            new[] { version.ObjectKey, MediaRenditionObjectKey.For(version.ObjectKey, MediaRenditionPurpose.Web) }.Order(),
            _store.Keys.Order());
    }

    [Fact]
    public async Task A_rendition_that_cannot_be_carried_is_left_behind_and_the_image_is_still_kept()
    {
        var staged = await StageAsync(WorkspaceA);

        // A row whose stored bytes are not the ones it describes: the copy will not match it.
        await AddStagedRenditionAsync(
            staged, MediaRenditionPurpose.Web, alter: rendition => rendition.ContentChecksum = "sha256:" + new string('0', 64));

        // And one made from some other picture's bytes.
        await AddStagedRenditionAsync(
            staged, MediaRenditionPurpose.Thumbnail, alter: rendition => rendition.SourceContentChecksum = "sha256:" + new string('1', 64));

        var created = await KeepAsync(WorkspaceA, staged.ImageId);
        var version = await VersionAsync(created.Id);

        // Kept, with no renditions and nothing stored beside the version for one. Making them is the
        // rendition job's work; describing bytes the row does not match would be a lie about the picture.
        Assert.Equal(GeneratedImageStatus.Kept, (await ImageAsync(staged.ImageId)).Status);
        Assert.Empty(await RenditionsOfAsync(created.Id));
        Assert.DoesNotContain(_store.Keys, key => key.StartsWith(version.ObjectKey + "-", StringComparison.Ordinal));
        Assert.Contains(version.ObjectKey, _store.Keys);
    }

    [Fact]
    public async Task A_rendition_whose_bytes_are_missing_is_not_carried()
    {
        var staged = await StageAsync(WorkspaceA);
        var web = await AddStagedRenditionAsync(staged, MediaRenditionPurpose.Web);

        Assert.True(await _store.DeleteAsync(GeneratedImageObjectKey.Container, web.ObjectKey!, Token));

        var created = await KeepAsync(WorkspaceA, staged.ImageId);

        Assert.Empty(await RenditionsOfAsync(created.Id));
    }

    [Fact]
    public async Task One_workspaces_keep_carries_only_its_own_renditions()
    {
        var mine = await StageAsync(WorkspaceA);
        var theirs = await StageAsync(WorkspaceB);
        await AddStagedRenditionAsync(mine, MediaRenditionPurpose.Web);
        var theirWeb = await AddStagedRenditionAsync(theirs, MediaRenditionPurpose.Web);

        var created = await KeepAsync(WorkspaceA, mine.ImageId);

        // A's version has A's rendition. B's row and bytes are exactly where they were.
        Assert.Equal(WorkspaceA, Assert.Single(await RenditionsOfAsync(created.Id)).WorkspaceId);
        Assert.Equal(theirWeb.Id, Assert.Single(await RenditionsOfImageAsync(theirs.ImageId)).Id);
        Assert.Contains(theirWeb.ObjectKey, _store.Keys);
        Assert.DoesNotContain(
            _store.Keys,
            key => key.Contains("/media-assets/", StringComparison.Ordinal)
                && key.StartsWith(MediaAssetObjectKey.PrefixFor(WorkspaceB), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Keeping_a_neighbours_image_is_refused_and_carries_nothing_of_theirs()
    {
        var theirs = await StageAsync(WorkspaceB);
        var theirWeb = await AddStagedRenditionAsync(theirs, MediaRenditionPurpose.Web);
        var before = _store.Keys.Order().ToArray();

        // B's image id, handed to A. The id is real and its renditions are ready to copy.
        var outcome = await KeepRawAsync(WorkspaceA, theirs.ImageId);

        Assert.False(outcome.Result.Succeeded);
        Assert.Empty(await AssetsAsync(WorkspaceA));

        // Nothing was read out of B's storage into A's, and nothing of B's moved.
        Assert.Equal(before, _store.Keys.Order());
        Assert.Equal(theirWeb.Id, Assert.Single(await RenditionsOfImageAsync(theirs.ImageId)).Id);
        Assert.Equal(GeneratedImageStatus.Staged, (await ImageAsync(theirs.ImageId)).Status);
    }

    [Fact]
    public async Task One_workspaces_retention_leaves_a_neighbours_removable_renditions_for_the_neighbour()
    {
        var mine = await StageAsync(WorkspaceA);
        var theirs = await StageAsync(WorkspaceB);
        var myWeb = await AddStagedRenditionAsync(mine, MediaRenditionPurpose.Web);
        var theirWeb = await AddStagedRenditionAsync(theirs, MediaRenditionPurpose.Web);

        // Both declined, so both workspaces have renditions that are due to go. The two staged pictures
        // are the same bytes, so every checksum on B's row is one A's row has too.
        await DeclineAsync(mine.ImageId, theirs.ImageId);

        await using var scope = _provider.CreateAsyncScope();
        await ResolveServiceAsync(scope, WorkspaceA);

        // A's pass, and only A's: not the sweep that would go on to visit B.
        var summary = await scope.ServiceProvider.GetRequiredService<IStagedImageFacade>().RunRetentionAsync(Token);

        Assert.Equal(1, summary.Renditions);
        Assert.Empty(await RenditionsOfImageAsync(mine.ImageId));
        Assert.DoesNotContain(myWeb.ObjectKey, _store.Keys);

        // B's is just as removable and is still there, row and bytes, because it is B's to remove.
        Assert.Equal(theirWeb.Id, Assert.Single(await RenditionsOfImageAsync(theirs.ImageId)).Id);
        Assert.Contains(theirWeb.ObjectKey, _store.Keys);
        Assert.Contains(theirs.ObjectKey, _store.Keys);
    }

    [Fact]
    public async Task An_upload_and_a_keep_each_ask_for_the_new_versions_renditions_as_they_commit()
    {
        var uploaded = await UploadAsync(WorkspaceA, Brand.BrandSourceSampleFiles.Png(width: 32, height: 24));
        var staged = await StageAsync(WorkspaceA);
        var kept = await KeepAsync(WorkspaceA, staged.ImageId);

        await using var scope = _provider.CreateAsyncScope();
        var requests = (await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
                .OutboxMessages
                .AsNoTracking()
                .Where(message => message.Type == MediaRenditionRequestedEvent.MessageType)
                .ToListAsync(Token))
            .Select(message => MediaRenditionRequestedEvent.TryParse(message.PayloadJson)!)
            .ToList();

        // One per version, written in the transaction that made it, naming this workspace and ids only.
        Assert.Equal(
            new[] { uploaded.Id, kept.Id }.Order(),
            requests.Select(request => request.MediaAssetId!.Value).Order());
        Assert.All(requests, request =>
        {
            Assert.Equal(WorkspaceA, request.WorkspaceId);
            Assert.Equal(1, request.MediaAssetVersionNumber);
            Assert.Null(request.GeneratedImageId);
        });
    }

    [Fact]
    public async Task A_keep_that_is_refused_asks_for_nothing()
    {
        var theirs = await StageAsync(WorkspaceB);

        Assert.False((await KeepRawAsync(WorkspaceA, theirs.ImageId)).Result.Succeeded);

        await using var scope = _provider.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .OutboxMessages
            .AsNoTracking()
            .Where(message => message.Type == MediaRenditionRequestedEvent.MessageType)
            .ToListAsync(Token));
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private async Task DeclineAsync(params Guid[] imageIds)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        foreach (var image in await db.GeneratedImages
            .IgnoreQueryFilters()
            .Where(image => imageIds.Contains(image.Id))
            .ToListAsync(Token))
        {
            image.Status = GeneratedImageStatus.Rejected;
            image.StatusChangedAt = _clock.UtcNow;
        }

        await db.SaveChangesAsync(Token);
    }

    /// <summary>A rendition of a staged image: its object beside the image's, and its row.</summary>
    private async Task<MediaRendition> AddStagedRenditionAsync(
        StagedImage staged,
        MediaRenditionPurpose purpose,
        MediaRenditionReason? notCompressed = null,
        Action<MediaRendition>? alter = null)
    {
        Assert.True(MediaRenditionObjectKey.TryFor(staged.ObjectKey, purpose, out var key));

        var rendition = new MediaRendition
        {
            Id = Guid.NewGuid(),
            WorkspaceId = key.WorkspaceId,
            GeneratedImageId = staged.ImageId,
            Purpose = purpose,
            Status = notCompressed is null ? MediaRenditionStatus.Ready : MediaRenditionStatus.NotCompressed,
            NotCompressedReason = notCompressed,
            SourceContentChecksum = staged.Checksum,
            CreatedAt = _clock.UtcNow,
        };

        if (notCompressed is null)
        {
            var write = await _store.PutAsync(
                key.Container,
                key.ObjectKey,
                new MemoryStream([0xFF, 0xD8, 0xFF, (byte)purpose, 0x10, 0x20, 0xFF, 0xD9]),
                "image/jpeg",
                MediaPolicy.ImageMaxBytes,
                Token);

            rendition.ObjectKey = key.ObjectKey;
            rendition.MediaType = "image/jpeg";
            rendition.SizeBytes = write.Object!.SizeBytes;
            rendition.ContentChecksum = write.Object.ContentChecksum;
            rendition.Width = 16;
            rendition.Height = 12;
        }

        alter?.Invoke(rendition);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.MediaRenditions.Add(rendition);
        await db.SaveChangesAsync(Token);

        return rendition;
    }

    /// <inheritdoc cref="AssetsAsync"/>
    private async Task<IReadOnlyList<MediaRendition>> RenditionsOfAsync(Guid assetId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .MediaRenditions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(rendition => rendition.MediaAssetId == assetId)
            .ToListAsync(Token);
    }

    /// <inheritdoc cref="AssetsAsync"/>
    private async Task<IReadOnlyList<MediaRendition>> RenditionsOfImageAsync(Guid imageId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .MediaRenditions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(rendition => rendition.GeneratedImageId == imageId)
            .ToListAsync(Token);
    }
}
