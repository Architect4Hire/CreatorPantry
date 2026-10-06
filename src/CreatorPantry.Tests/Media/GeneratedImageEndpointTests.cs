using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// <c>GET .../generated-images/{id}/preview</c>, <c>.../content</c> and <c>DELETE .../{id}</c> through the
/// real Gateway, over an in-memory object store: the three contracts of IMG-005 and IMG-006.
/// </summary>
/// <remarks>
/// <para>
/// Three routes in one file because they are one feature with one disclosure rule, and most of what is
/// worth asserting is that they agree: the same situations are the same 404, and no response carries an
/// address for a staging object.
/// </para>
/// <para>
/// The retrieval assertions are mostly about the <em>name</em> and the <em>type</em>, because that is
/// where a download can lie about what a provider actually returned. The bytes are asserted once, byte
/// for byte.
/// </para>
/// </remarks>
public sealed class GeneratedImageEndpointTests : IAsyncLifetime
{
    private readonly InMemoryPrivateObjectStore _store = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---- retrieval ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_download_carries_the_bytes_the_type_read_from_them_and_a_name_that_matches()
    {
        var bytes = BrandSourceSampleFiles.Png(width: 64, height: 48);
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0, bytes: bytes);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync(Content(_fixture.WorkspaceA, image));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        // The type established from the bytes at staging, never one a provider declared.
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        // Variant zero is the creator's first image, and the extension follows the real media type.
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("generated-1.png", response.Content.Headers.ContentDisposition?.FileName);

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.NotNull(response.Headers.ETag);
        Assert.True(response.Headers.ETag!.IsWeak is false);
    }

    [Fact]
    public async Task A_preview_is_the_same_response_shown_inline()
    {
        var bytes = BrandSourceSampleFiles.Jpeg(width: 32, height: 32);
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 1, bytes: bytes, mediaType: "image/jpeg");

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync(Preview(_fixture.WorkspaceA, image));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);

        // The one difference from the download, and the extension is still the real one.
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("generated-2.jpg", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task A_matching_entity_tag_answers_304_without_the_body()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var first = await client.GetAsync(Content(_fixture.WorkspaceA, image));
        var tag = first.Headers.ETag!.ToString();

        var second = await client.GetAsync(
            Content(_fixture.WorkspaceA, image), new Dictionary<string, string> { ["If-None-Match"] = tag });

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        // Every read handed out was handed back: a 304 releases its lease like any other path.
        Assert.Equal(0, _store.OpenReads);
    }

    // ---- what is not there, and what is not yours ------------------------------------------------------

    [Fact]
    public async Task An_unknown_image_answers_the_not_found_code()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/generated-images/{Guid.NewGuid()}/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("media.staged_image.not_found", await CodeAsync(response));
    }

    [Fact]
    public async Task Another_workspaces_image_is_the_same_404_as_one_that_does_not_exist()
    {
        var image = await StageAsync(_fixture.WorkspaceB, variantIndex: 0);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        // The real id of a real image, asked for under the neighbour's slug. Nothing may distinguish this
        // from an id that was never issued, or a caller could count a neighbour's images (tenancy.md).
        foreach (var path in new[] { Content(_fixture.WorkspaceA, image), Preview(_fixture.WorkspaceA, image) })
        {
            var response = await client.GetAsync(path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("media.staged_image.not_found", await CodeAsync(response));
        }

        // And the bytes were never opened, let alone sent.
        Assert.Equal(0, _store.OpenReads);
    }

    [Fact]
    public async Task An_image_whose_bytes_retention_already_removed_is_not_found_rather_than_unavailable()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0);
        await PurgeRowAsync(image);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync(Content(_fixture.WorkspaceA, image));

        // Gone for good, so "try again" would be the wrong thing to tell a creator.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("media.staged_image.not_found", await CodeAsync(response));
    }

    [Fact]
    public async Task A_row_whose_bytes_cannot_be_read_is_unavailable_rather_than_not_found()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0);
        _store.Unavailable = true;

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync(Content(_fixture.WorkspaceA, image));

        // The image exists and retrying is the remedy. A 404 here would tell a creator their work is gone.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("media.staged_image.unavailable", await CodeAsync(response));
    }

    [Theory]
    [InlineData("..%2F..%2Fetc%2Fpasswd")]
    [InlineData("workspaces")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_segment_that_is_not_an_image_id_never_reaches_a_store(string segment)
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/generated-images/{segment}/content");

        // The route is Guid-constrained, so anything shaped like a path does not route at all — there is
        // no code path from a URL segment to an object key, which is what makes traversal unreachable
        // rather than merely filtered.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, _store.OpenReads);
    }

    // ---- declining ------------------------------------------------------------------------------------

    [Fact]
    public async Task Declining_an_image_marks_the_row_and_leaves_the_bytes_for_the_sweep()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await DeleteAsync(client, _fixture.WorkspaceA, image);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var row = await RowAsync(image);
        Assert.Equal(GeneratedImageStatus.Rejected, row.Status);

        // The bytes stay. One system is written to per request, so there is no half-done delete to recover.
        Assert.Null(row.ObjectDeletedAt);
        Assert.Single(_store.Keys);
    }

    [Fact]
    public async Task Declining_twice_answers_the_same_way()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, _fixture.WorkspaceA, image)).StatusCode);

        // A client that retried, or a creator who clicked twice. They asked for the image to be gone and
        // it is, which is the answer they wanted however many times they ask.
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(client, _fixture.WorkspaceA, image)).StatusCode);
        Assert.Equal(GeneratedImageStatus.Rejected, (await RowAsync(image)).Status);
    }

    [Fact]
    public async Task A_kept_image_can_no_longer_be_declined()
    {
        var image = await StageAsync(_fixture.WorkspaceA, variantIndex: 0, status: GeneratedImageStatus.Kept);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await DeleteAsync(client, _fixture.WorkspaceA, image);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("media.staged_image.conflict", await CodeAsync(response));
        Assert.Equal(GeneratedImageStatus.Kept, (await RowAsync(image)).Status);
    }

    [Fact]
    public async Task A_viewer_cannot_decline_an_image()
    {
        var image = await StageAsync(_fixture.WorkspaceB, variantIndex: 0);

        // Workspace B's member is seeded as a Viewer.
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail);
        var response = await DeleteAsync(client, _fixture.WorkspaceB, image);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(GeneratedImageStatus.Staged, (await RowAsync(image)).Status);
    }

    [Fact]
    public async Task A_viewer_may_still_read_an_image()
    {
        var image = await StageAsync(_fixture.WorkspaceB, variantIndex: 0);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail);

        Assert.Equal(
            HttpStatusCode.OK, (await client.GetAsync(Content(_fixture.WorkspaceB, image))).StatusCode);
    }

    [Fact]
    public async Task Declining_another_workspaces_image_changes_nothing()
    {
        var image = await StageAsync(_fixture.WorkspaceB, variantIndex: 0);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var response = await DeleteAsync(client, _fixture.WorkspaceA, image);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("media.staged_image.not_found", await CodeAsync(response));
        Assert.Equal(GeneratedImageStatus.Staged, (await RowAsync(image)).Status);
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private static string Content(SeededWorkspace workspace, Guid imageId) =>
        $"/api/v1/workspaces/{workspace.Slug}/generated-images/{imageId}/content";

    private static string Preview(SeededWorkspace workspace, Guid imageId) =>
        $"/api/v1/workspaces/{workspace.Slug}/generated-images/{imageId}/preview";

    private static Task<HttpResponseMessage> DeleteAsync(
        GatewayClient client, SeededWorkspace workspace, Guid imageId) =>
        client.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/workspaces/{workspace.Slug}/generated-images/{imageId}",
            body: null,
            headers: null,
            TestContext.Current.CancellationToken);

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("code").GetString();

    /// <summary>
    /// Writes one staged image: the object, then the operation and the row that owns it.
    /// </summary>
    /// <remarks>
    /// Straight to the store and the database rather than through the generation job, because this file is
    /// about what the three routes do with a staged image rather than about how one comes to exist —
    /// <c>GeneratedImageWorkerTests</c> owns that, and staging through it would make every test here
    /// depend on a provider fake it does not care about.
    /// </remarks>
    private async Task<Guid> StageAsync(
        SeededWorkspace workspace,
        int variantIndex,
        byte[]? bytes = null,
        string mediaType = "image/png",
        GeneratedImageStatus status = GeneratedImageStatus.Staged)
    {
        var content = bytes ?? BrandSourceSampleFiles.Png(width: 16, height: 12);
        var operationId = Guid.NewGuid();
        var key = GeneratedImageObjectKey.For(workspace.Id, operationId, variantIndex);

        var write = await _store.PutAsync(
            GeneratedImageObjectKey.Container,
            key,
            new MemoryStream(content),
            mediaType,
            MediaPolicy.ImageMaxBytes,
            TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;
        var imageId = Guid.NewGuid();

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.GeneratedImageOperations.Add(new GeneratedImageOperation
        {
            Id = operationId,
            WorkspaceId = workspace.Id,
            Status = GeneratedImageOperationStatus.Succeeded,
            PromptText = "a bowl of soup on a wooden table",
            VariantCount = MediaPolicy.MaxVariantsPerOperation,
            IdempotencyKey = operationId.ToString("N"),
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = now,
            StatusChangedAt = now,
            AvailableAt = now,
        });

        db.GeneratedImages.Add(new GeneratedImage
        {
            Id = imageId,
            WorkspaceId = workspace.Id,
            GeneratedImageOperationId = operationId,
            VariantIndex = variantIndex,
            Status = status,
            ObjectKey = key,
            MediaType = mediaType,
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

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return imageId;
    }

    /// <summary>Puts a row into the state the sweep leaves behind: bytes gone, record kept.</summary>
    private async Task PurgeRowAsync(Guid imageId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var row = await db.GeneratedImages
            .IgnoreQueryFilters()
            .FirstAsync(image => image.Id == imageId, TestContext.Current.CancellationToken);

        row.Status = GeneratedImageStatus.Expired;
        row.ObjectDeletedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc cref="PurgeRowAsync"/>
    private async Task<GeneratedImage> RowAsync(Guid imageId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .GeneratedImages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(image => image.Id == imageId, TestContext.Current.CancellationToken);
    }
}
