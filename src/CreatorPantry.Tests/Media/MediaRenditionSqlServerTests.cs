using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The rules about renditions that the schema holds, against the real engine (AF.5.4).
/// </summary>
/// <remarks>
/// On SQL Server because that is where they have to be true. The migration creating the table is applied
/// by the fixture, so a second cascade path into it — which SQLite accepts and SQL Server refuses — would
/// fail every test here before one of them ran. Filtered unique indexes and check constraints are likewise
/// only as good as the engine that enforces them.
/// </remarks>
public sealed class MediaRenditionSqlServerTests(SqlServerMediaFixture fixture)
    : IClassFixture<SqlServerMediaFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SqlServerMediaFixture.Now;

    private static Guid WorkspaceA => SqlServerMediaFixture.WorkspaceA;

    private static Guid WorkspaceB => SqlServerMediaFixture.WorkspaceB;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var scope = fixture.ScopeFor(WorkspaceA);

        await SqlServerMediaFixture.Db(scope).Database.ExecuteSqlRawAsync("DELETE FROM MediaRenditions;", Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_staged_image_has_one_rendition_per_purpose()
    {
        var image = await AddImageAsync(WorkspaceA);

        await SaveAsync(WorkspaceA, Ready(MediaRenditionPurpose.Web, generatedImageId: image));
        await SaveAsync(WorkspaceA, Ready(MediaRenditionPurpose.Thumbnail, generatedImageId: image));

        // A retried job, or a "not compressed" outcome arriving for a slot that is already filled.
        await RefusedAsync(WorkspaceA, Ready(MediaRenditionPurpose.Web, generatedImageId: image));
        await RefusedAsync(WorkspaceA, NotCompressed(MediaRenditionPurpose.Thumbnail, generatedImageId: image));
    }

    [Fact]
    public async Task A_library_version_has_one_rendition_per_purpose()
    {
        var asset = await AddVersionAsync(WorkspaceA);

        await SaveAsync(WorkspaceA, NotCompressed(MediaRenditionPurpose.Web, mediaAssetId: asset));
        await SaveAsync(WorkspaceA, Ready(MediaRenditionPurpose.Thumbnail, mediaAssetId: asset));

        await RefusedAsync(WorkspaceA, Ready(MediaRenditionPurpose.Web, mediaAssetId: asset));
        await RefusedAsync(WorkspaceA, Ready(MediaRenditionPurpose.Thumbnail, mediaAssetId: asset));
    }

    [Fact]
    public async Task Two_workspaces_hold_the_same_purposes_without_colliding_or_seeing_each_other()
    {
        var imageA = await AddImageAsync(WorkspaceA);
        var imageB = await AddImageAsync(WorkspaceB);

        foreach (var purpose in new[] { MediaRenditionPurpose.Web, MediaRenditionPurpose.Thumbnail })
        {
            await SaveAsync(WorkspaceA, Ready(purpose, generatedImageId: imageA));
            await SaveAsync(WorkspaceB, Ready(purpose, generatedImageId: imageB));
        }

        await using var a = fixture.ScopeFor(WorkspaceA);
        await using var b = fixture.ScopeFor(WorkspaceB);
        var seenByA = await SqlServerMediaFixture.Db(a).MediaRenditions.AsNoTracking().ToListAsync(Ct);
        var seenByB = await SqlServerMediaFixture.Db(b).MediaRenditions.AsNoTracking().ToListAsync(Ct);

        // The query filter, with no predicate of the test's own.
        Assert.Equal(2, seenByA.Count);
        Assert.Equal(2, seenByB.Count);
        Assert.All(seenByA, rendition => Assert.Equal(imageA, rendition.GeneratedImageId));
        Assert.All(seenByB, rendition => Assert.Equal(imageB, rendition.GeneratedImageId));
    }

    [Fact]
    public async Task A_rendition_cannot_name_another_workspaces_picture()
    {
        var theirImage = await AddImageAsync(WorkspaceB);
        var theirAsset = await AddVersionAsync(WorkspaceB);

        // The foreign keys carry the workspace, so the ids alone are not enough to point across.
        await RefusedAsync(WorkspaceA, Ready(MediaRenditionPurpose.Web, generatedImageId: theirImage));
        await RefusedAsync(WorkspaceA, Ready(MediaRenditionPurpose.Web, mediaAssetId: theirAsset));
    }

    [Fact]
    public async Task A_rendition_has_exactly_one_source()
    {
        var image = await AddImageAsync(WorkspaceA);
        var asset = await AddVersionAsync(WorkspaceA);

        await RefusedAsync(WorkspaceA, Ready(MediaRenditionPurpose.Web));
        await RefusedAsync(WorkspaceA, Ready(MediaRenditionPurpose.Web, generatedImageId: image, mediaAssetId: asset));
    }

    [Fact]
    public async Task A_row_describes_stored_bytes_or_says_why_there_are_none_and_never_half_of_each()
    {
        var image = await AddImageAsync(WorkspaceA);

        // Ready, with a byte fact missing.
        await RefusedAsync(WorkspaceA, With(Ready(MediaRenditionPurpose.Web, generatedImageId: image), r => r.ObjectKey = null));
        await RefusedAsync(WorkspaceA, With(Ready(MediaRenditionPurpose.Web, generatedImageId: image), r => r.ContentChecksum = null));
        await RefusedAsync(WorkspaceA, With(Ready(MediaRenditionPurpose.Web, generatedImageId: image), r => r.SizeBytes = 0));
        await RefusedAsync(WorkspaceA, With(Ready(MediaRenditionPurpose.Web, generatedImageId: image), r => r.Width = 0));

        // Ready, yet claiming a reason; and not compressed, yet claiming bytes or giving no reason.
        await RefusedAsync(WorkspaceA, With(
            Ready(MediaRenditionPurpose.Web, generatedImageId: image), r => r.NotCompressedReason = MediaRenditionReason.Corrupt));
        await RefusedAsync(WorkspaceA, With(
            Ready(MediaRenditionPurpose.Web, generatedImageId: image), r => r.Status = MediaRenditionStatus.NotCompressed));
        await RefusedAsync(WorkspaceA, With(
            NotCompressed(MediaRenditionPurpose.Web, generatedImageId: image), r => r.NotCompressedReason = null));

        // Undeclared values.
        await RefusedAsync(WorkspaceA, Ready(MediaRenditionPurpose.Unspecified, generatedImageId: image));
        await RefusedAsync(WorkspaceA, With(
            NotCompressed(MediaRenditionPurpose.Web, generatedImageId: image), r => r.NotCompressedReason = MediaRenditionReason.Unspecified));
        await RefusedAsync(WorkspaceA, With(
            Ready(MediaRenditionPurpose.Web, generatedImageId: image), r => r.SourceContentChecksum = "  "));
    }

    [Fact]
    public async Task Every_declared_reason_is_storable_and_the_next_number_is_not()
    {
        // The check constraint lists the reasons by number, so a reason added to the enum without a
        // migration would be refused in production and nowhere else. RetriesExhausted arrived that way round.
        foreach (var reason in Enum.GetValues<MediaRenditionReason>().Where(reason => reason != MediaRenditionReason.Unspecified))
        {
            var image = await AddImageAsync(WorkspaceA);

            await SaveAsync(WorkspaceA, With(
                NotCompressed(MediaRenditionPurpose.Web, generatedImageId: image), r => r.NotCompressedReason = reason));
        }

        var undeclared = (MediaRenditionReason)(Enum.GetValues<MediaRenditionReason>().Max(reason => (int)reason) + 1);

        await RefusedAsync(WorkspaceA, With(
            NotCompressed(MediaRenditionPurpose.Web, generatedImageId: await AddImageAsync(WorkspaceA)),
            r => r.NotCompressedReason = undeclared));
    }

    [Fact]
    public async Task Two_rows_cannot_name_the_same_object()
    {
        var first = await AddImageAsync(WorkspaceA);
        var second = await AddImageAsync(WorkspaceA);
        var taken = Ready(MediaRenditionPurpose.Web, generatedImageId: first);

        await SaveAsync(WorkspaceA, taken);
        await RefusedAsync(
            WorkspaceA, With(Ready(MediaRenditionPurpose.Web, generatedImageId: second), r => r.ObjectKey = taken.ObjectKey));

        // Any number of rows may have no object at all: the index is over the keys that exist.
        await SaveAsync(WorkspaceA, NotCompressed(MediaRenditionPurpose.Thumbnail, generatedImageId: first));
        await SaveAsync(WorkspaceA, NotCompressed(MediaRenditionPurpose.Thumbnail, generatedImageId: second));
    }

    [Fact]
    public async Task A_source_row_cannot_be_removed_from_under_its_renditions()
    {
        var image = await AddImageAsync(WorkspaceA);
        await SaveAsync(WorkspaceA, Ready(MediaRenditionPurpose.Web, generatedImageId: image));

        await using var scope = fixture.ScopeFor(WorkspaceA);

        // Restrict, not cascade: a cascade would delete the row and strand the bytes it was the only
        // record of. Nothing in the application deletes a staged image's row; this is the schema agreeing.
        await Assert.ThrowsAnyAsync<Exception>(() => SqlServerMediaFixture.Db(scope).Database
            .ExecuteSqlAsync($"DELETE FROM GeneratedImages WHERE Id = {image}", Ct));

        Assert.Single(await SqlServerMediaFixture.Db(scope).MediaRenditions.AsNoTracking().ToListAsync(Ct));
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private static MediaRendition Ready(
        MediaRenditionPurpose purpose, Guid? generatedImageId = null, Guid? mediaAssetId = null) => new()
    {
        Id = Guid.NewGuid(),
        GeneratedImageId = generatedImageId,
        MediaAssetId = mediaAssetId,
        MediaAssetVersionNumber = mediaAssetId is null ? null : 1,
        Purpose = purpose,
        Status = MediaRenditionStatus.Ready,
        SourceContentChecksum = "sha256:source",
        ObjectKey = $"renditions/{Guid.NewGuid():N}",
        MediaType = "image/jpeg",
        SizeBytes = 1234,
        Width = 16,
        Height = 12,
        ContentChecksum = "sha256:rendition",
        CreatedAt = Now,
    };

    private static MediaRendition NotCompressed(
        MediaRenditionPurpose purpose, Guid? generatedImageId = null, Guid? mediaAssetId = null) => new()
    {
        Id = Guid.NewGuid(),
        GeneratedImageId = generatedImageId,
        MediaAssetId = mediaAssetId,
        MediaAssetVersionNumber = mediaAssetId is null ? null : 1,
        Purpose = purpose,
        Status = MediaRenditionStatus.NotCompressed,
        NotCompressedReason = MediaRenditionReason.UnreadableFormat,
        SourceContentChecksum = "sha256:source",
        CreatedAt = Now,
    };

    private static MediaRendition With(MediaRendition rendition, Action<MediaRendition> change)
    {
        change(rendition);

        return rendition;
    }

    private async Task SaveAsync(Guid workspaceId, MediaRendition rendition)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var db = SqlServerMediaFixture.Db(scope);

        db.MediaRenditions.Add(rendition);
        await db.SaveChangesAsync(Ct);
    }

    private async Task RefusedAsync(Guid workspaceId, MediaRendition rendition) =>
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(workspaceId, rendition));

    private async Task<Guid> AddImageAsync(Guid workspaceId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var db = SqlServerMediaFixture.Db(scope);
        var operationId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId,
            Status = GeneratedImageOperationStatus.Succeeded,
            PromptText = "a bowl of soup on a wooden table",
            VariantCount = 1,
            IdempotencyKey = operationId.ToString("N"),
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = Now,
            StatusChangedAt = Now,
            AvailableAt = Now,
        });

        db.GeneratedImages.Add(new GeneratedImage
        {
            Id = imageId,
            GeneratedImageOperationId = operationId,
            VariantIndex = 0,
            Status = GeneratedImageStatus.Staged,
            ObjectKey = GeneratedImageObjectKey.For(workspaceId, operationId, 0),
            MediaType = "image/png",
            Width = 32,
            Height = 24,
            SizeBytes = 4096,
            ContentChecksum = "sha256:source",
            ProviderName = "fake",
            ModelName = "fake-image-1",
            RetentionExpiresAt = Now + MediaPolicy.StagedImageTimeToLive,
            CreatedAt = Now,
            StatusChangedAt = Now,
        });

        await db.SaveChangesAsync(Ct);

        return imageId;
    }

    private async Task<Guid> AddVersionAsync(Guid workspaceId)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        var db = SqlServerMediaFixture.Db(scope);
        var assetId = Guid.NewGuid();
        var actor = Guid.NewGuid();

        db.MediaAssets.Add(new MediaAsset
        {
            Id = assetId,
            Title = "Soda bread hero",
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = 1,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        });

        db.MediaAssetVersions.Add(new MediaAssetVersion
        {
            Id = Guid.NewGuid(),
            MediaAssetId = assetId,
            VersionNumber = 1,
            MediaType = "image/png",
            SizeBytes = 4096,
            Width = 32,
            Height = 24,
            ContentChecksum = "sha256:source",
            ObjectKey = MediaAssetObjectKey.For(workspaceId, assetId, 1),
            Source = MediaAssetVersionSource.Upload,
            CreatedByMembershipId = actor,
            CreatedAt = Now,
        });

        await db.SaveChangesAsync(Ct);

        return assetId;
    }
}
