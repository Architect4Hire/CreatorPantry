using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Content;
using CreatorPantry.Tests.Media;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// AF.3.4 over HTTP: a reference image may be a library asset version or a generated image as well as a brand
/// document, exactly one per request, and neither workspace can have the other's picture read.
/// </summary>
/// <remarks>
/// The pictures are seeded as rows, because what this route decides it decides from metadata: nothing here
/// opens a byte. Opening them is the task handler's, and its own tests hold that.
/// </remarks>
internal static class MediaPictureAnalysisFacadeTestExtensions
{
    /// <summary>Keeps a reading from one record, as the facade's own business does behind its plain-value call.</summary>
    public static Task<bool> KeepAsync(
        this IMediaPictureAnalysisFacade analyses, MediaPictureAnalysisRecord record, CancellationToken cancellationToken) =>
        analyses.KeepAsync(
            record.MediaAssetId,
            record.MediaAssetVersionNumber,
            record.GeneratedImageId,
            record.ContentChecksum,
            record.Observations,
            record.AiOperationId,
            record.PromptTemplateId,
            record.PromptTemplateVersion,
            cancellationToken);
}

public sealed class ReferenceImageSourceEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SeededWorkspace A => _fixture.WorkspaceA;

    private SeededWorkspace B => _fixture.WorkspaceB;

    [Fact]
    public async Task A_library_asset_is_accepted_and_pinned_to_its_current_version()
    {
        using var mine = await OwnerOf(A);
        var assetId = await AssetAsync(A, versions: 2);

        var response = await AskAsync(mine, A, new { source = "DamAsset", mediaAssetId = assetId });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var inputs = await InputsOfAsync(A, await BodyOf(response));
        Assert.Equal("DamAsset", inputs[ReferenceImageInputs.Source]);
        Assert.Equal(assetId.ToString(), inputs[ReferenceImageInputs.MediaAssetId]);

        // Named no version, so the one that was current when it was asked is what will be read — not
        // whichever is current when a worker gets to it.
        Assert.Equal("2", inputs[ReferenceImageInputs.MediaAssetVersionNumber]);
        Assert.False(inputs.ContainsKey(ReferenceImageInputs.ReferenceDocumentId));
    }

    [Fact]
    public async Task A_named_version_of_an_asset_is_the_one_pinned()
    {
        using var mine = await OwnerOf(A);
        var assetId = await AssetAsync(A, versions: 2);

        var response = await AskAsync(
            mine, A, new { source = "DamAsset", mediaAssetId = assetId, mediaAssetVersionNumber = 1 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            "1", (await InputsOfAsync(A, await BodyOf(response)))[ReferenceImageInputs.MediaAssetVersionNumber]);
    }

    [Theory]
    [InlineData(GeneratedImageStatus.Staged)]
    [InlineData(GeneratedImageStatus.Kept)]
    public async Task A_generated_image_that_is_staged_or_kept_is_accepted(GeneratedImageStatus status)
    {
        using var mine = await OwnerOf(A);
        var imageId = await ImageAsync(A, status);

        var response = await AskAsync(mine, A, new { source = "GeneratedImage", generatedImageId = imageId });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var inputs = await InputsOfAsync(A, await BodyOf(response));
        Assert.Equal("GeneratedImage", inputs[ReferenceImageInputs.Source]);
        Assert.Equal(imageId.ToString(), inputs[ReferenceImageInputs.GeneratedImageId]);
    }

    /// <summary>
    /// Every way a picture can be unreadable is one answer, whichever source it was named through.
    /// </summary>
    /// <remarks>
    /// The whole body is compared, not only the status: wording that told a declined image from an unknown
    /// one, or an asset from a generated image, would be a way to learn what exists (tenancy.md).
    /// </remarks>
    [Fact]
    public async Task Every_unreadable_picture_gets_the_same_refusal_as_one_that_does_not_exist()
    {
        using var mine = await OwnerOf(A);

        var removed = await AssetAsync(A, removed: true);
        var twoVersions = await AssetAsync(A, versions: 2);
        var notAnImage = await AssetAsync(A, mediaType: "application/pdf");
        var tooLarge = await AssetAsync(A, sizeBytes: PromptEnvelopePolicy.ImageMaxBytes + 1L);
        var declined = await ImageAsync(A, GeneratedImageStatus.Rejected);
        var expired = await ImageAsync(A, GeneratedImageStatus.Expired);
        var purged = await ImageAsync(A, GeneratedImageStatus.Kept, purged: true);

        var unknown = await AskAsync(mine, A, new { source = "DamAsset", mediaAssetId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var expected = WithoutTraceId(await BodyOf(unknown));
        Assert.Contains(AiReferenceImageRequestErrors.ReferenceNotFound, expected, StringComparison.Ordinal);

        object[] unreadable =
        [
            new { source = "DamAsset", mediaAssetId = removed },
            new { source = "DamAsset", mediaAssetId = twoVersions, mediaAssetVersionNumber = 3 },
            new { source = "DamAsset", mediaAssetId = notAnImage },
            new { source = "DamAsset", mediaAssetId = tooLarge },
            new { source = "GeneratedImage", generatedImageId = Guid.NewGuid() },
            new { source = "GeneratedImage", generatedImageId = declined },
            new { source = "GeneratedImage", generatedImageId = expired },
            new { source = "GeneratedImage", generatedImageId = purged },
            new { source = "BrandDocument", referenceDocumentId = Guid.NewGuid() },
        ];

        foreach (var body in unreadable)
        {
            var response = await AskAsync(mine, A, body);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(expected, WithoutTraceId(await BodyOf(response)));
        }

        Assert.Equal(0, await OperationCountAsync(A));
    }

    [Fact]
    public async Task Another_workspaces_asset_or_generated_image_is_answered_as_one_that_does_not_exist()
    {
        using var mine = await OwnerOf(A);
        var theirAsset = await AssetAsync(B);
        var theirImage = await ImageAsync(B, GeneratedImageStatus.Staged);

        var missing = WithoutTraceId(await BodyOf(
            await AskAsync(mine, A, new { source = "GeneratedImage", generatedImageId = Guid.NewGuid() })));

        var asset = await AskAsync(mine, A, new { source = "DamAsset", mediaAssetId = theirAsset });
        var pinned = await AskAsync(
            mine, A, new { source = "DamAsset", mediaAssetId = theirAsset, mediaAssetVersionNumber = 1 });
        var image = await AskAsync(mine, A, new { source = "GeneratedImage", generatedImageId = theirImage });

        foreach (var response in new[] { asset, pinned, image })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(missing, WithoutTraceId(await BodyOf(response)));
        }

        // Nothing was queued in either workspace for a picture that is not the asker's.
        Assert.Equal(0, await OperationCountAsync(A));
        Assert.Equal(0, await OperationCountAsync(B));

        // And the same ids are perfectly readable to the workspace that holds them.
        using var theirs = await OwnerOf(B);
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await AskAsync(theirs, B, new { source = "DamAsset", mediaAssetId = theirAsset })).StatusCode);
    }

    /// <summary>
    /// Mixed and absent sources are a 400 naming the field that is wrong, and nothing is queued or charged.
    /// </summary>
    [Theory]
    [InlineData("""{ "note": "no picture at all" }""", "source")]
    [InlineData("""{ "source": "DamAsset" }""", "mediaAssetId")]
    [InlineData("""{ "source": "GeneratedImage" }""", "generatedImageId")]
    [InlineData("""{ "source": "BrandDocument" }""", "referenceDocumentId")]
    [InlineData("""{ "source": "DamAsset", "mediaAssetId": "{asset}", "generatedImageId": "{image}" }""", "generatedImageId")]
    [InlineData("""{ "source": "GeneratedImage", "generatedImageId": "{image}", "mediaAssetId": "{asset}" }""", "mediaAssetId")]
    [InlineData("""{ "source": "GeneratedImage", "generatedImageId": "{image}", "referenceDocumentId": "{asset}" }""", "referenceDocumentId")]
    [InlineData("""{ "source": "BrandDocument", "referenceDocumentId": "{asset}", "mediaAssetVersionNumber": 1 }""", "mediaAssetVersionNumber")]
    [InlineData("""{ "mediaAssetId": "{asset}" }""", "source")]
    [InlineData("""{ "referenceDocumentId": "{asset}", "generatedImageId": "{image}" }""", "source")]
    [InlineData("""{ "source": "DamAsset", "mediaAssetId": "{asset}", "mediaAssetVersionNumber": 0 }""", "mediaAssetVersionNumber")]
    public async Task A_request_that_names_no_picture_or_more_than_one_is_a_400_with_the_field(
        string template, string field)
    {
        using var mine = await OwnerOf(A);
        var assetId = await AssetAsync(A);
        var imageId = await ImageAsync(A, GeneratedImageStatus.Staged);

        var json = template
            .Replace("{asset}", assetId.ToString(), StringComparison.Ordinal)
            .Replace("{image}", imageId.ToString(), StringComparison.Ordinal);

        var response = await AskAsync(mine, A, JsonNode.Parse(json)!);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(AiReferenceImageRequestErrors.RequestInvalid, body.GetProperty("code").GetString());
        Assert.Contains(
            body.GetProperty("errors").EnumerateObject(),
            error => string.Equals(error.Name, field, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await OperationCountAsync(A));
    }

    [Fact]
    public async Task A_source_this_route_does_not_know_is_a_400()
    {
        using var mine = await OwnerOf(A);

        var response = await AskAsync(
            mine, A, JsonNode.Parse("""{ "source": "Url", "mediaAssetId": "11111111-1111-1111-1111-111111111111" }""")!);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await OperationCountAsync(A));
    }

    /// <summary>
    /// What a reading leaves behind for later work is kept per picture, per workspace.
    /// </summary>
    [Fact]
    public async Task A_stored_analysis_is_read_back_by_its_picture_and_never_from_the_other_workspace()
    {
        var assetId = await AssetAsync(A, versions: 2);
        var imageId = await ImageAsync(A, GeneratedImageStatus.Staged);
        var assetChecksum = await InAsync(A, db => db.MediaAssetVersions
            .Where(version => version.MediaAssetId == assetId && version.VersionNumber == 2)
            .Select(version => version.ContentChecksum)
            .SingleAsync(Ct));
        var imageChecksum = await InAsync(A, db => db.GeneratedImages
            .Where(image => image.Id == imageId)
            .Select(image => image.ContentChecksum)
            .SingleAsync(Ct));

        Assert.True(await WithAnalysesAsync(A, analyses => analyses.KeepAsync(
            Reading(assetId, 2, null, assetChecksum, "Soft daylight from the left."), Ct)));
        Assert.True(await WithAnalysesAsync(A, analyses => analyses.KeepAsync(
            Reading(null, null, imageId, imageChecksum, "A pale oak board."), Ct)));

        var ofAsset = await WithAnalysesAsync(A, analyses => analyses.FindForAssetAsync(assetId, 2, Ct));
        var ofImage = await WithAnalysesAsync(A, analyses => analyses.FindForGeneratedImageAsync(imageId, Ct));

        Assert.Equal("Soft daylight from the left.", Assert.Single(ofAsset!.Observations).Text);
        Assert.Equal("image.reference-analysis", ofAsset.PromptTemplateId);
        Assert.Equal("A pale oak board.", Assert.Single(ofImage!.Observations).Text);

        // A reading is of one version: the other version of the same asset has none.
        Assert.Null(await WithAnalysesAsync(A, analyses => analyses.FindForAssetAsync(assetId, 1, Ct)));

        // The other workspace finds nothing under the same ids, and cannot keep a reading for them either.
        Assert.Null(await WithAnalysesAsync(B, analyses => analyses.FindForAssetAsync(assetId, 2, Ct)));
        Assert.Null(await WithAnalysesAsync(B, analyses => analyses.FindForGeneratedImageAsync(imageId, Ct)));
        Assert.False(await WithAnalysesAsync(B, analyses => analyses.KeepAsync(
            Reading(assetId, 2, null, assetChecksum, "Written from next door."), Ct)));
        Assert.False(await WithAnalysesAsync(B, analyses => analyses.KeepAsync(
            Reading(null, null, imageId, imageChecksum, "Written from next door."), Ct)));

        Assert.Equal(
            "Soft daylight from the left.",
            Assert.Single((await WithAnalysesAsync(A, analyses => analyses.FindForAssetAsync(assetId, 2, Ct)))!.Observations).Text);
        Assert.Equal(0, await InAsync(B, db => db.MediaPictureAnalyses.CountAsync(Ct)));
    }

    /// <summary>
    /// A reading does not outlive the picture: one that is removed, declined or expired is no longer the
    /// creator's to have read, so its stored reading is no longer found — the rule the request route applies.
    /// </summary>
    [Fact]
    public async Task A_stored_analysis_is_not_found_once_its_picture_is_no_longer_available()
    {
        var assetId = await AssetAsync(A);
        var imageId = await ImageAsync(A, GeneratedImageStatus.Staged);
        var assetChecksum = await InAsync(A, db => db.MediaAssetVersions
            .Where(version => version.MediaAssetId == assetId).Select(version => version.ContentChecksum).SingleAsync(Ct));
        var imageChecksum = await InAsync(A, db => db.GeneratedImages
            .Where(image => image.Id == imageId).Select(image => image.ContentChecksum).SingleAsync(Ct));

        Assert.True(await WithAnalysesAsync(A, analyses => analyses.KeepAsync(
            Reading(assetId, 1, null, assetChecksum, "Soft daylight."), Ct)));
        Assert.True(await WithAnalysesAsync(A, analyses => analyses.KeepAsync(
            Reading(null, null, imageId, imageChecksum, "A pale oak board."), Ct)));

        await InAsync(A, async db =>
        {
            var asset = await db.MediaAssets.SingleAsync(item => item.Id == assetId, Ct);
            asset.DeletedAt = Now;
            asset.DeletedByMembershipId = Guid.NewGuid();
            (await db.GeneratedImages.SingleAsync(image => image.Id == imageId, Ct)).Status = GeneratedImageStatus.Rejected;

            return await db.SaveChangesAsync(Ct);
        });

        Assert.Null(await WithAnalysesAsync(A, analyses => analyses.FindForAssetAsync(assetId, 1, Ct)));
        Assert.Null(await WithAnalysesAsync(A, analyses => analyses.FindForGeneratedImageAsync(imageId, Ct)));
    }

    [Fact]
    public async Task Reading_a_picture_again_replaces_its_stored_analysis_rather_than_adding_a_second()
    {
        var imageId = await ImageAsync(A, GeneratedImageStatus.Staged);
        var checksum = await InAsync(A, db => db.GeneratedImages
            .Where(image => image.Id == imageId).Select(image => image.ContentChecksum).SingleAsync(Ct));

        Assert.True(await WithAnalysesAsync(A, analyses => analyses.KeepAsync(
            Reading(null, null, imageId, checksum, "The first look."), Ct)));
        Assert.True(await WithAnalysesAsync(A, analyses => analyses.KeepAsync(
            Reading(null, null, imageId, checksum, "The second look."), Ct)));

        Assert.Equal(1, await InAsync(A, db => db.MediaPictureAnalyses.CountAsync(Ct)));
        Assert.Equal(
            "The second look.",
            Assert.Single((await WithAnalysesAsync(A, analyses => analyses.FindForGeneratedImageAsync(imageId, Ct)))!.Observations).Text);
    }

    /// <summary>
    /// A reading of other bytes than the picture holds is a reading of a different picture, and is not kept
    /// under this one's name. Nor is one that names no picture, two, or nothing observed.
    /// </summary>
    [Fact]
    public async Task A_reading_that_is_not_of_this_picture_is_not_kept()
    {
        var assetId = await AssetAsync(A);
        var imageId = await ImageAsync(A, GeneratedImageStatus.Staged);
        var checksum = await InAsync(A, db => db.GeneratedImages
            .Where(image => image.Id == imageId).Select(image => image.ContentChecksum).SingleAsync(Ct));

        MediaPictureAnalysisRecord[] refused =
        [
            Reading(null, null, imageId, "sha256:" + new string('f', 64), "Of some other bytes."),
            Reading(assetId, 1, imageId, checksum, "Of two pictures at once."),
            Reading(null, null, null, checksum, "Of no picture."),
            Reading(assetId, null, null, checksum, "Of an asset at no version."),
            Reading(null, null, Guid.NewGuid(), checksum, "Of a picture that is not there."),
            Reading(null, null, imageId, checksum, "Nothing was observed.") with { Observations = [] },
        ];

        foreach (var record in refused)
        {
            Assert.False(await WithAnalysesAsync(A, analyses => analyses.KeepAsync(record, Ct)));
        }

        Assert.Equal(0, await InAsync(A, db => db.MediaPictureAnalyses.CountAsync(Ct)));
    }

    /// <summary>
    /// The worker's half of isolation: a queued operation's inputs are only ids, and an id is not authorization.
    /// </summary>
    /// <remarks>
    /// What a task handler does with those ids is open them through these facades, in the workspace the
    /// operation was claimed for. So a tampered or stale input naming the other workspace's picture has to
    /// find nothing here — the real facades, over the real filter — or the handler's own tests, which run
    /// against doubles, would be proving the wrong thing.
    /// </remarks>
    [Fact]
    public async Task A_worker_in_one_workspace_cannot_open_or_resolve_the_others_pictures()
    {
        var theirAsset = await AssetAsync(B, versions: 2);
        var theirImage = await ImageAsync(B, GeneratedImageStatus.Staged);

        await InScopeAsync(A, async services =>
        {
            var assets = services.GetRequiredService<IMediaAssetLookupFacade>();
            var images = services.GetRequiredService<IGeneratedImageLookupFacade>();

            Assert.Null(await assets.ResolvePictureAsync(theirAsset, null, Ct));
            Assert.Null(await assets.ResolvePictureAsync(theirAsset, 2, Ct));
            Assert.Null(await images.ResolvePictureAsync(theirImage, Ct));

            var asset = await assets.OpenPictureAsync(theirAsset, 2, Ct);
            var image = await images.OpenPictureAsync(theirImage, Ct);

            // Not found, and not "storage unavailable": the row was never seen, so storage was never asked.
            Assert.Equal(MediaPictureOpenOutcome.NotFound, asset.Outcome);
            Assert.Null(asset.Picture);
            Assert.Equal(MediaPictureOpenOutcome.NotFound, image.Outcome);
            Assert.Null(image.Picture);

            return true;
        });

        // The same ids resolve for the workspace that holds them, so the refusal above is the filter's.
        await InScopeAsync(B, async services =>
        {
            Assert.Equal(2, (await services.GetRequiredService<IMediaAssetLookupFacade>()
                .ResolvePictureAsync(theirAsset, null, Ct))?.VersionNumber);
            Assert.NotNull(await services.GetRequiredService<IGeneratedImageLookupFacade>()
                .ResolvePictureAsync(theirImage, Ct));

            return true;
        });
    }

    /// <summary>
    /// The table's own shape, so the things that keep one workspace's readings from another's cannot be lost
    /// to a later edit of its configuration.
    /// </summary>
    [Fact]
    public async Task The_stored_analysis_table_is_workspace_owned_filtered_and_keyed_by_workspace()
    {
        await InAsync(A, db =>
        {
            var entity = db.Model.FindEntityType(typeof(MediaPictureAnalysis))!;

            Assert.True(typeof(IWorkspaceOwned).IsAssignableFrom(entity.ClrType));
            Assert.NotEmpty(entity.GetDeclaredQueryFilters());

            // Uniqueness is workspace-relative: every unique index leads with the workspace.
            var unique = entity.GetIndexes().Where(index => index.IsUnique).ToList();
            Assert.Equal(2, unique.Count);
            Assert.All(unique, index =>
            {
                Assert.Equal(nameof(MediaPictureAnalysis.WorkspaceId), index.Properties[0].Name);
                Assert.False(string.IsNullOrWhiteSpace(index.GetFilter()));
            });

            // Both pictures are referenced on (workspace, id), so a row cannot name another workspace's.
            var pictures = entity.GetForeignKeys()
                .Where(key => key.PrincipalEntityType.ClrType == typeof(MediaAsset)
                    || key.PrincipalEntityType.ClrType == typeof(GeneratedImage))
                .ToList();
            Assert.Equal(2, pictures.Count);
            Assert.All(pictures, key =>
            {
                Assert.Equal(2, key.Properties.Count);
                Assert.Equal(nameof(MediaPictureAnalysis.WorkspaceId), key.Properties[0].Name);
                Assert.Equal(DeleteBehavior.Restrict, key.DeleteBehavior);
            });

            return Task.FromResult(true);
        });
    }

    private static MediaPictureAnalysisRecord Reading(
        Guid? assetId, int? versionNumber, Guid? imageId, string checksum, string text) =>
        new(
            assetId,
            versionNumber,
            imageId,
            checksum,
            [new MediaPictureObservation("Lighting", text, "Clear")],
            Guid.NewGuid(),
            "image.reference-analysis",
            "1.0.0");

    private static string Route(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/reference-image-requests";

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    private static Task<HttpResponseMessage> AskAsync(GatewayClient client, SeededWorkspace workspace, object body) =>
        client.PostAsJsonAsync(Route(workspace), body, Guid.NewGuid().ToString("N"), Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string WithoutTraceId(JsonElement body)
    {
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node.Remove("traceId");

        return node.ToJsonString();
    }

    /// <summary>Runs against the API's own database as a member of that workspace.</summary>
    private async Task<T> InAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>Runs with that workspace resolved, as a worker does before it calls any facade.</summary>
    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider);
    }

    /// <summary>Calls the analysis facade as a member of that workspace, as a worker would after resolving it.</summary>
    private async Task<T> WithAnalysesAsync<T>(SeededWorkspace workspace, Func<IMediaPictureAnalysisFacade, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<IMediaPictureAnalysisFacade>());
    }

    private Task<int> OperationCountAsync(SeededWorkspace workspace) =>
        InAsync(workspace, db => db.AiOperations.CountAsync(Ct));

    /// <summary>What the queued operation was told to read, as the worker will see it.</summary>
    private async Task<Dictionary<string, string>> InputsOfAsync(SeededWorkspace workspace, JsonElement accepted)
    {
        var requestId = accepted.GetProperty("aiProposalRequestId").GetGuid();
        var json = await InAsync(workspace, db => db.AiOperations
            .Where(operation => operation.Id == requestId)
            .Select(operation => operation.TaskInputsJson)
            .SingleAsync(Ct));

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json!)!;
    }

    /// <summary>A library asset of that workspace with that many versions, the last of them current.</summary>
    private Task<Guid> AssetAsync(
        SeededWorkspace workspace,
        int versions = 1,
        bool removed = false,
        string mediaType = "image/jpeg",
        long sizeBytes = 1024) =>
        InAsync(workspace, async db =>
        {
            var asset = SeededMediaAsset.For(workspace.Id, at: Now);
            asset.CurrentVersionNumber = versions;
            asset.DeletedAt = removed ? Now : null;
            asset.DeletedByMembershipId = removed ? Guid.NewGuid() : null;
            db.MediaAssets.Add(asset);

            for (var number = 1; number <= versions; number++)
            {
                var version = SeededMediaAsset.VersionOf(asset, number);
                version.MediaType = mediaType;
                version.SizeBytes = sizeBytes;
                db.MediaAssetVersions.Add(version);
            }

            await db.SaveChangesAsync(Ct);

            return asset.Id;
        });

    /// <summary>A generated image of that workspace in that state.</summary>
    private Task<Guid> ImageAsync(SeededWorkspace workspace, GeneratedImageStatus status, bool purged = false) =>
        InAsync(workspace, async db =>
        {
            var imageId = await CreativeContextSeeds.GeneratedImageAsync(db, Now, Ct);
            var image = await db.GeneratedImages.SingleAsync(each => each.Id == imageId, Ct);
            image.Status = status;
            image.ObjectDeletedAt = purged ? Now : null;

            // Each its own bytes, as real pictures are.
            image.ContentChecksum = "sha256:" + Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant().PadRight(64, '0');
            await db.SaveChangesAsync(Ct);

            return imageId;
        });
}
