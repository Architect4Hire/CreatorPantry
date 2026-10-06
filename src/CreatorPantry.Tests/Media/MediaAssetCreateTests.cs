using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Content;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
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
/// Creating a DAM asset from an upload or from a staged image (DAM-001): what commits together, what is
/// rolled back together, and what a replay does.
/// </summary>
/// <remarks>
/// <para>
/// The interesting assertions are the negative ones. An asset, its version, its tags, its recipe link,
/// its prompt record and the staged image's status change are one unit, and the object written for them
/// is the only thing outside it — so every failure case here checks that <em>nothing</em> is left: no
/// row, no object, and a staged image still staged.
/// </para>
/// <para>
/// Over SQLite with an in-memory object store and a movable clock. SQLite honours transactions, which is
/// the only thing these tests need from a database engine; the schema itself is exercised against SQL
/// Server by the fixtures that run migrations.
/// </para>
/// </remarks>
public sealed class MediaAssetCreateTests : IDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryPrivateObjectStore _store = new();
    private readonly ServiceProvider _provider;

    public MediaAssetCreateTests()
    {
        _connection.Open();
        _store.Now = _clock.UtcNow;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Idempotency:FingerprintKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock>(_clock)
            .AddApplicationTime()
            .AddTenancy()
            .AddAudit()
            .AddIdempotency(configuration)
            .AddOutbox()
            .AddDistributedMemoryCache()
            .AddApplicationCache()
            .AddMeasurementModule()
            .AddVocabularyModule()
            .AddIngredientModule()
            .AddRecipesModule(configuration)
            .AddContentModule()
            .AddMediaModule()

            // The AI module's narrow proposal lookup, and only it: the content module resolves a prompt's
            // provenance pin through this and needs nothing else from that module.
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiProposalLookupBusiness, AiProposalLookupBusiness>()
            .AddScoped<IAiProposalLookupFacade, AiProposalLookupFacade>()
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

    // ---- the two ways an asset is made -----------------------------------------------------------------

    [Fact]
    public async Task An_upload_becomes_an_asset_whose_version_describes_the_bytes_that_arrived()
    {
        var bytes = BrandSourceSampleFiles.Jpeg(width: 640, height: 400);

        var created = await UploadAsync(WorkspaceA, bytes, fileName: "hero.jpeg");

        Assert.Equal(MediaAssetKind.Original, created.Kind);
        Assert.Equal(1, created.CurrentVersionNumber);

        // Established from the signature, not from the filename or a declared content type.
        Assert.Equal("image/jpeg", created.MediaType);
        Assert.Equal(640, created.Width);
        Assert.Equal(400, created.Height);
        Assert.Null(created.SourceGeneratedImageId);

        var version = await VersionAsync(created.Id);
        Assert.Equal(MediaAssetVersionSource.Upload, version.Source);
        Assert.Equal("hero.jpeg", version.OriginalFileName);
        Assert.StartsWith("sha256:", version.ContentChecksum, StringComparison.Ordinal);

        // One object, under this workspace's DAM prefix, named by the server.
        Assert.Equal(MediaAssetObjectKey.For(WorkspaceA, created.Id, 1), Assert.Single(_store.Keys));
    }

    [Fact]
    public async Task Keeping_a_staged_image_copies_it_and_records_where_it_came_from()
    {
        var staged = await StageAsync(WorkspaceA);

        var created = await KeepAsync(WorkspaceA, staged.ImageId);

        Assert.Equal(MediaAssetKind.AiGenerated, created.Kind);
        Assert.Equal(staged.ImageId, created.SourceGeneratedImageId);

        var version = await VersionAsync(created.Id);
        Assert.Equal(MediaAssetVersionSource.GeneratedImage, version.Source);

        // The copy is the same bytes: the checksum the staged row recorded is what the DAM object carries.
        Assert.Equal(staged.Checksum, version.ContentChecksum);

        // The staged image is kept, which is what makes its staging copy redundant.
        Assert.Equal(GeneratedImageStatus.Kept, (await ImageAsync(staged.ImageId)).Status);

        // Two objects for now: the staging copy and the DAM copy. The next test is what reclaims the first.
        Assert.Equal(2, _store.Keys.Count);
        Assert.Contains(MediaAssetObjectKey.For(WorkspaceA, created.Id, 1), _store.Keys);
    }

    [Fact]
    public async Task Retention_reclaims_a_kept_images_staging_bytes_once_an_asset_owns_them()
    {
        var staged = await StageAsync(WorkspaceA);
        var created = await KeepAsync(WorkspaceA, staged.ImageId);

        await using var scope = _provider.CreateAsyncScope();
        await ResolveServiceAsync(scope, WorkspaceA);

        var summary = await scope.ServiceProvider.GetRequiredService<IStagedImageFacade>()
            .RunRetentionAsync(Token);

        // This is "copy or move" settled: nothing was removed until a committed asset owned a copy.
        Assert.Equal(1, summary.Purged);
        Assert.NotNull((await ImageAsync(staged.ImageId)).ObjectDeletedAt);

        // The DAM copy is untouched, which is the half that matters.
        Assert.Equal(MediaAssetObjectKey.For(WorkspaceA, created.Id, 1), Assert.Single(_store.Keys));
    }

    [Fact]
    public async Task A_prompt_sent_with_the_asset_commits_naming_it()
    {
        var staged = await StageAsync(WorkspaceA);

        var created = await KeepAsync(
            WorkspaceA,
            staged.ImageId,
            prompt: new PromptRecordSaveInput
            {
                ChannelKey = "instagram",
                ImageKind = PromptImageKind.Hero,
                Text = "a bowl of soup on a wooden table",
                Source = PromptRecordSource.Manual,
            });

        Assert.NotNull(created.PromptRecordId);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var prompt = await db.PromptRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(record => record.Id == created.PromptRecordId, Token);

        // Filled in by the server from inside the transaction: the request could not have named an asset
        // that did not exist yet, and an immutable row cannot gain the id afterwards.
        Assert.Equal(created.Id, prompt.DamAssetId);
    }

    // ---- rollback ---------------------------------------------------------------------------------------

    /// <summary>
    /// The case 12.3a's note is about: a refused prompt must take the asset down with it.
    /// </summary>
    /// <remarks>
    /// A prompt record is immutable, so one committed beside an asset that then failed could only be
    /// erased. Writing it inside the transaction is what makes "both or neither" true, and this is the
    /// test that would fail if it were ever moved after the commit.
    /// </remarks>
    [Fact]
    public async Task A_refused_prompt_leaves_no_asset_no_object_and_a_still_staged_image()
    {
        var staged = await StageAsync(WorkspaceA);

        var result = await KeepRawAsync(
            WorkspaceA,
            staged.ImageId,

            // A channel key the catalog does not know: refused by the prompt seam, after the asset rows
            // have already been written inside the transaction.
            prompt: new PromptRecordSaveInput
            {
                ChannelKey = "not-a-channel",
                ImageKind = PromptImageKind.Hero,
                Text = "a bowl of soup on a wooden table",
                Source = PromptRecordSource.Manual,
            });

        Assert.False(result.Result.Succeeded);

        Assert.Empty(await AssetsAsync(WorkspaceA));
        Assert.Empty(await PromptsAsync(WorkspaceA));

        // Only the staging object remains: the DAM object written for the rolled-back rows was removed.
        Assert.Equal(staged.ObjectKey, Assert.Single(_store.Keys));

        // And the image a creator has not yet kept is still theirs to choose.
        Assert.Equal(GeneratedImageStatus.Staged, (await ImageAsync(staged.ImageId)).Status);
    }

    [Fact]
    public async Task Unreachable_storage_writes_nothing_at_all()
    {
        _store.Unavailable = true;

        var result = await UploadRawAsync(WorkspaceA, BrandSourceSampleFiles.Png());

        Assert.False(result.Result.Succeeded);
        Assert.Equal("media.asset.unavailable", result.Result.Error!.Code);
        Assert.Empty(await AssetsAsync(WorkspaceA));
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_refused_before_anything_is_stored()
    {
        var result = await UploadRawAsync(WorkspaceA, BrandSourceSampleFiles.Executable(), "photo.png");

        Assert.False(result.Result.Succeeded);
        Assert.Equal("media.asset.unprocessable", result.Result.Error!.Code);

        // Whatever it was called. The signature decides, and nothing reached storage.
        Assert.Empty(_store.Keys);
        Assert.Empty(await AssetsAsync(WorkspaceA));
    }

    // ---- replay -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Keeping_one_image_twice_returns_one_asset_without_an_idempotency_key()
    {
        var staged = await StageAsync(WorkspaceA);

        var first = await KeepAsync(WorkspaceA, staged.ImageId);
        var second = await KeepAsync(WorkspaceA, staged.ImageId);

        // The natural guard: an image is kept once, so the repeat hands back what the first call made.
        Assert.Equal(first.Id, second.Id);
        Assert.Single(await AssetsAsync(WorkspaceA));

        // And no second object was written for it.
        Assert.Equal(2, _store.Keys.Count);
    }

    [Fact]
    public async Task An_upload_replayed_under_one_key_creates_one_asset()
    {
        var bytes = BrandSourceSampleFiles.Png();

        var first = await UploadAsync(WorkspaceA, bytes, idempotencyKey: "same-key");
        var second = await UploadAsync(WorkspaceA, bytes, idempotencyKey: "same-key");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await AssetsAsync(WorkspaceA));
        Assert.Single(_store.Keys);
    }

    [Fact]
    public async Task An_upload_replayed_without_a_key_creates_two_assets()
    {
        var bytes = BrandSourceSampleFiles.Png();

        var first = await UploadAsync(WorkspaceA, bytes);
        var second = await UploadAsync(WorkspaceA, bytes);

        // Documented rather than defended against: a creator may legitimately upload one photograph twice
        // as two assets, so there is no natural key to use instead and the header is the only guard.
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, (await AssetsAsync(WorkspaceA)).Count);
    }

    // ---- isolation --------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_workspaces_staged_image_cannot_be_kept()
    {
        var theirs = await StageAsync(WorkspaceB);

        var result = await KeepRawAsync(WorkspaceA, theirs.ImageId);

        Assert.False(result.Result.Succeeded);

        // The same answer an unknown id gets, so neither discloses the other (tenancy.md).
        Assert.Equal("media.asset.not_found", result.Result.Error!.Code);

        Assert.Empty(await AssetsAsync(WorkspaceA));
        Assert.Equal(GeneratedImageStatus.Staged, (await ImageAsync(theirs.ImageId)).Status);
        Assert.Equal(theirs.ObjectKey, Assert.Single(_store.Keys));
    }

    [Fact]
    public async Task An_asset_cannot_be_linked_to_another_workspaces_recipe()
    {
        var theirRecipe = await SeedRecipeAsync(WorkspaceB);

        var result = await UploadRawAsync(
            WorkspaceA,
            BrandSourceSampleFiles.Png(),
            recipeLink: new MediaAssetRecipeLinkInput(theirRecipe, "Theirs"));

        Assert.False(result.Result.Succeeded);
        Assert.Equal("media.asset.invalid_request", result.Result.Error!.Code);

        // Refused before anything was written, so the composite foreign key never had to catch it.
        Assert.Empty(_store.Keys);
        Assert.Empty(await AssetsAsync(WorkspaceA));
    }

    [Fact]
    public async Task An_asset_cannot_carry_another_workspaces_tag()
    {
        var theirTag = await SeedTagAsync(WorkspaceB);

        var result = await UploadRawAsync(
            WorkspaceA,
            BrandSourceSampleFiles.Png(),
            tagIds: [theirTag]);

        Assert.False(result.Result.Succeeded);
        Assert.Equal("media.asset.invalid_request", result.Result.Error!.Code);
        Assert.Empty(await AssetsAsync(WorkspaceA));
    }

    [Fact]
    public async Task An_asset_may_link_its_own_workspaces_recipe_and_tag()
    {
        var recipeId = await SeedRecipeAsync(WorkspaceA);
        var tagId = await SeedTagAsync(WorkspaceA);

        // The positive control for the two refusals above: they fail because of ownership, not because
        // links and tags are broken.
        var created = await UploadAsync(
            WorkspaceA,
            BrandSourceSampleFiles.Png(),
            recipeLink: new MediaAssetRecipeLinkInput(recipeId, "Hero"),
            tagIds: [tagId]);

        Assert.Equal(recipeId, created.RecipeId);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.Equal(
            tagId,
            (await db.MediaAssetTags.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(tag => tag.MediaAssetId == created.Id, Token)).WorkspaceTagId);

        Assert.Equal(
            created.Id,
            (await db.RecipeAssetLinks.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(link => link.RecipeId == recipeId, Token)).MediaAssetId);
    }

    [Fact]
    public async Task A_viewer_cannot_add_to_the_library()
    {
        var result = await UploadRawAsync(WorkspaceA, BrandSourceSampleFiles.Png(), role: WorkspaceRole.Viewer);

        Assert.False(result.Result.Succeeded);
        Assert.Equal("media.asset.forbidden", result.Result.Error!.Code);
        Assert.Empty(_store.Keys);
    }

    [Fact]
    public async Task Two_workspaces_each_keep_their_own_image_and_see_only_their_own_asset()
    {
        var mine = await StageAsync(WorkspaceA);
        var theirs = await StageAsync(WorkspaceB);

        var a = await KeepAsync(WorkspaceA, mine.ImageId);
        var b = await KeepAsync(WorkspaceB, theirs.ImageId);

        Assert.Equal([a.Id], (await AssetsAsync(WorkspaceA)).Select(asset => asset.Id));
        Assert.Equal([b.Id], (await AssetsAsync(WorkspaceB)).Select(asset => asset.Id));

        // Each asset's object is under its own workspace's prefix.
        Assert.Contains(MediaAssetObjectKey.For(WorkspaceA, a.Id, 1), _store.Keys);
        Assert.Contains(MediaAssetObjectKey.For(WorkspaceB, b.Id, 1), _store.Keys);
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record StagedImage(Guid ImageId, string ObjectKey, string Checksum);

    private AsyncServiceScope ScopeFor(Guid workspaceId, WorkspaceRole role = WorkspaceRole.Owner)
    {
        var scope = _provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "workspace-a" : "workspace-b",
            Guid.NewGuid(),
            role,
            "acct");

        return scope;
    }

    private static async Task ResolveServiceAsync(AsyncServiceScope scope, Guid workspaceId) =>
        Assert.True(
            (await scope.ServiceProvider.GetRequiredService<Domain.Modules.Tenancy.Facade.IWorkspaceResolutionFacade>()
                .ResolveForServiceAsync(workspaceId, Token)).Succeeded);

    private async Task<MediaAssetServiceModel> UploadAsync(
        Guid workspaceId,
        byte[] bytes,
        string fileName = "photo.png",
        string? idempotencyKey = null,
        MediaAssetRecipeLinkInput? recipeLink = null,
        IReadOnlyList<Guid>? tagIds = null)
    {
        var outcome = await UploadRawAsync(workspaceId, bytes, fileName, idempotencyKey, recipeLink, tagIds);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);

        return outcome.Result.Value!;
    }

    private async Task<Domain.Managers.Idempotency.IdempotentOutcome<MediaAssetServiceModel>> UploadRawAsync(
        Guid workspaceId,
        byte[] bytes,
        string fileName = "photo.png",
        string? idempotencyKey = null,
        MediaAssetRecipeLinkInput? recipeLink = null,
        IReadOnlyList<Guid>? tagIds = null,
        WorkspaceRole role = WorkspaceRole.Owner)
    {
        await using var scope = ScopeFor(workspaceId, role);
        using var content = new MemoryStream(bytes);

        return await scope.ServiceProvider.GetRequiredService<IMediaAssetFacade>().CreateFromUploadAsync(
            new MediaAssetUpload(
                content,
                fileName,
                new MediaAssetMetadataInput { Title = "A photograph", WorkspaceTagIds = tagIds },
                recipeLink,
                Prompt: null,
                "user"),
            idempotencyKey,
            Token);
    }

    private async Task<MediaAssetServiceModel> KeepAsync(
        Guid workspaceId, Guid imageId, PromptRecordSaveInput? prompt = null)
    {
        var outcome = await KeepRawAsync(workspaceId, imageId, prompt);

        Assert.True(outcome.Result.Succeeded, outcome.Result.Error?.Message);

        return outcome.Result.Value!;
    }

    private async Task<Domain.Managers.Idempotency.IdempotentOutcome<MediaAssetServiceModel>> KeepRawAsync(
        Guid workspaceId, Guid imageId, PromptRecordSaveInput? prompt = null)
    {
        await using var scope = ScopeFor(workspaceId);

        return await scope.ServiceProvider.GetRequiredService<IMediaAssetFacade>()
            .CreateFromGeneratedImageAsync(
                new MediaAssetFromGeneratedImage(
                    imageId,
                    new MediaAssetMetadataInput { Title = "Kept image" },
                    RecipeLink: null,
                    prompt,
                    "user"),
                idempotencyKey: null,
                Token);
    }

    /// <summary>One staged image with real bytes, the way 12.7's worker leaves one.</summary>
    private async Task<StagedImage> StageAsync(Guid workspaceId)
    {
        var operationId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var key = GeneratedImageObjectKey.For(workspaceId, operationId, 0);

        var write = await _store.PutAsync(
            GeneratedImageObjectKey.Container,
            key,
            new MemoryStream(BrandSourceSampleFiles.Png(width: 32, height: 24)),
            "image/png",
            MediaPolicy.ImageMaxBytes,
            Token);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var now = _clock.UtcNow;

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId,
            WorkspaceId = workspaceId,
            Status = GeneratedImageOperationStatus.Succeeded,
            PromptText = "a bowl of soup on a wooden table",
            VariantCount = 1,
            IdempotencyKey = operationId.ToString("N"),
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = now,
            StatusChangedAt = now,
            AvailableAt = now,
        });

        db.GeneratedImages.Add(new GeneratedImage
        {
            Id = imageId,
            WorkspaceId = workspaceId,
            GeneratedImageOperationId = operationId,
            VariantIndex = 0,
            Status = GeneratedImageStatus.Staged,
            ObjectKey = key,
            MediaType = "image/png",
            Width = 32,
            Height = 24,
            SizeBytes = write.Object!.SizeBytes,
            ContentChecksum = write.Object.ContentChecksum,
            ProviderName = "fake",
            ModelName = "fake-image-1",
            RetentionExpiresAt = now + MediaPolicy.StagedImageTimeToLive,
            CreatedAt = now,
            StatusChangedAt = now,
        });

        await db.SaveChangesAsync(Token);

        return new StagedImage(imageId, key, write.Object.ContentChecksum);
    }

    private async Task<Guid> SeedRecipeAsync(Guid workspaceId)
    {
        await using var scope = ScopeFor(workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var recipe = new Domain.Modules.Recipes.Data.Entities.Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Olive oil cake",
            Status = Domain.Modules.Recipes.Managers.RecipeStatus.Draft,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
        };

        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(Token);

        return recipe.Id;
    }

    private async Task<Guid> SeedTagAsync(Guid workspaceId)
    {
        await using var scope = ScopeFor(workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var tag = new Domain.Modules.Recipes.Data.Entities.WorkspaceTag
        {
            Id = Guid.NewGuid(),
            Name = "Weeknight",
            NormalizedName = "weeknight",
            CreatedAt = _clock.UtcNow,
        };

        db.WorkspaceTags.Add(tag);
        await db.SaveChangesAsync(Token);

        return tag.Id;
    }

    /// <summary>
    /// Reads past the filters, because a test asserting isolation has to see both sides of it.
    /// </summary>
    private async Task<IReadOnlyList<MediaAsset>> AssetsAsync(Guid workspaceId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .MediaAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => asset.WorkspaceId == workspaceId)
            .ToListAsync(Token);
    }

    /// <inheritdoc cref="AssetsAsync"/>
    private async Task<IReadOnlyList<PromptRecord>> PromptsAsync(Guid workspaceId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PromptRecords
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(record => record.WorkspaceId == workspaceId)
            .ToListAsync(Token);
    }

    /// <inheritdoc cref="AssetsAsync"/>
    private async Task<MediaAssetVersion> VersionAsync(Guid assetId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .MediaAssetVersions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(version => version.MediaAssetId == assetId, Token);
    }

    /// <inheritdoc cref="AssetsAsync"/>
    private async Task<GeneratedImage> ImageAsync(Guid imageId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .GeneratedImages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(image => image.Id == imageId, Token);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
