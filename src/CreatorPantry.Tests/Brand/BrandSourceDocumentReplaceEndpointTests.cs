using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Gateways;
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
/// <c>POST .../brand-source-documents/{id}/versions</c> through the real Gateway, over an in-memory object
/// store and a scanner double: that a replacement adds a version and takes nothing away.
/// </summary>
/// <remarks>
/// <para>
/// Most of this file is about the <em>old</em> version, because that is what a replacement can get wrong. Its
/// row, its bytes, its download, its extracted text and the guide version that cites it are all asserted
/// after the replacement, not before.
/// </para>
/// <para>
/// <strong>What SQLite cannot show here.</strong> <c>RowVersion</c> is filled on insert and does not move on
/// update (see <see cref="SqliteModelCustomizer"/>), so a token does not go stale by itself in this harness.
/// The conflict tests therefore quote a token that is well formed and simply is not this document's, which is
/// the same refusal from the application's side. The genuine lost race, where two writers read the same row
/// and one loses at the save, belongs to <see cref="BrandSourceDocumentSqlServerTests"/>.
/// </para>
/// </remarks>
public sealed class BrandSourceDocumentReplaceEndpointTests : IAsyncLifetime
{
    private const string ContributorEmail = "source-replace-contributor-a@example.com";

    private const string Password = "correct horse battery";

    private readonly InMemoryPrivateObjectStore _store = new();

    private readonly FakeMalwareScanGateway _scanner = new();

    private readonly AuditFault _auditFault = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(_scanner);

            // The real writer, behind a switch that makes the save after the object write fail.
            var audit = services.Single(descriptor => descriptor.ServiceType == typeof(IAuditWriter));
            services.Remove(audit);
            services.Add(new ServiceDescriptor(
                typeof(IAuditWriter),
                provider => new FaultingAuditWriter(
                    audit.ImplementationFactory is { } factory
                        ? (IAuditWriter)factory(provider)
                        : (IAuditWriter)ActivatorUtilities.CreateInstance(provider, audit.ImplementationType!),
                    _auditFault),
                audit.Lifetime));
        });

        var userId = await _fixture.Api.CreateUserAsync(ContributorEmail, Password);
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            UserId = userId,
            Role = WorkspaceRole.Contributor,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---- Success ----

    [Fact]
    public async Task Replacing_adds_version_two_and_leaves_version_one_whole()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var original = BrandSourceSampleFiles.Pdf("the first file");
        var seeded = await UploadAsync(client, _fixture.WorkspaceA, original, tags: ["Launch"]);
        var firstKey = Assert.Single(_store.Keys);

        var replacement = BrandSourceSampleFiles.Png();
        var response = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(replacement, "Second Draft.png", token: seeded.Token));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync(cancellation);
        var body = JsonDocument.Parse(raw).RootElement;

        // The same document at its next version: the id does not change and neither does the description.
        Assert.Equal(seeded.Id, body.GetProperty("id").GetGuid());
        Assert.EndsWith($"{SourcesIn(_fixture.WorkspaceA)}/{seeded.Id}", response.Headers.Location!.ToString());
        Assert.Equal("House style", body.GetProperty("title").GetString());
        Assert.Equal("StyleGuide", body.GetProperty("documentType").GetString());
        Assert.Equal("Voice", body.GetProperty("purpose").GetString());
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.Equal(["Launch"], body.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));

        var version = body.GetProperty("currentVersion");
        Assert.Equal(2, version.GetProperty("versionNumber").GetInt32());

        // Established from the new bytes: the replacement is a PNG whatever the first file was.
        Assert.Equal("image/png", version.GetProperty("mediaType").GetString());
        Assert.Equal(replacement.Length, version.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("Second Draft.png", version.GetProperty("originalFileName").GetString());
        Assert.NotEqual(seeded.VersionId, version.GetProperty("id").GetGuid());
        Assert.DoesNotContain("workspaces/", raw);
        Assert.DoesNotContain("objectKey", raw, StringComparison.OrdinalIgnoreCase);

        // Two objects, not one overwritten: the first key is still there, holding the first bytes.
        Assert.Equal(2, _store.Keys.Count);
        Assert.Contains(firstKey, _store.Keys);
        Assert.Equal(original, await BytesOfAsync(_fixture.WorkspaceA, firstKey));

        var rows = await InScopeAsync(_fixture.WorkspaceA, services => services
            .GetRequiredService<CreatorPantryDbContext>().BrandSourceDocumentVersions
            .OrderBy(row => row.VersionNumber)
            .ToListAsync(cancellation));
        Assert.Equal([1, 2], rows.Select(row => row.VersionNumber));
        Assert.Equal(firstKey, rows[0].ObjectKey);
        Assert.NotEqual(rows[0].ObjectKey, rows[1].ObjectKey);
        Assert.Equal("application/pdf", rows[0].MediaType);
        Assert.Equal("house-style.pdf", rows[0].OriginalFileName);

        var document = await InScopeAsync(_fixture.WorkspaceA, services => services
            .GetRequiredService<CreatorPantryDbContext>().BrandSourceDocuments.SingleAsync(cancellation));
        Assert.Equal(2, document.CurrentVersionNumber);
    }

    /// <summary>Both versions download, each as itself. The point of keeping the row is keeping the bytes.</summary>
    [Fact]
    public async Task Both_versions_still_download_as_the_files_they_were()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var original = BrandSourceSampleFiles.Pdf("the first file");
        var replacement = BrandSourceSampleFiles.Png();
        var seeded = await UploadAsync(client, _fixture.WorkspaceA, original);

        await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, Form(replacement, "redraw.png", token: seeded.Token));

        var first = await client.GetAsync(ContentIn(_fixture.WorkspaceA, seeded.Id, 1), cancellation);
        var second = await client.GetAsync(ContentIn(_fixture.WorkspaceA, seeded.Id, 2), cancellation);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("application/pdf", first.Content.Headers.ContentType!.MediaType);
        Assert.Equal("image/png", second.Content.Headers.ContentType!.MediaType);
        Assert.Equal(original, await first.Content.ReadAsByteArrayAsync(cancellation));
        Assert.Equal(replacement, await second.Content.ReadAsByteArrayAsync(cancellation));

        // One version, one name: the version number is in it, so the two downloads cannot collide on disk.
        Assert.Equal("house-style-v1.pdf", first.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("house-style-v2.png", second.Content.Headers.ContentDisposition!.FileName);

        // Immutable, so each has its own strong tag, and they are not the same tag.
        Assert.NotEqual(first.Headers.ETag!.Tag, second.Headers.ETag!.Tag);
        Assert.Equal(0, _store.OpenReads);
    }

    [Fact]
    public async Task A_replacement_is_audited_as_the_move_from_one_version_to_the_next()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);

        await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Png(), "redraw.png", token: seeded.Token));

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AuditLogs
            .IgnoreQueryFilters()
            .SingleAsync(log => log.Action == BrandAuditActions.SourceDocumentReplaced, cancellation);

        Assert.Equal(_fixture.WorkspaceA.Id, audit.WorkspaceId);
        Assert.Equal(seeded.Id.ToString("D"), audit.ResourceId);
        Assert.Equal("1", audit.BeforeReference);
        Assert.Equal("2", audit.AfterReference);

        // State references only: never the title the creator gave it, nor the name of either file.
        Assert.DoesNotContain("House", audit.Summary);
        Assert.DoesNotContain("redraw", audit.Summary);
    }

    [Fact]
    public async Task A_second_replacement_numbers_the_third_version()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);

        await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token));
        var third = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id,
            Form(BrandSourceSampleFiles.Pdf("three"), token: await TokenOfAsync(client, seeded.Id)));

        Assert.Equal(HttpStatusCode.Created, third.StatusCode);
        Assert.Equal(3, (await BodyOf(third)).GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
        Assert.Equal(3, _store.Keys.Count);
    }

    // ---- What the new version does not inherit, and what the old one keeps ----

    [Fact]
    public async Task The_new_version_has_no_extraction_and_the_old_one_keeps_its_own()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);
        await SeedExtractionAsync(seeded.VersionId);

        await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token));

        // The document now reads as unextracted, because its current version is: extraction is a fact about a
        // version's bytes, and these are different bytes.
        var detail = await BodyOf(await client.GetAsync(DocumentIn(_fixture.WorkspaceA, seeded.Id), cancellation));
        Assert.Equal(2, detail.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
        Assert.Equal("NotExtracted", detail.GetProperty("extraction").GetProperty("state").GetString());

        // And the old version's extraction is untouched, still pointing at the version it was made from.
        var extraction = await InScopeAsync(_fixture.WorkspaceA, services => services
            .GetRequiredService<CreatorPantryDbContext>().BrandSourceExtractions.SingleAsync(cancellation));
        Assert.Equal(seeded.VersionId, extraction.BrandSourceDocumentVersionId);
        Assert.Equal(BrandSourceExtractionStatus.Succeeded, extraction.Status);
    }

    /// <summary>
    /// The restriction this whole operation is shaped around: a guide version cites a <em>source version</em>,
    /// so replacing the upload cannot change what it says it was written from, and nothing here marks it
    /// stale — that is for explicit staleness rules, which do not exist yet.
    /// </summary>
    [Fact]
    public async Task An_approved_guide_version_still_cites_the_exact_upload_it_was_written_from()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);
        var guideVersionId = await SeedApprovedGuideCitingAsync(seeded.VersionId);

        var response = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var (link, approvals, guideVersions) = await InScopeAsync(_fixture.WorkspaceA, async services =>
        {
            var db = services.GetRequiredService<CreatorPantryDbContext>();
            return (
                await db.BrandStyleGuideSourceLinks.SingleAsync(cancellation),
                await db.BrandStyleGuideApprovals.CountAsync(cancellation),
                await db.BrandStyleGuideVersions.CountAsync(cancellation));
        });

        // Still version 1's id, not the document's and not the new version's.
        Assert.Equal(seeded.VersionId, link.BrandSourceDocumentVersionId);
        Assert.Equal(guideVersionId, link.BrandStyleGuideVersionId);

        // And the approval is intact: the replacement neither revoked it nor wrote a new guide version.
        Assert.Equal(1, approvals);
        Assert.Equal(1, guideVersions);

        // The cited version's bytes are still readable, which is what the citation is worth.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(ContentIn(_fixture.WorkspaceA, seeded.Id, 1), cancellation)).StatusCode);
    }

    // ---- Replay ----

    [Fact]
    public async Task Repeating_a_replacement_with_its_key_replays_the_response_and_adds_no_version()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);
        var file = BrandSourceSampleFiles.Pdf("two");
        var key = Key();

        var first = await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, Form(file, token: seeded.Token), key);
        var second = await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, Form(file, token: seeded.Token), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(first.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal("true", second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());

        var original = await BodyOf(first);
        var replayed = await BodyOf(second);
        Assert.Equal(
            original.GetProperty("currentVersion").GetProperty("id").GetGuid(),
            replayed.GetProperty("currentVersion").GetProperty("id").GetGuid());
        Assert.Equal(2, replayed.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());

        // Two objects and two versions, not three: the replay stored nothing.
        Assert.Equal(2, _store.Keys.Count);
        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_key_reused_for_different_bytes_is_refused_and_adds_no_version()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);
        var key = Key();

        await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token), key);
        var reused = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("three"), token: seeded.Token), key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, Code(await BodyOf(reused)));
        Assert.Equal(2, _store.Keys.Count);
        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_replacement_without_a_key_is_refused_before_anything_is_stored()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsync(
            VersionsIn(_fixture.WorkspaceA, seeded.Id),
            Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token),
            idempotencyKey: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, Code(await BodyOf(response)));
        await AssertUnchangedAsync(seeded);
    }

    // ---- Concurrency ----

    [Fact]
    public async Task A_token_that_is_not_this_documents_is_a_conflict_and_nothing_is_stored()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);
        var scansBefore = _scanner.Scans;

        var response = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: SomeOtherToken()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceConflict, Code(await BodyOf(response)));

        // Refused before the file was read, let alone scanned or stored.
        Assert.Equal(scansBefore, _scanner.Scans);
        await AssertUnchangedAsync(seeded);
    }

    [Fact]
    public async Task A_missing_or_malformed_token_is_a_request_error_by_field()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);
        var scansBefore = _scanner.Scans;

        var missing = await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two")));
        var malformed = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: "not-a-token"));

        foreach (var response in new[] { missing, malformed })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await BodyOf(response);
            Assert.Equal(BrandErrorCodes.SourceInvalidRequest, Code(body));
            Assert.True(body.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
        }

        Assert.Equal(scansBefore, _scanner.Scans);
        await AssertUnchangedAsync(seeded);
    }

    [Fact]
    public async Task A_missing_file_is_reported_by_field()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);

        var response = await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, Form(file: null, token: seeded.Token));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.SourceInvalidRequest, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("file", out _));
        await AssertUnchangedAsync(seeded);
    }

    // ---- Status ----

    [Fact]
    public async Task An_archived_document_refuses_a_replacement_and_says_to_restore_it()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);
        await SetStatusAsync(seeded.Id, BrandSourceDocumentStatus.Archived, DateTimeOffset.UtcNow);

        var response = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceArchivedConflict, Code(await BodyOf(response)));

        // Not a 404: a shelved document still reads and still downloads, so pretending it is gone would lie.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(
            DocumentIn(_fixture.WorkspaceA, seeded.Id), TestContext.Current.CancellationToken)).StatusCode);
        await AssertUnchangedAsync(seeded);
    }

    [Fact]
    public async Task A_removed_document_an_unknown_id_and_another_workspaces_document_are_one_answer()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var seeded = await UploadAsync(ownerA, _fixture.WorkspaceA);
        var removed = await UploadAsync(ownerA, _fixture.WorkspaceA, BrandSourceSampleFiles.Pdf("doomed"));
        await SetStatusAsync(removed.Id, BrandSourceDocumentStatus.Removed, null);

        var intoRemoved = await ReplaceAsync(
            ownerA, _fixture.WorkspaceA, removed.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: removed.Token));
        var intoUnknown = await ReplaceAsync(
            ownerA, _fixture.WorkspaceA, Guid.NewGuid(), Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token));

        // B's owner is a member of B, so this reaches the route and is refused by the document's invisibility.
        var intoOtherWorkspace = await ReplaceAsync(
            ownerB, _fixture.WorkspaceB, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token));

        foreach (var response in new[] { intoRemoved, intoUnknown, intoOtherWorkspace })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(BrandErrorCodes.SourceNotFound, Code(await BodyOf(response)));
        }

        await AssertUnchangedAsync(seeded);
    }

    // ---- The file is accepted on exactly the upload's terms ----

    [Theory]
    [InlineData("exe", "invoice.pdf", "application/pdf", BrandErrorCodes.SourceFileUnsupported)]
    [InlineData("png", "photo.jpg", "image/jpeg", BrandErrorCodes.SourceFileUnsupported)]
    [InlineData("macro", "guide.docx", BrandSourceSampleFiles.DocxMediaType, BrandErrorCodes.SourceFileUnsupported)]
    [InlineData("damaged", "house-style.pdf", "application/pdf", BrandErrorCodes.SourceFileCorrupt)]
    public async Task A_replacement_file_is_inspected_as_strictly_as_a_first_upload(
        string kind, string fileName, string declaredType, string expectedCode)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);
        var scansBefore = _scanner.Scans;

        var bytes = kind switch
        {
            "exe" => BrandSourceSampleFiles.Executable(),
            "png" => BrandSourceSampleFiles.Png(),
            "macro" => BrandSourceSampleFiles.Docx(withMacros: true),
            _ => BrandSourceSampleFiles.PdfWithoutTrailer(),
        };

        var response = await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, Form(bytes, fileName, declaredType, seeded.Token));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(expectedCode, Code(await BodyOf(response)));

        // Refused on its bytes alone: never handed to the scanner, let alone stored.
        Assert.Equal(scansBefore, _scanner.Scans);
        await AssertUnchangedAsync(seeded);
    }

    [Fact]
    public async Task A_replacement_over_its_formats_limit_is_refused_by_the_api()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);

        var response = await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, Form(
            BrandSourceSampleFiles.Pdf(padding: (int)BrandPolicy.SourceUploadMaxBytes), token: seeded.Token));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceFileTooLarge, Code(await BodyOf(response)));
        await AssertUnchangedAsync(seeded);
    }

    [Fact]
    public async Task A_replacement_larger_than_the_default_body_limit_passes_the_gateway_on_this_route()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);
        var pdf = BrandSourceSampleFiles.Pdf(padding: (int)ServiceDefaults.EdgeHardening.DefaultMaxRequestBodyBytes + 1024);

        var response = await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, Form(pdf, token: seeded.Token));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_replacement_the_scanner_refuses_is_rejected_without_saying_what_was_found()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);

        var response = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Infected(), token: seeded.Token));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(BrandErrorCodes.SourceFileRejected, Code(JsonDocument.Parse(raw).RootElement));
        Assert.DoesNotContain(BrandSourceSampleFiles.MalwareMarker, raw);
        await AssertUnchangedAsync(seeded);
    }

    [Fact]
    public async Task With_no_scan_verdict_to_be_had_the_replacement_is_refused_rather_than_accepted_unscanned()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);
        var key = Key();
        _scanner.Unavailable = true;

        var refused = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token), key);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceScanUnavailable, Code(await BodyOf(refused)));
        await AssertUnchangedAsync(seeded);

        // The refusal was not remembered: the same request with the same key goes through once scanning is back.
        _scanner.Unavailable = false;
        var retried = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token), key);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.False(retried.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
    }

    // ---- Blob failure and compensation ----

    [Fact]
    public async Task When_storage_cannot_be_reached_no_version_is_written_and_a_retry_with_the_same_key_succeeds()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);
        var key = Key();
        _store.Unavailable = true;

        var refused = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token), key);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceStorageUnavailable, Code(await BodyOf(refused)));

        _store.Unavailable = false;
        await AssertUnchangedAsync(seeded);

        var retried = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token), key);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.False(retried.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal(2, _store.Keys.Count);
        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceA));
    }

    /// <summary>
    /// The compensation that matters most here: the failed attempt's object goes, and the version the document
    /// already had keeps both its row and its bytes.
    /// </summary>
    [Fact]
    public async Task When_the_save_fails_after_the_object_was_written_only_the_new_object_is_removed()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var original = BrandSourceSampleFiles.Pdf("the first file");
        var seeded = await UploadAsync(client, _fixture.WorkspaceA, original);
        var firstKey = Assert.Single(_store.Keys);
        var key = Key();
        _auditFault.Armed = true;

        var failed = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token), key);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        // The fault fires after the object write, so this is compensation and not a write that never happened.
        Assert.Equal(1, _auditFault.Fired);
        Assert.Equal(firstKey, Assert.Single(_store.Keys));
        Assert.Equal(original, await BytesOfAsync(_fixture.WorkspaceA, firstKey));
        await AssertUnchangedAsync(seeded);

        _auditFault.Armed = false;
        var retried = await ReplaceAsync(
            client, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token), key);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(2, _store.Keys.Count);
        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceA));
    }

    // ---- Policy ----

    [Fact]
    public async Task A_contributor_cannot_replace()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(owner, _fixture.WorkspaceA);
        var scansBefore = _scanner.Scans;

        using var contributor = await _fixture.SignInAsync(ContributorEmail, Password, TestContext.Current.CancellationToken);
        var response = await ReplaceAsync(
            contributor, _fixture.WorkspaceA, seeded.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: seeded.Token));

        // The route's Editor policy refuses first, so the answer carries the edge's own code rather than
        // the facade's. The facade keeps its check for the callers that do not come through a controller.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(scansBefore, _scanner.Scans);
        await AssertUnchangedAsync(seeded);
    }

    // ---- Isolation ----

    [Fact]
    public async Task One_workspace_cannot_replace_the_others_document_or_reach_its_objects()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        var inA = await UploadAsync(ownerA, _fixture.WorkspaceA, BrandSourceSampleFiles.Pdf("a's file"));
        var inB = await UploadAsync(ownerB, _fixture.WorkspaceB, BrandSourceSampleFiles.Pdf("b's file"));

        // A's owner is a member of A only, so naming A's document under B's slug is not theirs to reach, and
        // it answers exactly as a workspace that was never created does.
        var intoBsRoute = await ReplaceAsync(
            ownerA, _fixture.WorkspaceB, inA.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: inA.Token));
        var intoNowhere = await ownerA.PostAsync(
            $"/api/v1/workspaces/no-such-kitchen/brand-source-documents/{inA.Id}/versions",
            Form(BrandSourceSampleFiles.Pdf("two"), token: inA.Token),
            Key(),
            cancellation);

        Assert.Equal(HttpStatusCode.NotFound, intoBsRoute.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, intoNowhere.StatusCode);
        Assert.Equal(Code(await BodyOf(intoNowhere)), Code(await BodyOf(intoBsRoute)));

        // B's owner, who is a member of B, cannot replace A's document through B's own route either: the
        // document is not B's, so it does not exist there.
        var fromB = await ReplaceAsync(ownerB, _fixture.WorkspaceB, inA.Id, Form(BrandSourceSampleFiles.Pdf("two"), token: inA.Token));
        Assert.Equal(HttpStatusCode.NotFound, fromB.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceNotFound, Code(await BodyOf(fromB)));

        // Each workspace replaces its own, and the two sets of objects stay apart.
        Assert.Equal(HttpStatusCode.Created, (await ReplaceAsync(
            ownerA, _fixture.WorkspaceA, inA.Id, Form(BrandSourceSampleFiles.Pdf("a's second"), token: inA.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ReplaceAsync(
            ownerB, _fixture.WorkspaceB, inB.Id, Form(BrandSourceSampleFiles.Pdf("b's second"), token: inB.Token))).StatusCode);

        Assert.Equal(2, _store.Keys.Count(key => key.StartsWith($"workspaces/{_fixture.WorkspaceA.Id:N}/", StringComparison.Ordinal)));
        Assert.Equal(2, _store.Keys.Count(key => key.StartsWith($"workspaces/{_fixture.WorkspaceB.Id:N}/", StringComparison.Ordinal)));

        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceA));
        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceB));

        var versionsInA = await InScopeAsync(_fixture.WorkspaceA, services => services
            .GetRequiredService<CreatorPantryDbContext>().BrandSourceDocumentVersions.ToListAsync(cancellation));
        Assert.All(versionsInA, row => Assert.Equal(_fixture.WorkspaceA.Id, row.WorkspaceId));
        Assert.All(versionsInA, row => Assert.Equal(inA.Id, row.BrandSourceDocumentId));
    }


    /// <summary>
    /// The replay path is the one way a refusal could hand back another workspace's document metadata, so the
    /// scope that prevents it is worth a test of its own: an idempotency record is keyed on user, workspace,
    /// operation and key, and only the workspace distinguishes these two requests.
    /// </summary>
    [Fact]
    public async Task One_person_using_one_key_in_both_workspaces_replaces_each_document_rather_than_replaying_the_first()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        var inA = await UploadAsync(ownerA, _fixture.WorkspaceA, BrandSourceSampleFiles.Pdf("a's file"));

        // One person in both workspaces, which is what makes the user part of the scope identical.
        await AddMemberAsync(_fixture.WorkspaceB, _fixture.WorkspaceA.OwnerEmail, WorkspaceRole.Editor);
        var inB = await UploadAsync(ownerA, _fixture.WorkspaceB, BrandSourceSampleFiles.Pdf("b's file"));

        // The same key and the same bytes, so the operation and the file are identical too.
        var key = Key();
        var file = BrandSourceSampleFiles.Pdf("the replacement");

        var replacedInA = await ReplaceAsync(ownerA, _fixture.WorkspaceA, inA.Id, Form(file, token: inA.Token), key);
        var replacedInB = await ReplaceAsync(ownerA, _fixture.WorkspaceB, inB.Id, Form(file, token: inB.Token), key);

        Assert.Equal(HttpStatusCode.Created, replacedInA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replacedInB.StatusCode);

        // The second is a real replacement of B's document, not a replay of the answer about A's.
        Assert.False(replacedInB.Headers.Contains(IdempotencyPolicy.ReplayedHeader));

        var bodyA = await BodyOf(replacedInA);
        var bodyB = await BodyOf(replacedInB);
        Assert.Equal(inA.Id, bodyA.GetProperty("id").GetGuid());
        Assert.Equal(inB.Id, bodyB.GetProperty("id").GetGuid());
        Assert.NotEqual(
            bodyA.GetProperty("currentVersion").GetProperty("id").GetGuid(),
            bodyB.GetProperty("currentVersion").GetProperty("id").GetGuid());

        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceA));
        Assert.Equal(2, await VersionCountAsync(_fixture.WorkspaceB));
    }

    // ---- Helpers ----

    private static string SourcesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents";

    private static string DocumentIn(SeededWorkspace workspace, Guid documentId) => $"{SourcesIn(workspace)}/{documentId}";

    private static string VersionsIn(SeededWorkspace workspace, Guid documentId) => $"{DocumentIn(workspace, documentId)}/versions";

    private static string ContentIn(SeededWorkspace workspace, Guid documentId, int versionNumber) =>
        $"{VersionsIn(workspace, documentId)}/{versionNumber}/content";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static string Key() => Guid.NewGuid().ToString("N");

    /// <summary>A token this API would have issued, for a row that is not this one.</summary>
    private static string SomeOtherToken() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray().AsSpan(0, BrandConcurrencyToken.ByteLength));

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private static MultipartFormDataContent Form(
        byte[]? file,
        string fileName = "house-style.pdf",
        string declaredType = "application/octet-stream",
        string? token = null)
    {
        var form = new MultipartFormDataContent();

        if (file is not null)
        {
            var part = new ByteArrayContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue(declaredType);
            form.Add(part, "file", fileName);
        }

        if (token is not null)
        {
            form.Add(new StringContent(token), "expectedConcurrencyToken");
        }

        return form;
    }

    private Task<HttpResponseMessage> ReplaceAsync(
        GatewayClient client, SeededWorkspace workspace, Guid documentId, MultipartFormDataContent form, string? key = null) =>
        client.PostAsync(VersionsIn(workspace, documentId), form, key ?? Key(), TestContext.Current.CancellationToken);

    /// <summary>Uploads one document and returns what a replacement of it has to quote.</summary>
    private async Task<SeededDocument> UploadAsync(
        GatewayClient client, SeededWorkspace workspace, byte[]? file = null, params string[] tags)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(file ?? BrandSourceSampleFiles.Pdf("house style"));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "house-style.pdf");
        form.Add(new StringContent("House style"), "title");
        form.Add(new StringContent("StyleGuide"), "documentType");
        form.Add(new StringContent("Voice"), "purpose");

        foreach (var tag in tags)
        {
            form.Add(new StringContent(tag), "tags");
        }

        var response = await client.PostAsync(SourcesIn(workspace), form, Key(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await BodyOf(response);

        return new SeededDocument(
            workspace,
            body.GetProperty("id").GetGuid(),
            body.GetProperty("currentVersion").GetProperty("id").GetGuid(),
            body.GetProperty("concurrencyToken").GetString()!);
    }

    private async Task<string> TokenOfAsync(GatewayClient client, Guid documentId) =>
        (await BodyOf(await client.GetAsync(DocumentIn(_fixture.WorkspaceA, documentId), TestContext.Current.CancellationToken)))
            .GetProperty("concurrencyToken").GetString()!;

    /// <summary>One version, the one that was uploaded, and its bytes still in place.</summary>
    private async Task AssertUnchangedAsync(SeededDocument seeded)
    {
        var versions = await InScopeAsync(seeded.Workspace, services => services
            .GetRequiredService<CreatorPantryDbContext>().BrandSourceDocumentVersions
            .Where(row => row.BrandSourceDocumentId == seeded.Id)
            .ToListAsync(TestContext.Current.CancellationToken));

        var only = Assert.Single(versions);
        Assert.Equal(1, only.VersionNumber);
        Assert.Equal(seeded.VersionId, only.Id);
        Assert.Contains(only.ObjectKey, _store.Keys);

        var document = await InScopeAsync(seeded.Workspace, services => services
            .GetRequiredService<CreatorPantryDbContext>().BrandSourceDocuments
            .SingleAsync(row => row.Id == seeded.Id, TestContext.Current.CancellationToken));
        Assert.Equal(1, document.CurrentVersionNumber);
    }

    private Task<int> VersionCountAsync(SeededWorkspace workspace) => InScopeAsync(workspace, services => services
        .GetRequiredService<CreatorPantryDbContext>().BrandSourceDocumentVersions.CountAsync(TestContext.Current.CancellationToken));

    private Task<byte[]> BytesOfAsync(SeededWorkspace workspace, string objectKey) => InScopeAsync(workspace, async services =>
    {
        await using var content = await services.GetRequiredService<IBrandSourceObjectGateway>()
            .OpenReadAsync(objectKey, TestContext.Current.CancellationToken);
        using var buffer = new MemoryStream();
        await content!.Content.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        return buffer.ToArray();
    });


    private async Task AddMemberAsync(SeededWorkspace workspace, string email, WorkspaceRole role)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await db.Users.Where(user => user.Email == email)
            .Select(user => user.Id).SingleAsync(TestContext.Current.CancellationToken);

        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = userId,
            Role = role,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider);
    }

    private Task SetStatusAsync(Guid documentId, BrandSourceDocumentStatus status, DateTimeOffset? archivedAt) =>
        InScopeAsync(_fixture.WorkspaceA, async services =>
        {
            var db = services.GetRequiredService<CreatorPantryDbContext>();
            var document = await db.BrandSourceDocuments.SingleAsync(
                candidate => candidate.Id == documentId, TestContext.Current.CancellationToken);

            document.Status = status;
            document.ArchivedAt = archivedAt;

            // A tombstone names when and who — CK_BrandSourceDocuments_Removed_Consistent refuses one that
            // does not, and refuses the pair on anything that is not removed.
            var removed = status == BrandSourceDocumentStatus.Removed;
            document.RemovedAt = removed ? DateTimeOffset.UtcNow : null;
            document.RemovedByMembershipId = removed ? Guid.NewGuid() : null;

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

    /// <summary>A succeeded extraction of one version, as the worker would eventually leave one.</summary>
    private Task SeedExtractionAsync(Guid versionId) => InScopeAsync(_fixture.WorkspaceA, async services =>
    {
        var db = services.GetRequiredService<CreatorPantryDbContext>();
        db.BrandSourceExtractions.Add(new BrandSourceExtraction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            BrandSourceDocumentVersionId = versionId,
            Ordinal = 1,
            Status = BrandSourceExtractionStatus.Succeeded,
            Origin = BrandSourceExtractionOrigin.Extracted,
            ExtractedTextObjectKey = BrandSourceObjectKey.ForExtractedText(
                _fixture.WorkspaceA.Id, Guid.NewGuid(), versionId, 1),
            ContentChecksum = "sha256:" + new string('a', 64),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return 0;
    });

    /// <summary>An approved guide version pinned to one source version. Returns the guide version's id.</summary>
    private Task<Guid> SeedApprovedGuideCitingAsync(Guid sourceVersionId) => InScopeAsync(_fixture.WorkspaceA, async services =>
    {
        var db = services.GetRequiredService<CreatorPantryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var membershipId = Guid.NewGuid();
        var guideId = Guid.NewGuid();
        var guideVersionId = Guid.NewGuid();

        db.BrandStyleGuides.Add(new BrandStyleGuide
        {
            Id = guideId,
            WorkspaceId = _fixture.WorkspaceA.Id,
            DisplayName = "Everyday voice",
            Status = BrandStyleGuideStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByMembershipId = membershipId,
            UpdatedByMembershipId = membershipId,
        });

        db.BrandStyleGuideVersions.Add(new BrandStyleGuideVersion
        {
            Id = guideVersionId,
            WorkspaceId = _fixture.WorkspaceA.Id,
            BrandStyleGuideId = guideId,
            VersionNumber = 1,
            CreatedByMembershipId = membershipId,
            CreatedAt = now,
            SourceLinks =
            [
                new BrandStyleGuideSourceLink
                {
                    WorkspaceId = _fixture.WorkspaceA.Id,
                    BrandStyleGuideVersionId = guideVersionId,
                    BrandSourceDocumentVersionId = sourceVersionId,
                },
            ],
        });

        db.BrandStyleGuideApprovals.Add(new BrandStyleGuideApproval
        {
            WorkspaceId = _fixture.WorkspaceA.Id,
            BrandStyleGuideVersionId = guideVersionId,
            ApprovedByMembershipId = membershipId,
            ApprovedAt = now,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return guideVersionId;
    });

    /// <param name="Token">The <c>concurrencyToken</c> a replacement of this document has to quote.</param>
    private sealed record SeededDocument(SeededWorkspace Workspace, Guid Id, Guid VersionId, string Token);

    private sealed class AuditFault
    {
        public bool Armed { get; set; }

        public int Fired { get; private set; }

        public void ThrowIfArmed(AuditEntry entry)
        {
            if (Armed && entry.Action == BrandAuditActions.SourceDocumentReplaced)
            {
                Fired++;
                throw new InvalidOperationException("The test made this save fail.");
            }
        }
    }

    private sealed class FaultingAuditWriter(IAuditWriter inner, AuditFault fault) : IAuditWriter
    {
        public void Record(AuditEntry entry)
        {
            fault.ThrowIfArmed(entry);
            inner.Record(entry);
        }
    }
}
