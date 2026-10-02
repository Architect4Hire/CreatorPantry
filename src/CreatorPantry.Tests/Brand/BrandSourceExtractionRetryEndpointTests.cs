using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <c>POST .../versions/{n}/extraction/retry</c> through the real Gateway: the contract of asking for a version's
/// text to be read again. That the worker then writes the next artifact is driven end to end in
/// <see cref="BrandSourceExtractionQueueTests"/>; what this file is about is the HTTP surface — status, shape,
/// policy, the conflicts, replay and isolation.
/// </summary>
public sealed class BrandSourceExtractionRetryEndpointTests : IAsyncLifetime
{
    private const string ViewerEmail = "extraction-retry-viewer-a@example.com";

    private const string Password = "correct horse battery";

    private readonly InMemoryPrivateObjectStore _store = new();

    private readonly FakeMalwareScanGateway _scanner = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(_scanner);
        });

        var userId = await _fixture.Api.CreateUserAsync(ViewerEmail, Password);
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            UserId = userId,
            Role = WorkspaceRole.Viewer,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---- Accepting ----

    [Fact]
    public async Task Asking_again_after_a_failed_read_is_accepted_and_puts_the_read_back_in_the_queue()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var (documentId, versionId) = await SeedDocumentAsync(client);
        var failed = await SeedFailedReadAsync(versionId);

        var response = await RetryAsync(client, documentId, expected: failed);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.EndsWith($"/{documentId}/versions/1/extraction", response.Headers.Location!.ToString());

        var body = await BodyOf(response);
        Assert.Equal("Failed", body.GetProperty("state").GetString());
        Assert.Equal(failed, body.GetProperty("id").GetGuid());
        Assert.True(body.GetProperty("isReading").GetBoolean());

        var operation = await OperationAsync(versionId);
        Assert.Equal(BrandSourceExtractionOperationStatus.Queued, operation.Status);
        Assert.Equal(0, operation.Attempts);
        Assert.Null(operation.CompletedAt);

        // The earlier artifact is untouched, and a read says the work is under way.
        Assert.Single(await ArtifactsAsync(versionId));
        var read = await BodyOf(await client.GetAsync(ExtractionIn(documentId), TestContext.Current.CancellationToken));
        Assert.True(read.GetProperty("isReading").GetBoolean());
        Assert.Equal("Failed", read.GetProperty("state").GetString());

        Assert.Equal(1, await AuditCountAsync(documentId, BrandAuditActions.SourceExtractionRetried));
    }

    [Fact]
    public async Task A_read_that_stopped_before_it_wrote_anything_can_be_asked_for_again()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var (documentId, versionId) = await SeedDocumentAsync(client);
        await SetOperationAsync(versionId, operation =>
        {
            operation.Status = BrandSourceExtractionOperationStatus.Failed;
            operation.FailureCategory = BrandSourceExtractionFailureCategory.Storage;
            operation.FailureSummary = "Storage was unreachable.";
            operation.CompletedAt = DateTimeOffset.UtcNow;
        });

        var response = await RetryAsync(client, documentId, expected: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("NotExtracted", body.GetProperty("state").GetString());
        Assert.True(body.GetProperty("isReading").GetBoolean());
        Assert.Equal(BrandSourceExtractionOperationStatus.Queued, (await OperationAsync(versionId)).Status);
    }

    [Fact]
    public async Task Asking_while_a_read_is_already_queued_is_accepted_and_changes_nothing()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var (documentId, versionId) = await SeedDocumentAsync(client);
        var before = await OperationAsync(versionId);

        var response = await RetryAsync(client, documentId, expected: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("isReading").GetBoolean());
        Assert.Equal(before.QueuedAt, (await OperationAsync(versionId)).QueuedAt);
        Assert.Equal(0, await AuditCountAsync(documentId, BrandAuditActions.SourceExtractionRetried));
    }

    [Fact]
    public async Task A_fresh_upload_reads_as_reading_before_anything_has_finished()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var (documentId, _) = await SeedDocumentAsync(client);

        var body = await BodyOf(await client.GetAsync(ExtractionIn(documentId), TestContext.Current.CancellationToken));

        Assert.Equal("NotExtracted", body.GetProperty("state").GetString());
        Assert.True(body.GetProperty("isReading").GetBoolean());
    }

    // ---- Refusing ----

    [Theory]
    [InlineData("Succeeded", "already been read")]
    [InlineData("Unsupported", "Type the text in")]
    public async Task Text_that_was_read_or_has_nothing_to_read_is_not_retryable_and_says_why(string kind, string fragment)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var (documentId, versionId) = await SeedDocumentAsync(client);
        var artifact = await SeedArtifactAsync(versionId, kind == "Succeeded" ? BrandSourceExtractionStatus.Succeeded : BrandSourceExtractionStatus.Unsupported);
        await SetOperationAsync(versionId, operation =>
        {
            operation.Status = BrandSourceExtractionOperationStatus.Completed;
            operation.CompletedAt = DateTimeOffset.UtcNow;
        });

        var response = await RetryAsync(client, documentId, expected: artifact);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.SourceExtractionNotRetryableConflict, body.GetProperty("code").GetString());
        Assert.Contains(fragment, body.GetProperty("title").GetString());
        Assert.Equal(BrandSourceExtractionOperationStatus.Completed, (await OperationAsync(versionId)).Status);
    }

    [Fact]
    public async Task An_artifact_other_than_the_one_being_looked_at_is_a_conflict()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var (documentId, versionId) = await SeedDocumentAsync(client);
        await SeedFailedReadAsync(versionId);

        var response = await RetryAsync(client, documentId, expected: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceExtractionConflict, (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_archived_document_cannot_be_read_again_until_it_is_restored()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var (documentId, versionId) = await SeedDocumentAsync(client);
        var failed = await SeedFailedReadAsync(versionId);
        await SetDocumentStatusAsync(documentId, BrandSourceDocumentStatus.Archived);

        var response = await RetryAsync(client, documentId, expected: failed);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceArchivedConflict, (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_version_that_has_been_replaced_cannot_be_read_again()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var (documentId, versionId) = await SeedDocumentAsync(client);
        var failed = await SeedFailedReadAsync(versionId);
        await ReplaceAsync(client, documentId);

        var response = await RetryAsync(client, documentId, expected: failed);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceExtractionSupersededConflict, (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_viewer_cannot_ask_for_a_read_again()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceA);
        var (documentId, versionId) = await SeedDocumentAsync(owner);
        var failed = await SeedFailedReadAsync(versionId);
        using var viewer = await _fixture.SignInAsync(ViewerEmail, Password, TestContext.Current.CancellationToken);

        var response = await RetryAsync(viewer, documentId, expected: failed);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(BrandSourceExtractionOperationStatus.Completed, (await OperationAsync(versionId)).Status);
    }

    [Fact]
    public async Task A_request_without_an_idempotency_key_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var (documentId, versionId) = await SeedDocumentAsync(client);
        var failed = await SeedFailedReadAsync(versionId);

        var response = await client.PostAsJsonAsync(
            RetryUrl(_fixture.WorkspaceA, documentId), new { expectedExtractionId = failed }, TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(BrandSourceExtractionOperationStatus.Completed, (await OperationAsync(versionId)).Status);
    }

    // ---- Replay and isolation ----

    [Fact]
    public async Task Repeating_a_request_with_its_key_replays_it_and_queues_nothing_more()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var (documentId, versionId) = await SeedDocumentAsync(client);
        var failed = await SeedFailedReadAsync(versionId);
        var key = Guid.NewGuid().ToString("N");

        var first = await RetryAsync(client, documentId, expected: failed, key: key);
        var second = await RetryAsync(client, documentId, expected: failed, key: key);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());
        Assert.Equal(1, await AuditCountAsync(documentId, BrandAuditActions.SourceExtractionRetried));
    }

    [Fact]
    public async Task Another_workspaces_document_answers_exactly_as_one_that_does_not_exist()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var (documentId, versionId) = await SeedDocumentAsync(ownerA);
        var failed = await SeedFailedReadAsync(versionId);

        var intoA = await ownerB.PostAsJsonAsync(
            RetryUrl(_fixture.WorkspaceA, documentId),
            new { expectedExtractionId = failed },
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);
        var intoNowhere = await ownerB.PostAsJsonAsync(
            $"/api/v1/workspaces/no-such-kitchen/brand-source-documents/{documentId}/versions/1/extraction/retry",
            new { expectedExtractionId = failed },
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);
        var sameWorkspaceWrongId = await RetryAsync(ownerB, documentId, expected: failed, workspace: _fixture.WorkspaceB);

        Assert.Equal(HttpStatusCode.NotFound, intoA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, intoNowhere.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, sameWorkspaceWrongId.StatusCode);
        Assert.Equal(
            (await BodyOf(intoNowhere)).GetProperty("code").GetString(),
            (await BodyOf(intoA)).GetProperty("code").GetString());
        Assert.Equal(BrandSourceExtractionOperationStatus.Completed, (await OperationAsync(versionId)).Status);
    }

    [Fact]
    public async Task The_answer_carries_no_address_no_checksum_and_no_membership_id()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var (documentId, versionId) = await SeedDocumentAsync(client);
        var failed = await SeedFailedReadAsync(versionId);

        var raw = await (await RetryAsync(client, documentId, expected: failed))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("objectKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checksum", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("membership", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("://", raw);
    }

    // ---- Helpers ----

    private Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private static string SourcesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents";

    private string ExtractionIn(Guid documentId) => $"{SourcesIn(_fixture.WorkspaceA)}/{documentId}/versions/1/extraction";

    private static string RetryUrl(SeededWorkspace workspace, Guid documentId) =>
        $"{SourcesIn(workspace)}/{documentId}/versions/1/extraction/retry";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> RetryAsync(
        GatewayClient client, Guid documentId, Guid? expected, string? key = null, SeededWorkspace? workspace = null) =>
        client.PostAsJsonAsync(
            RetryUrl(workspace ?? _fixture.WorkspaceA, documentId),
            new { expectedExtractionId = expected },
            key ?? Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

    private async Task<(Guid DocumentId, Guid VersionId)> SeedDocumentAsync(GatewayClient client)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(BrandSourceSampleFiles.Pdf("house style"));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "house-style.pdf");
        form.Add(new StringContent("House style"), "title");
        form.Add(new StringContent("StyleGuide"), "documentType");
        form.Add(new StringContent("Voice"), "purpose");

        var response = await client.PostAsync(
            SourcesIn(_fixture.WorkspaceA), form, Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);

        return (body.GetProperty("id").GetGuid(), body.GetProperty("currentVersion").GetProperty("id").GetGuid());
    }

    private async Task ReplaceAsync(GatewayClient client, Guid documentId)
    {
        var token = (await BodyOf(await client.GetAsync(
                $"{SourcesIn(_fixture.WorkspaceA)}/{documentId}", TestContext.Current.CancellationToken)))
            .GetProperty("concurrencyToken").GetString()!;

        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(BrandSourceSampleFiles.Pdf("second"));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "house-style.pdf");
        form.Add(new StringContent(token), "expectedConcurrencyToken");

        var response = await client.PostAsync(
            $"{SourcesIn(_fixture.WorkspaceA)}/{documentId}/versions",
            form,
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>A failed artifact, with the operation that produced it completed — a read that settled on "could not read it".</summary>
    private async Task<Guid> SeedFailedReadAsync(Guid versionId)
    {
        var id = await SeedArtifactAsync(versionId, BrandSourceExtractionStatus.Failed);
        await SetOperationAsync(versionId, operation =>
        {
            operation.Status = BrandSourceExtractionOperationStatus.Completed;
            operation.CompletedAt = DateTimeOffset.UtcNow;
        });

        return id;
    }

    private async Task<Guid> SeedArtifactAsync(Guid versionId, BrandSourceExtractionStatus status)
    {
        var extraction = new BrandSourceExtraction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            BrandSourceDocumentVersionId = versionId,
            Ordinal = 1,
            Status = status,
            Origin = BrandSourceExtractionOrigin.Extracted,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        if (status is BrandSourceExtractionStatus.Succeeded)
        {
            // Text must have somewhere to live, or the read faults on a missing object rather than returning.
            var documentId = await DocumentIdAsync(versionId);
            var key = BrandSourceObjectKey.ForExtractedText(_fixture.WorkspaceA.Id, documentId, versionId, 1);
            await _store.PutAsync(
                BrandSourceObjectKey.Container,
                key,
                new MemoryStream("Warm and plain.\n"u8.ToArray()),
                "text/plain; charset=utf-8",
                BrandPolicy.ExtractedTextMaxBytes,
                TestContext.Current.CancellationToken);
            extraction.ExtractedTextObjectKey = key;
            extraction.ContentChecksum = "sha256:" + new string('0', 64);
        }
        else
        {
            extraction.Reason = "Could not be read.";
        }

        await using var scope = ScopeFor(_fixture.WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.BrandSourceExtractions.Add(extraction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return extraction.Id;
    }

    private async Task<Guid> DocumentIdAsync(Guid versionId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().BrandSourceDocumentVersions
            .AsNoTracking()
            .Where(version => version.Id == versionId)
            .Select(version => version.BrandSourceDocumentId)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task SetOperationAsync(Guid versionId, Action<BrandSourceExtractionOperation> change)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var operation = await db.BrandSourceExtractionOperations.SingleAsync(
            candidate => candidate.BrandSourceDocumentVersionId == versionId, TestContext.Current.CancellationToken);

        change(operation);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<BrandSourceExtractionOperation> OperationAsync(Guid versionId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().BrandSourceExtractionOperations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.BrandSourceDocumentVersionId == versionId, TestContext.Current.CancellationToken);
    }

    private async Task<List<BrandSourceExtraction>> ArtifactsAsync(Guid versionId)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().BrandSourceExtractions
            .AsNoTracking()
            .Where(extraction => extraction.BrandSourceDocumentVersionId == versionId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> AuditCountAsync(Guid documentId, string action)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AuditLogs
            .IgnoreQueryFilters()
            .CountAsync(log => log.ResourceId == documentId.ToString() && log.Action == action, TestContext.Current.CancellationToken);
    }

    private async Task SetDocumentStatusAsync(Guid documentId, BrandSourceDocumentStatus status)
    {
        await using var scope = ScopeFor(_fixture.WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var document = await db.BrandSourceDocuments.SingleAsync(
            candidate => candidate.Id == documentId, TestContext.Current.CancellationToken);

        document.Status = status;
        document.ArchivedAt = status is BrandSourceDocumentStatus.Archived ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private AsyncServiceScope ScopeFor(SeededWorkspace workspace)
    {
        var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return scope;
    }
}
