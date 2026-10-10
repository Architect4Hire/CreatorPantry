using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The five byte-serving routes through the real Gateway, once renditions exist (B-28, AF.5.6): the smaller
/// picture by default where the contract says so, the original on request, and a header saying which.
/// </summary>
/// <remarks>
/// Each test covers one picture across every selector rather than one selector at a time, because a gateway
/// fixture is the expensive thing here and the selectors are only meaningful beside each other. What a
/// rendition's bytes decode to is <c>MediaRenditionJobTests</c>' business; these only need to tell three
/// payloads apart.
/// </remarks>
public sealed class MediaRenditionEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Original = BrandSourceSampleFiles.Png(width: 16, height: 12);

    private static readonly byte[] WebBytes = [0xFF, 0xD8, 0xFF, 0x01, 0x77, 0x65, 0x62, 0xFF, 0xD9];

    private static readonly byte[] ThumbnailBytes = [0xFF, 0xD8, 0xFF, 0x02, 0x74, 0xFF, 0xD9];

    private readonly InMemoryPrivateObjectStore _store = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- staged images ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("preview", "inline")]
    [InlineData("content", "attachment")]
    public async Task A_staged_image_is_served_web_size_by_default_and_as_staged_on_request(string route, string disposition)
    {
        var image = await StageAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA);
        string Url(string? rendition = null) => Staged(_fixture.WorkspaceA, image.Id, route, rendition);

        // Before the job has run there is only the picture itself, and every selector says so.
        foreach (var rendition in new string?[] { null, "web", "thumbnail", "original" })
        {
            await AssertServedAsync(await client.GetAsync(Url(rendition), Ct), "original", Original, "image/png", disposition, "generated-1.png");
        }

        await AddRenditionAsync(_fixture.WorkspaceA, image.ObjectKey, MediaRenditionPurpose.Web, WebBytes, generatedImageId: image.Id);
        await AddRenditionAsync(_fixture.WorkspaceA, image.ObjectKey, MediaRenditionPurpose.Thumbnail, ThumbnailBytes, generatedImageId: image.Id);

        // The same address, and now fewer bytes: only what is sent changed.
        await AssertServedAsync(await client.GetAsync(Url(), Ct), "web", WebBytes, "image/jpeg", disposition, "generated-1.jpg");
        await AssertServedAsync(await client.GetAsync(Url("web"), Ct), "web", WebBytes, "image/jpeg", disposition, "generated-1.jpg");
        await AssertServedAsync(await client.GetAsync(Url("thumbnail"), Ct), "thumbnail", ThumbnailBytes, "image/jpeg", disposition, "generated-1.jpg");
        await AssertServedAsync(await client.GetAsync(Url("original"), Ct), "original", Original, "image/png", disposition, "generated-1.png");

        // Staged bytes are private and never stored by anything in between, whichever they are.
        Assert.True((await client.GetAsync(Url(), Ct)).Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task A_staged_images_tag_is_the_served_bytes_own_and_an_unknown_selector_reads_nothing()
    {
        var image = await StageAsync(_fixture.WorkspaceA);
        await AddRenditionAsync(_fixture.WorkspaceA, image.ObjectKey, MediaRenditionPurpose.Web, WebBytes, generatedImageId: image.Id);
        using var client = await SignInAsync(_fixture.WorkspaceA);
        string Url(string? rendition = null) => Staged(_fixture.WorkspaceA, image.Id, "preview", rendition);

        var web = await client.GetAsync(Url(), Ct);
        var original = await client.GetAsync(Url("original"), Ct);

        // Two representations at one address have two tags, and each answers only its own.
        Assert.NotEqual(web.Headers.ETag, original.Headers.ETag);

        var revalidated = await client.SendAsync(
            HttpMethod.Get, Url(), body: null, new Dictionary<string, string> { ["If-None-Match"] = web.Headers.ETag!.ToString() }, Ct);
        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        Assert.Equal("web", Assert.Single(revalidated.Headers.GetValues(MediaRenditionSelector.HeaderName)));

        var crossed = await client.SendAsync(
            HttpMethod.Get, Url("original"), body: null, new Dictionary<string, string> { ["If-None-Match"] = web.Headers.ETag.ToString() }, Ct);
        Assert.Equal(HttpStatusCode.OK, crossed.StatusCode);

        // Not one of the three words, in any spelling: refused before anything is opened.
        foreach (var bad in new[] { "large", "WEB", "web,original", "original " })
        {
            var refused = await client.GetAsync(Url(bad), Ct);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(MediaErrorCodes.StagedImageInvalidRequest, await CodeAsync(refused));
        }

        // An empty value is an absent one, as the framework binds it: the route's own default.
        Assert.Equal("web", Assert.Single((await client.GetAsync(Url(string.Empty), Ct)).Headers.GetValues(MediaRenditionSelector.HeaderName)));

        Assert.Equal(0, _store.OpenReads);
    }

    [Fact]
    public async Task A_picture_recorded_as_not_compressed_is_served_as_staged_and_a_declined_one_is_not_served_at_all()
    {
        var uncompressed = await StageAsync(_fixture.WorkspaceA);
        await AddRenditionAsync(_fixture.WorkspaceA, uncompressed.ObjectKey, MediaRenditionPurpose.Web, bytes: null, generatedImageId: uncompressed.Id);

        var declined = await StageAsync(_fixture.WorkspaceA, GeneratedImageStatus.Rejected);
        await AddRenditionAsync(_fixture.WorkspaceA, declined.ObjectKey, MediaRenditionPurpose.Web, WebBytes, generatedImageId: declined.Id);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        await AssertServedAsync(
            await client.GetAsync(Staged(_fixture.WorkspaceA, uncompressed.Id, "preview"), Ct),
            "original", Original, "image/png", "inline", "generated-1.png");

        // Its rendition's bytes are still in storage until the sweep runs. A rendition is not a softer way
        // in than the picture it was made from.
        foreach (var rendition in new string?[] { null, "web", "thumbnail", "original" })
        {
            var response = await client.GetAsync(Staged(_fixture.WorkspaceA, declined.Id, "preview", rendition), Ct);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(MediaErrorCodes.StagedImageNotFound, await CodeAsync(response));
        }
    }

    // ---- library assets --------------------------------------------------------------------------------

    [Fact]
    public async Task A_library_asset_renders_web_size_by_default_and_downloads_the_original_by_default()
    {
        var asset = await AddAssetAsync(_fixture.WorkspaceA);
        using var client = await SignInAsync(_fixture.WorkspaceA);
        string Url(string route, string? rendition = null) => Asset(_fixture.WorkspaceA, asset.Id, route, rendition);

        // No renditions yet: everything is the stored version.
        await AssertServedAsync(await client.GetAsync(Url("content"), Ct), "original", Original, "image/png", "inline", null);
        await AssertServedAsync(await client.GetAsync(Url("download", "web"), Ct), "original", Original, "image/png", "attachment", "soda-bread-hero-v1.png");

        await AddRenditionAsync(_fixture.WorkspaceA, asset.ObjectKey, MediaRenditionPurpose.Web, WebBytes, mediaAssetId: asset.Id);
        await AddRenditionAsync(_fixture.WorkspaceA, asset.ObjectKey, MediaRenditionPurpose.Thumbnail, ThumbnailBytes, mediaAssetId: asset.Id);

        // The render route: the smaller picture unless the original is asked for.
        await AssertServedAsync(await client.GetAsync(Url("content"), Ct), "web", WebBytes, "image/jpeg", "inline", null);
        await AssertServedAsync(await client.GetAsync(Url("content", "thumbnail"), Ct), "thumbnail", ThumbnailBytes, "image/jpeg", "inline", null);
        await AssertServedAsync(await client.GetAsync(Url("content", "original"), Ct), "original", Original, "image/png", "inline", null);

        // The download routes: the original unless a rendition is asked for, which is named as what it is.
        foreach (var route in new[] { "download", "versions/1/download" })
        {
            await AssertServedAsync(await client.GetAsync(Url(route), Ct), "original", Original, "image/png", "attachment", "soda-bread-hero-v1.png");
            await AssertServedAsync(await client.GetAsync(Url(route, "original"), Ct), "original", Original, "image/png", "attachment", "soda-bread-hero-v1.png");
            await AssertServedAsync(await client.GetAsync(Url(route, "web"), Ct), "web", WebBytes, "image/jpeg", "attachment", "soda-bread-hero-v1-web.jpg");
        }

        // Private, revalidated every time, and never stored by a shared proxy — whichever bytes they are.
        var cache = (await client.GetAsync(Url("content"), Ct)).Headers.CacheControl!;
        Assert.True(cache.Private);
        Assert.True(cache.NoCache);

        var refused = await client.GetAsync(Url("content", "huge"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(MediaErrorCodes.AssetInvalidRequest, await CodeAsync(refused));
    }

    [Fact]
    public async Task The_models_state_each_renditions_size_beside_the_originals()
    {
        var image = await StageAsync(_fixture.WorkspaceA);
        var asset = await AddAssetAsync(_fixture.WorkspaceA);
        await AddRenditionAsync(_fixture.WorkspaceA, image.ObjectKey, MediaRenditionPurpose.Web, WebBytes, generatedImageId: image.Id);
        await AddRenditionAsync(_fixture.WorkspaceA, asset.ObjectKey, MediaRenditionPurpose.Web, WebBytes, mediaAssetId: asset.Id);
        await AddRenditionAsync(_fixture.WorkspaceA, asset.ObjectKey, MediaRenditionPurpose.Thumbnail, ThumbnailBytes, mediaAssetId: asset.Id);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        var operation = await ReadAsync(await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/generated-images/operations/{image.OperationId}", Ct));
        var staged = Assert.Single(operation.GetProperty("images").EnumerateArray());

        Assert.Equal(Original.Length, staged.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(WebBytes.Length, staged.GetProperty("webSizeBytes").GetInt64());

        // No thumbnail was made for this one, and the model says so by saying nothing.
        Assert.Equal(JsonValueKind.Null, staged.GetProperty("thumbnailSizeBytes").ValueKind);

        var detail = await ReadAsync(await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/dam-assets/{asset.Id:D}", Ct));
        var version = detail.GetProperty("currentVersion");

        Assert.Equal(Original.Length, version.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(WebBytes.Length, version.GetProperty("webSizeBytes").GetInt64());
        Assert.Equal(ThumbnailBytes.Length, version.GetProperty("thumbnailSizeBytes").GetInt64());

        // Sizes, and nothing that says where the bytes are.
        Assert.DoesNotContain("objectKey", operation.GetRawText() + detail.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- two workspaces --------------------------------------------------------------------------------

    [Fact]
    public async Task No_selector_reaches_another_workspaces_picture_or_its_renditions()
    {
        var theirImage = await StageAsync(_fixture.WorkspaceB);
        var theirAsset = await AddAssetAsync(_fixture.WorkspaceB);
        await AddRenditionAsync(_fixture.WorkspaceB, theirImage.ObjectKey, MediaRenditionPurpose.Web, WebBytes, generatedImageId: theirImage.Id);
        await AddRenditionAsync(_fixture.WorkspaceB, theirAsset.ObjectKey, MediaRenditionPurpose.Web, WebBytes, mediaAssetId: theirAsset.Id);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        foreach (var rendition in new string?[] { null, "web", "thumbnail", "original" })
        {
            // B's ids under A's slug, and B's ids under B's slug with A's session: neither is a way in.
            foreach (var workspace in new[] { _fixture.WorkspaceA, _fixture.WorkspaceB })
            {
                foreach (var url in new[]
                {
                    Staged(workspace, theirImage.Id, "preview", rendition),
                    Staged(workspace, theirImage.Id, "content", rendition),
                    Asset(workspace, theirAsset.Id, "content", rendition),
                    Asset(workspace, theirAsset.Id, "download", rendition),
                    Asset(workspace, theirAsset.Id, "versions/1/download", rendition),
                })
                {
                    var response = await client.GetAsync(url, Ct);

                    Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{url} answered {(int)response.StatusCode}");
                    Assert.False(response.Headers.Contains(MediaRenditionSelector.HeaderName));
                }
            }
        }

        // Nothing of B's was opened on A's behalf, and B still sees its own.
        Assert.Equal(0, _store.OpenReads);

        using var owner = await SignInAsync(_fixture.WorkspaceB);
        await AssertServedAsync(
            await owner.GetAsync(Asset(_fixture.WorkspaceB, theirAsset.Id, "content"), Ct), "web", WebBytes, "image/jpeg", "inline", null);
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private sealed record StagedPicture(Guid Id, Guid OperationId, string ObjectKey);

    private sealed record LibraryPicture(Guid Id, string ObjectKey);

    private static string Staged(SeededWorkspace workspace, Guid imageId, string route, string? rendition = null) =>
        $"/api/v1/workspaces/{workspace.Slug}/generated-images/{imageId}/{route}" + Query(rendition);

    private static string Asset(SeededWorkspace workspace, Guid assetId, string route, string? rendition = null) =>
        $"/api/v1/workspaces/{workspace.Slug}/dam-assets/{assetId:D}/{route}" + Query(rendition);

    private static string Query(string? rendition) =>
        rendition is null ? string.Empty : $"?{MediaRenditionSelector.QueryName}={Uri.EscapeDataString(rendition)}";

    /// <summary>Everything a response says about what it sent, checked against what was actually sent.</summary>
    private static async Task AssertServedAsync(
        HttpResponseMessage response, string rendition, byte[] bytes, string mediaType, string disposition, string? fileName)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(rendition, Assert.Single(response.Headers.GetValues(MediaRenditionSelector.HeaderName)));
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal(disposition, response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(fileName, response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace) =>
        _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    private AsyncServiceScope ScopeFor(SeededWorkspace workspace)
    {
        var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "seed");

        return scope;
    }

    private async Task<StagedPicture> StageAsync(
        SeededWorkspace workspace, GeneratedImageStatus status = GeneratedImageStatus.Staged)
    {
        var operationId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var key = GeneratedImageObjectKey.For(workspace.Id, operationId, 0);

        var write = await _store.PutAsync(
            GeneratedImageObjectKey.Container, key, new MemoryStream(Original), "image/png", MediaPolicy.ImageMaxBytes, Ct);

        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId,
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
            GeneratedImageOperationId = operationId,
            VariantIndex = 0,
            Status = status,
            ObjectKey = key,
            MediaType = "image/png",
            Width = 16,
            Height = 12,
            SizeBytes = write.Object!.SizeBytes,
            ContentChecksum = write.Object.ContentChecksum,
            ProviderName = "fake",
            ModelName = "fake-image-1",
            RetentionExpiresAt = now + MediaPolicy.StagedImageTimeToLive,
            CreatedAt = now,
            StatusChangedAt = now,
        });

        await db.SaveChangesAsync(Ct);

        return new StagedPicture(imageId, operationId, key);
    }

    private async Task<LibraryPicture> AddAssetAsync(SeededWorkspace workspace)
    {
        var assetId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var key = MediaAssetObjectKey.For(workspace.Id, assetId, 1);

        var write = await _store.PutAsync(
            MediaAssetObjectKey.Container, key, new MemoryStream(Original), "image/png", MediaPolicy.ImageMaxBytes, Ct);

        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var asset = new MediaAsset
        {
            Id = assetId,
            Title = "Soda bread hero",
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = 1,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        };

        asset.Versions.Add(new MediaAssetVersion
        {
            Id = Guid.NewGuid(),
            MediaAssetId = assetId,
            VersionNumber = 1,
            MediaType = "image/png",
            SizeBytes = write.Object!.SizeBytes,
            Width = 16,
            Height = 12,
            ContentChecksum = write.Object.ContentChecksum,
            ObjectKey = key,
            OriginalFileName = "soda-bread.png",
            Source = MediaAssetVersionSource.Upload,
            CreatedByMembershipId = actor,
            CreatedAt = Now,
        });

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Ct);

        return new LibraryPicture(assetId, key);
    }

    /// <summary>
    /// A rendition row and, when there are bytes, its object beside the source. Null bytes record a source
    /// that could not be compressed.
    /// </summary>
    private async Task AddRenditionAsync(
        SeededWorkspace workspace,
        string sourceObjectKey,
        MediaRenditionPurpose purpose,
        byte[]? bytes,
        Guid? generatedImageId = null,
        Guid? mediaAssetId = null)
    {
        Assert.True(MediaRenditionObjectKey.TryFor(sourceObjectKey, purpose, out var key));

        var rendition = new MediaRendition
        {
            Id = Guid.NewGuid(),
            GeneratedImageId = generatedImageId,
            MediaAssetId = mediaAssetId,
            MediaAssetVersionNumber = mediaAssetId is null ? null : 1,
            Purpose = purpose,
            Status = bytes is null ? MediaRenditionStatus.NotCompressed : MediaRenditionStatus.Ready,
            NotCompressedReason = bytes is null ? MediaRenditionReason.NotSmaller : null,
            SourceContentChecksum = "sha256:source",
            CreatedAt = Now,
        };

        if (bytes is not null)
        {
            var write = await _store.PutAsync(
                key.Container, key.ObjectKey, new MemoryStream(bytes), "image/jpeg", MediaPolicy.ImageMaxBytes, Ct);

            rendition.ObjectKey = key.ObjectKey;
            rendition.MediaType = "image/jpeg";
            rendition.SizeBytes = write.Object!.SizeBytes;
            rendition.ContentChecksum = write.Object.ContentChecksum;
            rendition.Width = 16;
            rendition.Height = 12;
        }

        await using var scope = ScopeFor(workspace);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.MediaRenditions.Add(rendition);
        await db.SaveChangesAsync(Ct);
    }
}
