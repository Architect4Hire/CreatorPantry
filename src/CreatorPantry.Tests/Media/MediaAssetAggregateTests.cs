using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The DAM aggregate's schema: what a media asset, its versions, its utilization and its tags may hold,
/// and what the database refuses outright (DATA-003).
/// </summary>
/// <remarks>
/// <para>
/// Mostly about refusals, because that is what an aggregate's configuration is for. The interesting half
/// is the three link tables in other modules — <c>RecipeAssetLink</c>, <c>BrandAssetLink</c> and
/// <c>TestAttachmentLink</c> — which have carried an unconstrained <c>MediaAssetId</c> since Phase 2 and
/// now cannot name another workspace's asset. Before this migration every one of those tests seeded a
/// random GUID and the database accepted it.
/// </para>
/// <para>
/// Over SQLite, which creates the check constraints verbatim and enforces the foreign keys and unique
/// indexes. The migration itself is exercised against SQL Server by every fixture in the suite that uses
/// one.
/// </para>
/// </remarks>
public sealed class MediaAssetAggregateTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    // ---- the root -------------------------------------------------------------------------------------

    [Fact]
    public async Task An_asset_round_trips_with_its_version()
    {
        var assetId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Sourdough on linen");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var asset = await db.MediaAssets
            .Include(candidate => candidate.Versions)
            .FirstAsync(candidate => candidate.Id == assetId, Token);

        Assert.Equal("Sourdough on linen", asset.Title);
        Assert.Equal(MediaAssetKind.Original, asset.Kind);
        Assert.Equal(1, asset.CurrentVersionNumber);
        Assert.Null(asset.DeletedAt);

        var version = Assert.Single(asset.Versions);
        Assert.Equal(1, version.VersionNumber);
        Assert.Equal(MediaAssetVersionSource.Upload, version.Source);
        Assert.Null(version.SourceGeneratedImageId);

        // The concurrency token DAM-004's patch checks exists and is populated.
        Assert.NotEmpty(asset.RowVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_asset_must_have_a_title(string title)
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        db.MediaAssets.Add(Asset(RecipeAggregateFixture.WorkspaceA, title));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task An_asset_must_say_what_kind_it_is()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var asset = Asset(RecipeAggregateFixture.WorkspaceA, "Unspecified");
        asset.Kind = MediaAssetKind.Unspecified;
        db.MediaAssets.Add(asset);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task A_tombstone_names_both_who_and_when_or_neither()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // A deletion nobody can account for is the opposite of what DAM-005 asks a soft delete to be.
        var halfDeleted = Asset(RecipeAggregateFixture.WorkspaceA, "Half deleted");
        halfDeleted.DeletedAt = RecipeAggregateFixture.Now;
        db.MediaAssets.Add(halfDeleted);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task Two_workspaces_may_hold_assets_with_the_same_title_and_see_only_their_own()
    {
        var mine = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Hero shot");
        var theirs = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "Hero shot");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var visible = await db.MediaAssets.Select(asset => asset.Id).ToListAsync(Token);

        Assert.Contains(mine, visible);
        Assert.DoesNotContain(theirs, visible);

        // Not forbidden — invisible. The query filter is what makes the two answers one (tenancy.md).
        Assert.Null(await db.MediaAssets.FirstOrDefaultAsync(asset => asset.Id == theirs, Token));
    }

    // ---- versions -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_version_cannot_be_edited_or_deleted_once_written()
    {
        var assetId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Immutable");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var version = await db.MediaAssetVersions.FirstAsync(candidate => candidate.MediaAssetId == assetId, Token);

        version.SizeBytes = 999;

        // DAM-010's immutable original, enforced by the interceptor rather than by remembering.
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Token));

        db.Entry(version).State = EntityState.Unchanged;
        db.MediaAssetVersions.Remove(version);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task Two_versions_of_one_asset_cannot_share_a_number()
    {
        var assetId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Versioned");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // What DAM-010 means by "concurrent uploads cannot share a version number": the index decides,
        // not the application's belief about its own ordering.
        db.MediaAssetVersions.Add(Version(RecipeAggregateFixture.WorkspaceA, assetId, versionNumber: 1));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task Two_assets_may_each_have_a_version_one()
    {
        var first = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "First");
        var second = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Second");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var numbers = await db.MediaAssetVersions
            .Where(version => version.MediaAssetId == first || version.MediaAssetId == second)
            .Select(version => version.VersionNumber)
            .ToListAsync(Token);

        Assert.Equal([1, 1], numbers);
    }

    [Fact]
    public async Task Two_versions_cannot_name_the_same_object()
    {
        var assetId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Shared bytes");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var existing = await db.MediaAssetVersions.AsNoTracking()
            .FirstAsync(candidate => candidate.MediaAssetId == assetId, Token);

        // Two rows naming one object would let deleting a version take away another version's file.
        var second = Version(RecipeAggregateFixture.WorkspaceA, assetId, versionNumber: 2);
        second.ObjectKey = existing.ObjectKey;
        db.MediaAssetVersions.Add(second);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task A_version_cannot_name_another_workspaces_asset()
    {
        var theirs = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "Theirs");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        db.MediaAssetVersions.Add(Version(RecipeAggregateFixture.WorkspaceA, theirs, versionNumber: 2));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    [Theory]
    [InlineData(MediaAssetVersionSource.Upload, true)]
    [InlineData(MediaAssetVersionSource.GeneratedImage, false)]
    public async Task A_versions_source_and_its_generated_image_must_agree(
        MediaAssetVersionSource source, bool nameAnImage)
    {
        var assetId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Provenance");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // An upload that names an image, or a generated version that names none: either way the row would
        // be claiming a provenance it has no evidence for.
        var version = Version(RecipeAggregateFixture.WorkspaceA, assetId, versionNumber: 2);
        version.Source = source;
        version.SourceGeneratedImageId = nameAnImage ? Guid.NewGuid() : null;
        db.MediaAssetVersions.Add(version);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public async Task A_version_describes_real_pixels(int width, int height)
    {
        var assetId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Dimensions");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var version = Version(RecipeAggregateFixture.WorkspaceA, assetId, versionNumber: 2);
        version.Width = width;
        version.Height = height;
        db.MediaAssetVersions.Add(version);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    // ---- the three links that had no key until now -----------------------------------------------------

    /// <summary>
    /// The guarantee this migration exists for.
    /// </summary>
    /// <remarks>
    /// <c>RecipeAssetLink.MediaAssetId</c> had no foreign key at all from Phase 2 until now, so one
    /// workspace's recipe could name another's photograph and the database would store it. Only the write
    /// seam stood in the way. These three cases are what the composite key buys.
    /// </remarks>
    [Fact]
    public async Task A_recipe_cannot_link_another_workspaces_asset()
    {
        var theirs = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "Theirs");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var recipe = RecipeAggregateFixture.NewRecipe("Mine", theirs);
        db.Recipes.Add(recipe);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    /// <inheritdoc cref="A_recipe_cannot_link_another_workspaces_asset"/>
    [Fact]
    public async Task A_brand_profile_cannot_show_another_workspaces_logo()
    {
        var theirs = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "Their logo");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        AddProfileWithLogo(db, theirs);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    /// <inheritdoc cref="A_recipe_cannot_link_another_workspaces_asset"/>
    [Fact]
    public async Task A_test_run_cannot_attach_another_workspaces_photograph()
    {
        var theirs = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "Their photo");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        AddRunWithAttachment(db, theirs);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task A_recipe_may_link_its_own_workspaces_asset()
    {
        // The other half: the key refuses a neighbour's asset and accepts this workspace's, so the
        // refusals above are about ownership rather than about links being broken.
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        db.Recipes.Add(RecipeAggregateFixture.NewRecipe("Mine", RecipeAggregateFixture.MediaAssetIdA));

        await db.SaveChangesAsync(Token);

        Assert.Equal(
            RecipeAggregateFixture.MediaAssetIdA,
            (await db.RecipeAssetLinks.AsNoTracking().FirstAsync(Token)).MediaAssetId);
    }

    /// <summary>
    /// The positive control for <see cref="A_brand_profile_cannot_show_another_workspaces_logo"/>.
    /// </summary>
    /// <remarks>
    /// Without it that test passes on any <c>DbUpdateException</c> at all — a missing required field on the
    /// profile would do — and would still look green if the foreign key were never the reason.
    /// </remarks>
    [Fact]
    public async Task A_brand_profile_may_show_its_own_workspaces_logo()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        AddProfileWithLogo(db, RecipeAggregateFixture.MediaAssetIdA);

        await db.SaveChangesAsync(Token);

        Assert.Equal(
            RecipeAggregateFixture.MediaAssetIdA,
            (await db.BrandAssetLinks.AsNoTracking().FirstAsync(Token)).MediaAssetId);
    }

    /// <inheritdoc cref="A_brand_profile_may_show_its_own_workspaces_logo"/>
    [Fact]
    public async Task A_test_run_may_attach_its_own_workspaces_photograph()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        AddRunWithAttachment(db, RecipeAggregateFixture.MediaAssetIdA);

        await db.SaveChangesAsync(Token);

        Assert.Equal(
            RecipeAggregateFixture.MediaAssetIdA,
            (await db.TestAttachmentLinks.AsNoTracking().FirstAsync(Token)).MediaAssetId);
    }

    [Fact]
    public async Task A_version_cannot_name_another_workspaces_generated_image()
    {
        var theirImage = await SeedGeneratedImageAsync(RecipeAggregateFixture.WorkspaceB);
        var assetId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Mine");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // Workspace-paired, so a neighbour's staged image is unrepresentable rather than merely refused.
        // The Source_Agrees check is satisfied here, so the foreign key is the only thing left to fail.
        var version = Version(RecipeAggregateFixture.WorkspaceA, assetId, versionNumber: 2);
        version.Source = MediaAssetVersionSource.GeneratedImage;
        version.SourceGeneratedImageId = theirImage;
        db.MediaAssetVersions.Add(version);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    /// <inheritdoc cref="A_brand_profile_may_show_its_own_workspaces_logo"/>
    [Fact]
    public async Task A_version_may_name_its_own_workspaces_generated_image()
    {
        var myImage = await SeedGeneratedImageAsync(RecipeAggregateFixture.WorkspaceA);
        var assetId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Mine");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        var version = Version(RecipeAggregateFixture.WorkspaceA, assetId, versionNumber: 2);
        version.Source = MediaAssetVersionSource.GeneratedImage;
        version.SourceGeneratedImageId = myImage;
        db.MediaAssetVersions.Add(version);

        await db.SaveChangesAsync(Token);

        // The provenance a kept image leaves behind, which survives 12.8 removing the staged bytes.
        Assert.Equal(
            myImage,
            (await db.MediaAssetVersions.AsNoTracking()
                .FirstAsync(candidate => candidate.VersionNumber == 2, Token)).SourceGeneratedImageId);
    }

    // ---- utilization and tags --------------------------------------------------------------------------

    [Fact]
    public async Task Utilization_is_a_history_rather_than_a_status()
    {
        var assetId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Reused");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        // The same asset going out twice is two rows, not an overwrite.
        db.MediaAssetUtilizations.AddRange(
            Utilization(RecipeAggregateFixture.WorkspaceA, assetId, new DateOnly(2026, 10, 5)),
            Utilization(RecipeAggregateFixture.WorkspaceA, assetId, new DateOnly(2026, 11, 2)));

        await db.SaveChangesAsync(Token);

        Assert.Equal(2, await db.MediaAssetUtilizations.CountAsync(Token));
    }

    [Fact]
    public async Task Utilization_cannot_name_another_workspaces_asset()
    {
        var theirs = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "Theirs");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        db.MediaAssetUtilizations.Add(
            Utilization(RecipeAggregateFixture.WorkspaceA, theirs, new DateOnly(2026, 10, 5)));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task An_asset_carries_a_tag_from_its_own_workspaces_vocabulary()
    {
        var assetId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Tagged");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        db.MediaAssetTags.Add(new MediaAssetTag
        {
            MediaAssetId = assetId,
            WorkspaceTagId = RecipeAggregateFixture.TagIdA,
        });

        await db.SaveChangesAsync(Token);

        // The shared WorkspaceTag vocabulary the recipe library uses, not a second tag table.
        Assert.Equal(
            RecipeAggregateFixture.TagIdA,
            (await db.MediaAssetTags.AsNoTracking().FirstAsync(Token)).WorkspaceTagId);
    }

    [Fact]
    public async Task An_asset_cannot_carry_another_workspaces_tag()
    {
        var assetId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Tagged");

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        db.MediaAssetTags.Add(new MediaAssetTag
        {
            MediaAssetId = assetId,
            WorkspaceTagId = RecipeAggregateFixture.TagIdB,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task An_asset_carries_a_tag_once_or_not_at_all()
    {
        var assetId = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Tagged twice");

        await using (var first = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA))
        {
            var firstDb = RecipeAggregateFixture.Db(first);
            firstDb.MediaAssetTags.Add(
                new MediaAssetTag { MediaAssetId = assetId, WorkspaceTagId = RecipeAggregateFixture.TagIdA });
            await firstDb.SaveChangesAsync(Token);
        }

        // A second scope, because two of these in one change tracker is refused before the database ever
        // sees them. The pair being the primary key is what makes the repeat a no-op rather than a second
        // row, however it arrives.
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        db.MediaAssetTags.Add(
            new MediaAssetTag { MediaAssetId = assetId, WorkspaceTagId = RecipeAggregateFixture.TagIdA });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Token));
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<Guid> SeedAsync(Guid workspaceId, string title)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);

        var asset = Asset(workspaceId, title);
        db.MediaAssets.Add(asset);
        db.MediaAssetVersions.Add(Version(workspaceId, asset.Id, versionNumber: 1));

        await db.SaveChangesAsync(Token);

        return asset.Id;
    }

    /// <summary>A brand profile with one logo, so the pair of logo tests differ only in whose asset it is.</summary>
    private static void AddProfileWithLogo(CreatorPantryDbContext db, Guid mediaAssetId)
    {
        var profile = new BrandProfile
        {
            Id = Guid.NewGuid(),
            BrandName = "Mine",
            CreatedAt = RecipeAggregateFixture.Now,
            UpdatedAt = RecipeAggregateFixture.Now,
        };

        db.BrandProfiles.Add(profile);
        db.BrandAssetLinks.Add(new BrandAssetLink
        {
            Id = Guid.NewGuid(),
            BrandProfileId = profile.Id,
            MediaAssetId = mediaAssetId,
            Role = BrandAssetRole.PrimaryLogo,
            SortOrder = 0,
        });
    }

    /// <inheritdoc cref="AddProfileWithLogo"/>
    private static void AddRunWithAttachment(CreatorPantryDbContext db, Guid mediaAssetId)
    {
        var recipe = RecipeAggregateFixture.NewRecipe("Mine");
        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            VersionNumber = RecipePolicy.FirstVersionNumber,
            Source = RecipeVersionSource.CreatorEdit,
            Readiness = RecipeVersionReadiness.Draft,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = RecipeAggregateFixture.Now,
            SnapshotSchemaVersion = 1,
        };
        var run = new RecipeTestRun
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            RecipeVersionId = version.Id,
            TestedByMembershipId = Guid.NewGuid(),
            TestedAt = RecipeAggregateFixture.Now,
            CreatedAt = RecipeAggregateFixture.Now,
            UpdatedAt = RecipeAggregateFixture.Now,
        };

        db.Recipes.Add(recipe);
        db.RecipeVersions.Add(version);
        db.RecipeTestRuns.Add(run);
        db.TestAttachmentLinks.Add(new TestAttachmentLink
        {
            Id = Guid.NewGuid(),
            RecipeTestRunId = run.Id,
            MediaAssetId = mediaAssetId,
            SortOrder = 0,
        });
    }

    /// <summary>One staged generated image, so a version has something real to have come from.</summary>
    private async Task<Guid> SeedGeneratedImageAsync(Guid workspaceId)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);

        var operationId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId,
            WorkspaceId = workspaceId,
            Status = GeneratedImageOperationStatus.Succeeded,
            PromptText = "a bowl of soup on a wooden table",
            VariantCount = 1,
            IdempotencyKey = operationId.ToString("N"),
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = RecipeAggregateFixture.Now,
            StatusChangedAt = RecipeAggregateFixture.Now,
            AvailableAt = RecipeAggregateFixture.Now,
        });

        db.GeneratedImages.Add(new GeneratedImage
        {
            Id = imageId,
            WorkspaceId = workspaceId,
            GeneratedImageOperationId = operationId,
            VariantIndex = 0,
            Status = GeneratedImageStatus.Kept,
            ObjectKey = GeneratedImageObjectKey.For(workspaceId, operationId, 0),
            MediaType = "image/png",
            Width = 16,
            Height = 12,
            SizeBytes = 512,
            ContentChecksum = $"sha256:{Guid.NewGuid():N}",
            ProviderName = "fake",
            ModelName = "fake-image-1",
            RetentionExpiresAt = RecipeAggregateFixture.Now + MediaPolicy.StagedImageTimeToLive,
            CreatedAt = RecipeAggregateFixture.Now,
            StatusChangedAt = RecipeAggregateFixture.Now,
        });

        await db.SaveChangesAsync(Token);

        return imageId;
    }

    private static MediaAsset Asset(Guid workspaceId, string title)
    {
        var asset = SeededMediaAsset.For(workspaceId, at: RecipeAggregateFixture.Now);
        asset.Title = title;

        return asset;
    }

    private static MediaAssetVersion Version(Guid workspaceId, Guid assetId, int versionNumber) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            MediaAssetId = assetId,
            VersionNumber = versionNumber,
            MediaType = "image/png",
            SizeBytes = 2048,
            Width = 1200,
            Height = 800,
            ContentChecksum = $"sha256:{Guid.NewGuid():N}",
            ObjectKey = $"workspaces/{workspaceId:N}/dam/{Guid.NewGuid():N}/{versionNumber}",
            Source = MediaAssetVersionSource.Upload,
            CreatedByMembershipId = Guid.NewGuid(),
            CreatedAt = RecipeAggregateFixture.Now,
        };

    private static MediaAssetUtilization Utilization(Guid workspaceId, Guid assetId, DateOnly on) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            MediaAssetId = assetId,
            PlatformKey = "instagram",
            UtilizedOn = on,
            UtilizedDay = on.DayOfWeek,
            LoggedByMembershipId = Guid.NewGuid(),
            CreatedAt = RecipeAggregateFixture.Now,
        };
}
