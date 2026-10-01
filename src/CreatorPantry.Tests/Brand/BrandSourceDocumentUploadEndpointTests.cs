using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Gateways;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.ServiceDefaults;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <c>POST .../brand-source-documents</c> through the real Gateway, over an in-memory object store and a
/// scanner double: what is accepted and recorded, what is refused and leaves nothing behind, replay, the two
/// storage failures and their compensation, the Editor policy and workspace isolation.
/// </summary>
public sealed class BrandSourceDocumentUploadEndpointTests : IAsyncLifetime
{
    private const string ContributorEmail = "source-contributor-a@example.com";

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

    private static string SourcesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static string Key() => Guid.NewGuid().ToString("N");

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private static MultipartFormDataContent Form(
        byte[]? file,
        string fileName = "house-style.pdf",
        string declaredType = "application/octet-stream",
        string? title = "House style",
        string? documentType = "StyleGuide",
        string? purpose = "Voice",
        string? channelKey = null,
        params string[] tags)
    {
        var form = new MultipartFormDataContent();

        if (file is not null)
        {
            var part = new ByteArrayContent(file);
            part.Headers.ContentType = new MediaTypeHeaderValue(declaredType);
            form.Add(part, "file", fileName);
        }

        void Field(string name, string? value)
        {
            if (value is not null)
            {
                form.Add(new StringContent(value), name);
            }
        }

        Field("title", title);
        Field("documentType", documentType);
        Field("purpose", purpose);
        Field("channelKey", channelKey);

        foreach (var tag in tags)
        {
            Field("tags", tag);
        }

        return form;
    }

    private Task<HttpResponseMessage> UploadAsync(
        GatewayClient client, SeededWorkspace workspace, MultipartFormDataContent form, string? key = null) =>
        client.PostAsync(SourcesIn(workspace), form, key ?? Key(), TestContext.Current.CancellationToken);

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider);
    }

    private Task<int> DocumentCountAsync(SeededWorkspace workspace) => InScopeAsync(workspace, services =>
        services.GetRequiredService<CreatorPantryDbContext>().BrandSourceDocuments.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>Nothing anywhere: no object, no document, no version.</summary>
    private async Task AssertNothingStoredAsync()
    {
        Assert.Empty(_store.Keys);
        Assert.Equal(0, await DocumentCountAsync(_fixture.WorkspaceA));
        Assert.Equal(0, await InScopeAsync(_fixture.WorkspaceA, services =>
            services.GetRequiredService<CreatorPantryDbContext>().BrandSourceDocumentVersions.CountAsync(TestContext.Current.CancellationToken)));
    }

    // ---- Success ----

    [Fact]
    public async Task Uploading_stores_the_object_and_records_the_document_at_version_one()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var pdf = BrandSourceSampleFiles.Pdf();

        // The part claims to be something else entirely; the bytes decide.
        var response = await UploadAsync(client, _fixture.WorkspaceA, Form(
            pdf, @"C:\Users\sam\House Style.pdf", "application/x-msdownload", title: "  House style  ", channelKey: "instagram",
            tags: ["Launch", "long-form"]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync(cancellation);
        var body = JsonDocument.Parse(raw).RootElement;
        var id = body.GetProperty("id").GetGuid();

        Assert.EndsWith($"{SourcesIn(_fixture.WorkspaceA)}/{id}", response.Headers.Location!.ToString());
        Assert.Equal("House style", body.GetProperty("title").GetString());
        Assert.Equal("StyleGuide", body.GetProperty("documentType").GetString());
        Assert.Equal("Voice", body.GetProperty("purpose").GetString());
        Assert.Equal("instagram", body.GetProperty("channelKey").GetString());
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.Equal(new[] { "Launch", "long-form" }, body.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));

        var version = body.GetProperty("currentVersion");
        Assert.Equal(1, version.GetProperty("versionNumber").GetInt32());
        Assert.Equal("application/pdf", version.GetProperty("mediaType").GetString());
        Assert.Equal(pdf.Length, version.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("House Style.pdf", version.GetProperty("originalFileName").GetString());

        // One private object, under this workspace's prefix, and the response says nothing of where.
        var key = Assert.Single(_store.Keys);
        Assert.StartsWith($"workspaces/{_fixture.WorkspaceA.Id:N}/brand-sources/{id:N}/", key);
        Assert.DoesNotContain("workspaces/", raw);
        Assert.DoesNotContain("objectKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("://", raw);

        var stored = await InScopeAsync(_fixture.WorkspaceA, services =>
            services.GetRequiredService<CreatorPantryDbContext>().BrandSourceDocumentVersions.SingleAsync(cancellation));
        Assert.Equal(id, stored.BrandSourceDocumentId);
        Assert.Equal(key, stored.ObjectKey);
        Assert.Equal("application/pdf", stored.MediaType);
        Assert.Equal(pdf.Length, stored.SizeBytes);
        Assert.Equal(_fixture.WorkspaceA.Id, stored.WorkspaceId);

        // The bytes in storage are the bytes that were sent.
        var read = await InScopeAsync(_fixture.WorkspaceA, async services =>
        {
            await using var content = await services.GetRequiredService<IBrandSourceObjectGateway>().OpenReadAsync(key, cancellation);
            using var buffer = new MemoryStream();
            await content!.Content.CopyToAsync(buffer, cancellation);
            return (content.Object.ContentChecksum, Bytes: buffer.ToArray());
        });
        Assert.Equal(pdf, read.Bytes);
        Assert.Equal(stored.ContentChecksum, read.ContentChecksum);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AuditLogs
            .IgnoreQueryFilters()
            .SingleAsync(log => log.ResourceId == id.ToString("D"), cancellation);
        Assert.Equal(BrandAuditActions.SourceDocumentUploaded, audit.Action);
        Assert.Equal(_fixture.WorkspaceA.Id, audit.WorkspaceId);
        Assert.DoesNotContain("House", audit.Summary);
    }

    public static TheoryData<string, string, string> AcceptedFormats() => new()
    {
        { "docx", "guide.docx", BrandSourceSampleFiles.DocxMediaType },
        { "png", "logo.png", "image/png" },
        { "jpeg", "photo.jpeg", "image/jpeg" },
        { "webp", "photo.webp", "image/webp" },
        { "text", "voice.md", "text/markdown" },
        { "text", "voice.txt", "text/plain" },
        { "text", "post.html", "text/html" },
    };

    [Theory]
    [MemberData(nameof(AcceptedFormats))]
    public async Task Each_accepted_format_is_recorded_with_the_media_type_its_bytes_establish(string kind, string fileName, string mediaType)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var bytes = kind switch
        {
            "docx" => BrandSourceSampleFiles.Docx(),
            "png" => BrandSourceSampleFiles.Png(),
            "jpeg" => BrandSourceSampleFiles.Jpeg(),
            "webp" => BrandSourceSampleFiles.Webp(),
            _ => BrandSourceSampleFiles.Text(),
        };

        // Declared as a PDF every time: never the type that is recorded.
        var response = await UploadAsync(client, _fixture.WorkspaceA, Form(bytes, fileName, "application/pdf"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(mediaType, (await BodyOf(response)).GetProperty("currentVersion").GetProperty("mediaType").GetString());
    }

    [Fact]
    public async Task An_editor_may_upload_and_a_reused_tag_keeps_the_spelling_it_already_has()
    {
        using var editor = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: TestContext.Current.CancellationToken);

        var first = await UploadAsync(editor, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf("one"), tags: ["Launch"]));
        var second = await UploadAsync(editor, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf("two"), tags: ["LAUNCH", "Holiday"]));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(new[] { "Launch", "Holiday" }, (await BodyOf(second)).GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));

        var vocabulary = await InScopeAsync(_fixture.WorkspaceA, services =>
            services.GetRequiredService<CreatorPantryDbContext>().BrandSourceTags.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, vocabulary);
    }

    [Fact]
    public async Task A_file_larger_than_the_default_body_limit_passes_the_gateway_on_this_route()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var pdf = BrandSourceSampleFiles.Pdf(padding: (int)EdgeHardening.DefaultMaxRequestBodyBytes + 1024);

        var response = await UploadAsync(client, _fixture.WorkspaceA, Form(pdf));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- Replay ----

    [Fact]
    public async Task Repeating_an_upload_with_its_key_replays_the_response_and_stores_nothing_new()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var pdf = BrandSourceSampleFiles.Pdf();
        var key = Key();

        var first = await UploadAsync(client, _fixture.WorkspaceA, Form(pdf, tags: ["Launch"]), key);
        var second = await UploadAsync(client, _fixture.WorkspaceA, Form(pdf, tags: ["Launch"]), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(first.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal("true", second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());

        var original = await BodyOf(first);
        var replayed = await BodyOf(second);
        Assert.Equal(original.GetProperty("id").GetGuid(), replayed.GetProperty("id").GetGuid());
        Assert.Equal(
            original.GetProperty("currentVersion").GetProperty("id").GetGuid(),
            replayed.GetProperty("currentVersion").GetProperty("id").GetGuid());

        Assert.Single(_store.Keys);
        Assert.Equal(1, await DocumentCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task A_key_reused_for_different_bytes_is_refused_and_stores_nothing_new()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var key = Key();
        await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf("one")), key);

        var second = await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf("two")), key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, Code(await BodyOf(second)));
        Assert.Single(_store.Keys);
        Assert.Equal(1, await DocumentCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task An_upload_without_a_key_is_refused_before_anything_is_stored()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.PostAsync(
            SourcesIn(_fixture.WorkspaceA), Form(BrandSourceSampleFiles.Pdf()), idempotencyKey: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, Code(await BodyOf(response)));
        await AssertNothingStoredAsync();
    }

    // ---- Spoofed, unsupported and damaged ----

    [Theory]
    [InlineData("exe", "invoice.pdf", "application/pdf")]
    [InlineData("exe", "notes.txt", "text/plain")]
    [InlineData("pdf", "notes.txt", "text/plain")]
    [InlineData("png", "photo.jpg", "image/jpeg")]
    [InlineData("zip", "guide.docx", BrandSourceSampleFiles.DocxMediaType)]
    [InlineData("macro", "guide.docx", BrandSourceSampleFiles.DocxMediaType)]
    public async Task A_file_that_is_not_what_its_name_and_declared_type_claim_is_refused(string kind, string fileName, string declaredType)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var bytes = kind switch
        {
            "exe" => BrandSourceSampleFiles.Executable(),
            "pdf" => BrandSourceSampleFiles.Pdf(),
            "png" => BrandSourceSampleFiles.Png(),
            "zip" => BrandSourceSampleFiles.PlainZip(),
            _ => BrandSourceSampleFiles.Docx(withMacros: true),
        };

        var response = await UploadAsync(client, _fixture.WorkspaceA, Form(bytes, fileName, declaredType));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.SourceFileUnsupported, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("file", out _));

        // Refused on its bytes alone: it was never handed to the scanner, let alone stored.
        Assert.Equal(0, _scanner.Scans);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task A_damaged_file_is_refused_as_corrupt()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.PdfWithoutTrailer()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceFileCorrupt, Code(await BodyOf(response)));
        await AssertNothingStoredAsync();
    }

    // ---- Oversized ----

    [Fact]
    public async Task A_request_over_the_routes_body_limit_is_refused_at_the_edge()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var pdf = BrandSourceSampleFiles.Pdf(padding: (int)BrandPolicy.SourceUploadRequestMaxBytes);

        var response = await UploadAsync(client, _fixture.WorkspaceA, Form(pdf));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(EdgeHardening.RequestTooLargeCode, Code(await BodyOf(response)));
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task A_file_over_its_formats_limit_but_inside_the_body_limit_is_refused_by_the_api()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        // Just past 20 MB as a PDF, and just past 5 MB as text: each fits the route and not its format.
        var pdf = await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf(padding: (int)BrandPolicy.SourceUploadMaxBytes)));
        var text = await UploadAsync(client, _fixture.WorkspaceA, Form(
            BrandSourceSampleFiles.Text(new string('a', (int)BrandPolicy.SourceTextUploadMaxBytes + 1)), "voice.txt"));
        var image = await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Png(10_000, 10_000), "logo.png"));

        foreach (var response in new[] { pdf, text, image })
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal(BrandErrorCodes.SourceFileTooLarge, Code(await BodyOf(response)));
        }

        await AssertNothingStoredAsync();
    }

    // ---- Malware ----

    [Fact]
    public async Task A_file_the_scanner_refuses_is_rejected_without_saying_what_was_found()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Infected()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(BrandErrorCodes.SourceFileRejected, Code(JsonDocument.Parse(raw).RootElement));
        Assert.DoesNotContain(BrandSourceSampleFiles.MalwareMarker, raw);
        Assert.Equal(1, _scanner.Scans);
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task With_no_scan_verdict_to_be_had_the_upload_is_refused_rather_than_accepted_unscanned()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        _scanner.Unavailable = true;
        var key = Key();

        var refused = await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf()), key);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceScanUnavailable, Code(await BodyOf(refused)));
        await AssertNothingStoredAsync();

        // The refusal was not remembered: the same request with the same key goes through once scanning is back.
        _scanner.Unavailable = false;
        var retried = await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf()), key);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.False(retried.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
    }

    [Fact]
    public async Task The_scanner_registered_by_default_clears_nothing()
    {
        var services = new ServiceCollection().AddMalwareScanning().BuildServiceProvider();

        var verdict = await services.GetRequiredService<IMalwareScanGateway>()
            .ScanAsync(new MemoryStream(BrandSourceSampleFiles.Pdf()), TestContext.Current.CancellationToken);

        Assert.Equal(MalwareScanVerdict.Unavailable, verdict);
    }

    // ---- Blob failure and compensation ----

    [Fact]
    public async Task When_storage_cannot_be_reached_no_row_is_written_and_a_retry_with_the_same_key_succeeds()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        _store.Unavailable = true;
        var key = Key();

        var refused = await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf()), key);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceStorageUnavailable, Code(await BodyOf(refused)));
        _store.Unavailable = false;
        await AssertNothingStoredAsync();

        var retried = await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf()), key);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.False(retried.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Single(_store.Keys);
        Assert.Equal(1, await DocumentCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task When_the_save_fails_after_the_object_was_written_the_object_is_removed_again()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        _auditFault.Armed = true;
        var key = Key();

        var failed = await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf()), key);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        // The fault fires after the object write, so this is compensation and not a write that never happened.
        Assert.Equal(1, _auditFault.Fired);
        await AssertNothingStoredAsync();

        _auditFault.Armed = false;
        var retried = await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf()), key);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Single(_store.Keys);
        Assert.Equal(1, await DocumentCountAsync(_fixture.WorkspaceA));
    }

    [Fact]
    public async Task Abandoning_removes_an_object_with_no_row_and_keeps_one_whose_row_committed()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);

        // A committed upload: abandoning it — a commit that reported failure but landed — must not touch it.
        var committed = await BodyOf(await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf())));
        var committedDocument = committed.GetProperty("id").GetGuid();
        var committedVersion = committed.GetProperty("currentVersion").GetProperty("id").GetGuid();

        // An object whose row never arrived.
        var orphanDocument = Guid.NewGuid();
        var orphanVersion = Guid.NewGuid();

        await InScopeAsync(_fixture.WorkspaceA, async services =>
        {
            var write = await services.GetRequiredService<IBrandSourceObjectGateway>().PutOriginalAsync(
                orphanDocument, orphanVersion, new MemoryStream(BrandSourceSampleFiles.Pdf("orphan")), "application/pdf", 1024, cancellation);
            Assert.Equal(BrandSourceObjectWriteOutcome.Stored, write.Outcome);

            var dataLayer = services.GetRequiredService<IBrandSourceDocumentDataLayer>();
            await dataLayer.AbandonAsync(committedDocument, committedVersion);
            await dataLayer.AbandonAsync(orphanDocument, orphanVersion);
            return 0;
        });

        var remaining = Assert.Single(_store.Keys);
        Assert.Contains(committedVersion.ToString("N"), remaining);
    }

    // ---- Validation and policy ----

    [Fact]
    public async Task Missing_metadata_and_a_missing_file_are_reported_by_field()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await UploadAsync(client, _fixture.WorkspaceA, Form(
            file: null, title: " ", documentType: null, purpose: null, channelKey: "myspace"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.SourceInvalidRequest, Code(body));

        var errors = body.GetProperty("errors");
        Assert.All(
            new[] { "title", "documentType", "purpose", "channelKey", "file" },
            field => Assert.True(errors.TryGetProperty(field, out _), $"no error for {field}"));
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task Too_many_tags_and_a_repeated_tag_are_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var tooMany = await UploadAsync(client, _fixture.WorkspaceA, Form(
            BrandSourceSampleFiles.Pdf(), tags: [.. Enumerable.Range(0, BrandPolicy.MaxSourceDocumentTags + 1).Select(index => $"tag {index}")]));
        var repeated = await UploadAsync(client, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf(), tags: ["Launch", "launch"]));

        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceInvalidRequest, Code(await BodyOf(repeated)));
        await AssertNothingStoredAsync();
    }

    [Fact]
    public async Task A_contributor_cannot_upload()
    {
        using var contributor = await _fixture.SignInAsync(ContributorEmail, Password, TestContext.Current.CancellationToken);

        var response = await UploadAsync(contributor, _fixture.WorkspaceA, Form(BrandSourceSampleFiles.Pdf()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _scanner.Scans);
        await AssertNothingStoredAsync();
    }

    // ---- Isolation ----

    [Fact]
    public async Task One_workspaces_upload_is_invisible_and_unreachable_from_the_other()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var pdf = BrandSourceSampleFiles.Pdf();
        var key = Key();

        var inA = await BodyOf(await UploadAsync(ownerA, _fixture.WorkspaceA, Form(pdf, tags: ["Launch"]), key));

        // B's owner cannot upload into A: the workspace is not theirs, so it does not exist for them.
        // And it answers exactly as a workspace that was never created does, without the file being read.
        var scansBefore = _scanner.Scans;
        var intoA = await UploadAsync(ownerB, _fixture.WorkspaceA, Form(pdf));
        var intoNowhere = await UploadIntoUnknownWorkspaceAsync(ownerB);
        Assert.Equal(HttpStatusCode.NotFound, intoA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, intoNowhere.StatusCode);
        Assert.Equal(Code(await BodyOf(intoNowhere)), Code(await BodyOf(intoA)));
        Assert.Equal(scansBefore, _scanner.Scans);

        // One person in both workspaces, the same key and the same bytes: the key is scoped to the workspace,
        // so the second is a fresh upload and not a replay of the first.
        await AddMemberAsync(_fixture.WorkspaceB, _fixture.WorkspaceA.OwnerEmail, WorkspaceRole.Editor);
        var sameUserInB = await UploadAsync(ownerA, _fixture.WorkspaceB, Form(pdf, title: "A's owner in B"), key);
        Assert.Equal(HttpStatusCode.Created, sameUserInB.StatusCode);
        Assert.False(sameUserInB.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.NotEqual(inA.GetProperty("id").GetGuid(), (await BodyOf(sameUserInB)).GetProperty("id").GetGuid());

        // The same bytes, name, tag and idempotency key in B are B's own document, not a replay of A's.
        var uploadedInB = await UploadAsync(ownerB, _fixture.WorkspaceB, Form(pdf, tags: ["Launch"]), key);
        Assert.Equal(HttpStatusCode.Created, uploadedInB.StatusCode);
        Assert.False(uploadedInB.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        var inB = await BodyOf(uploadedInB);
        Assert.NotEqual(inA.GetProperty("id").GetGuid(), inB.GetProperty("id").GetGuid());

        // Each workspace's rows and vocabulary are its own.
        foreach (var (workspace, document) in new[] { (_fixture.WorkspaceA, inA), (_fixture.WorkspaceB, inB) })
        {
            var (documents, tags) = await InScopeAsync(workspace, async services =>
            {
                var db = services.GetRequiredService<CreatorPantryDbContext>();
                return (await db.BrandSourceDocuments.ToListAsync(cancellation), await db.BrandSourceTags.ToListAsync(cancellation));
            });

            Assert.Contains(documents, row => row.Id == document.GetProperty("id").GetGuid());
            Assert.All(documents, row => Assert.Equal(workspace.Id, row.WorkspaceId));
            Assert.Equal(workspace.Id, Assert.Single(tags).WorkspaceId);
        }

        Assert.Equal(1, await DocumentCountAsync(_fixture.WorkspaceA));
        Assert.Equal(2, await DocumentCountAsync(_fixture.WorkspaceB));

        // Each object sits under its own workspace's prefix, and neither workspace can open the other's.
        var keyA = Assert.Single(_store.Keys, candidate => candidate.StartsWith($"workspaces/{_fixture.WorkspaceA.Id:N}/", StringComparison.Ordinal));
        var keyB = Assert.Single(_store.Keys, candidate => candidate.Contains(inB.GetProperty("id").GetGuid().ToString("N"), StringComparison.Ordinal));
        Assert.StartsWith($"workspaces/{_fixture.WorkspaceB.Id:N}/", keyB);
        Assert.Equal(3, _store.Keys.Count);

        // From B's scope A's version row does not exist, and A's key deletes nothing even when named outright.
        var versionA = inA.GetProperty("currentVersion").GetProperty("id").GetGuid();
        var seenFromB = await InScopeAsync(_fixture.WorkspaceB, async services =>
        {
            await services.GetRequiredService<IBrandSourceObjectGateway>().DeleteAsync(keyA, cancellation);
            return await services.GetRequiredService<IBrandSourceDocumentRepository>().VersionExistsAsync(versionA, cancellation);
        });
        Assert.False(seenFromB);
        Assert.Contains(keyA, _store.Keys);

        var crossRead = await InScopeAsync(_fixture.WorkspaceB, async services =>
        {
            var gateway = services.GetRequiredService<IBrandSourceObjectGateway>();
            await using var others = await gateway.OpenReadAsync(keyA, cancellation);
            await using var own = await gateway.OpenReadAsync(keyB, cancellation);
            return (Others: others is null, Own: own is not null);
        });
        Assert.True(crossRead.Others);
        Assert.True(crossRead.Own);

        // And abandoning A's upload from B's scope removes nothing: B's data layer cannot name A's object.
        await InScopeAsync(_fixture.WorkspaceB, async services =>
        {
            await services.GetRequiredService<IBrandSourceDocumentDataLayer>().AbandonAsync(
                inA.GetProperty("id").GetGuid(), inA.GetProperty("currentVersion").GetProperty("id").GetGuid());
            return 0;
        });
        Assert.Contains(keyA, _store.Keys);
    }

    private Task<HttpResponseMessage> UploadIntoUnknownWorkspaceAsync(GatewayClient client) => client.PostAsync(
        "/api/v1/workspaces/no-such-kitchen/brand-source-documents",
        Form(BrandSourceSampleFiles.Pdf()),
        Key(),
        TestContext.Current.CancellationToken);

    private async Task AddMemberAsync(SeededWorkspace workspace, string email, WorkspaceRole role)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync(TestContext.Current.CancellationToken);
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

    private sealed class AuditFault
    {
        public bool Armed { get; set; }

        public int Fired { get; private set; }

        public void ThrowIfArmed(AuditEntry entry)
        {
            if (Armed && entry.Action == BrandAuditActions.SourceDocumentUploaded)
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

/// <summary>
/// A scanner double: clean unless the content carries <see cref="BrandSourceSampleFiles.MalwareMarker"/>, and
/// switchable to having no verdict at all.
/// </summary>
internal sealed class FakeMalwareScanGateway : IMalwareScanGateway
{
    public bool Unavailable { get; set; }

    public int Scans { get; private set; }

    public async Task<MalwareScanVerdict> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        Scans++;

        if (Unavailable)
        {
            return MalwareScanVerdict.Unavailable;
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        return buffer.ToArray().AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(BrandSourceSampleFiles.MalwareMarker)) >= 0
            ? MalwareScanVerdict.Infected
            : MalwareScanVerdict.Clean;
    }
}
