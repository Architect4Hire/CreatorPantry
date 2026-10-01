using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
/// <c>GET .../brand-source-documents/{id}</c> and
/// <c>GET .../brand-source-documents/{id}/versions/{n}/content</c> through the real Gateway, over an in-memory
/// object store: the two contracts of 11A.7.
/// </summary>
/// <remarks>
/// <para>
/// Two routes in one file because they are one feature with one disclosure rule, and most of what is worth
/// asserting is that they agree: the same five situations are the same 404, the same document is readable by
/// the same members, and neither response contains an address.
/// </para>
/// <para>
/// The download assertions are mostly about the <em>name</em> and the <em>type</em>, because that is where a
/// download can lie. The bytes themselves are asserted once, byte for byte.
/// </para>
/// </remarks>
public sealed class BrandSourceDocumentDetailEndpointTests : IAsyncLifetime
{
    private const string ViewerEmail = "source-detail-viewer-a@example.com";

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

    // ---- Detail ----

    [Fact]
    public async Task Detail_carries_the_description_the_current_version_and_the_extraction_state()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(client, _fixture.WorkspaceA, tags: ["Launch", "Evergreen"]);

        var body = await BodyOf(await client.GetAsync(
            DocumentIn(_fixture.WorkspaceA, id), TestContext.Current.CancellationToken));

        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal("House style", body.GetProperty("title").GetString());
        Assert.Equal("StyleGuide", body.GetProperty("documentType").GetString());
        Assert.Equal("Voice", body.GetProperty("purpose").GetString());
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.Equal(["Evergreen", "Launch"], body.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));

        var version = body.GetProperty("currentVersion");
        Assert.Equal(1, version.GetProperty("versionNumber").GetInt32());
        Assert.Equal("application/pdf", version.GetProperty("mediaType").GetString());
        Assert.Equal("house-style.pdf", version.GetProperty("originalFileName").GetString());
        Assert.StartsWith("sha256:", version.GetProperty("contentChecksum").GetString()!, StringComparison.Ordinal);

        // No attempt has been made, which is a real answer rather than a null.
        Assert.Equal("NotExtracted", body.GetProperty("extraction").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("extraction").GetProperty("origin").ValueKind);
    }

    /// <summary>The three things a list row withholds, which is the reason this route exists beside it.</summary>
    [Fact]
    public async Task Detail_adds_the_token_the_checksum_and_the_archive_date_that_a_row_withholds()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(client, _fixture.WorkspaceA);

        var detail = await BodyOf(await client.GetAsync(
            DocumentIn(_fixture.WorkspaceA, id), TestContext.Current.CancellationToken));

        Assert.False(string.IsNullOrWhiteSpace(detail.GetProperty("concurrencyToken").GetString()));
        Assert.True(detail.GetProperty("currentVersion").TryGetProperty("contentChecksum", out _));
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("archivedAt").ValueKind);

        var row = Assert.Single((await BodyOf(await client.GetAsync(
            SourcesIn(_fixture.WorkspaceA), TestContext.Current.CancellationToken)))
            .GetProperty("items").EnumerateArray());

        Assert.False(row.TryGetProperty("concurrencyToken", out _));
        Assert.False(row.TryGetProperty("archivedAt", out _));
        Assert.False(row.GetProperty("currentVersion").TryGetProperty("contentChecksum", out _));
    }

    /// <summary>
    /// Asserted against the real object key rather than against field names, which is the stronger claim: a
    /// key nested inside some future field would still be caught.
    /// </summary>
    [Fact]
    public async Task Detail_carries_no_object_key_no_container_and_no_url()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        // Body text that is not the title, so its absence says the bytes were not read rather than only that
        // the title happens to differ.
        var id = await UploadAsync(client, _fixture.WorkspaceA, file: BrandSourceSampleFiles.Pdf("inner-marker-text"));
        var objectKey = Assert.Single(_store.Keys);

        var json = await (await client.GetAsync(
            DocumentIn(_fixture.WorkspaceA, id), TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(objectKey, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BrandSourceObjectKey.Container, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspaceId", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("inner-marker-text", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_viewer_may_read_a_document()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(owner, _fixture.WorkspaceA);

        using var viewer = await _fixture.SignInAsync(
            ViewerEmail, Password, cancellationToken: TestContext.Current.CancellationToken);

        var response = await viewer.GetAsync(
            DocumentIn(_fixture.WorkspaceA, id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Archiving is a shelf, not a deletion: a creator unarchives from the thing they are looking at.</summary>
    [Fact]
    public async Task An_archived_document_reads_normally_and_says_when_it_was_shelved()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(client, _fixture.WorkspaceA);
        var archivedAt = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);

        await SetStatusAsync(id, BrandSourceDocumentStatus.Archived, archivedAt);

        var response = await client.GetAsync(
            DocumentIn(_fixture.WorkspaceA, id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("Archived", body.GetProperty("status").GetString());
        Assert.Equal(archivedAt, body.GetProperty("archivedAt").GetDateTimeOffset());
    }

    /// <summary>A tombstone the library never lists must not be readable either.</summary>
    [Fact]
    public async Task A_removed_document_is_not_found()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(client, _fixture.WorkspaceA);

        await SetStatusAsync(id, BrandSourceDocumentStatus.Removed, archivedAt: null);

        var detail = await client.GetAsync(DocumentIn(_fixture.WorkspaceA, id), TestContext.Current.CancellationToken);
        var download = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id, 1), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceNotFound, (await BodyOf(detail)).GetProperty("code").GetString());

        // The bytes refuse with it. A removed document that still served its file would be a tombstone in
        // name only.
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceNotFound, (await BodyOf(download)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unknown_document_is_not_found()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(
            DocumentIn(_fixture.WorkspaceA, Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- Download ----

    [Fact]
    public async Task The_download_is_the_original_bytes_under_the_stored_media_type()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var file = BrandSourceSampleFiles.Pdf("house style");
        var id = await UploadAsync(client, _fixture.WorkspaceA, file: file);

        var response = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id, 1), cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(file, await response.Content.ReadAsByteArrayAsync(cancellation));
    }

    /// <summary>
    /// The name is built from the title and the stored media type, not echoed from the upload.
    /// </summary>
    /// <remarks>
    /// A JPEG is the case that shows it: the inspector accepts both <c>.jpg</c> and <c>.jpeg</c>, so a file
    /// arriving as <c>hero.jpeg</c> is stored as <c>image/jpeg</c> and downloads as <c>.jpg</c> — one name for
    /// one version, whichever spelling it came under. A larger mismatch is unreachable, because the upload
    /// refuses a file whose bytes are not the format its extension claims.
    /// </remarks>
    [Fact]
    public async Task The_download_name_comes_from_the_title_and_the_real_media_type()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(
            client,
            _fixture.WorkspaceA,
            file: BrandSourceSampleFiles.Jpeg(),
            fileName: "hero.jpeg",
            title: "Autumn Voice & Tone");

        var response = await client.GetAsync(
            ContentIn(_fixture.WorkspaceA, id, 1), TestContext.Current.CancellationToken);

        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("autumn-voice-tone-v1.jpg", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);

        // And the creator's own name is still in the metadata, for display, exactly as it arrived.
        var detail = await BodyOf(await client.GetAsync(
            DocumentIn(_fixture.WorkspaceA, id), TestContext.Current.CancellationToken));
        Assert.Equal("hero.jpeg", detail.GetProperty("currentVersion").GetProperty("originalFileName").GetString());
    }

    /// <summary>
    /// The reason the name is built rather than echoed: a title is creator text, and a header is a place where
    /// a quote or a separator changes the meaning of what follows.
    /// </summary>
    [Fact]
    public async Task A_hostile_title_cannot_reach_the_header()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(
            client, _fixture.WorkspaceA, title: "../../etc/pa\"ss\r\nwd; rm -rf /");

        var response = await client.GetAsync(
            ContentIn(_fixture.WorkspaceA, id, 1), TestContext.Current.CancellationToken);

        var name = response.Content.Headers.ContentDisposition?.FileName;

        Assert.Equal("etc-pa-ss-wd-rm-rf-v1.pdf", name);
        Assert.DoesNotContain('"', name!);
        Assert.DoesNotContain('/', name!);
        Assert.DoesNotContain('\\', name!);
        Assert.DoesNotContain("..", name!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_download_is_private_unsniffable_and_tagged()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            ContentIn(_fixture.WorkspaceA, id, 1), TestContext.Current.CancellationToken);

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));

        // Strong, because a version is immutable: the checksum of its bytes identifies this representation
        // for good, so there is no weak-comparison caveat to carry.
        var tag = response.Headers.ETag;
        Assert.NotNull(tag);
        Assert.False(tag.IsWeak);
        Assert.Contains("sha256:", tag.Tag, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_matching_entity_tag_answers_304_with_no_body()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(client, _fixture.WorkspaceA);
        var path = ContentIn(_fixture.WorkspaceA, id, 1);

        var first = await client.GetAsync(path, cancellation);
        var tag = first.Headers.ETag!;

        var second = await client.GetAsync(
            path, new Dictionary<string, string> { ["If-None-Match"] = tag.ToString() }, cancellation);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync(cancellation));
        Assert.Equal(tag, second.Headers.ETag);
    }

    [Fact]
    public async Task A_stale_entity_tag_sends_the_file()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var file = BrandSourceSampleFiles.Pdf("house style");
        var id = await UploadAsync(client, _fixture.WorkspaceA, file: file);
        var path = ContentIn(_fixture.WorkspaceA, id, 1);

        var response = await client.GetAsync(
            path, new Dictionary<string, string> { ["If-None-Match"] = "\"sha256:0000\"" }, cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(file, await response.Content.ReadAsByteArrayAsync(cancellation));
    }

    /// <summary>
    /// A version number is not a probe. All four of these are one answer, so a caller cannot count a
    /// document's history or learn that one exists where they cannot see it.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_version_the_document_does_not_have_is_not_found(int versionNumber)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            ContentIn(_fixture.WorkspaceA, id, versionNumber), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unknown_document_has_nothing_to_download()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(
            ContentIn(_fixture.WorkspaceA, Guid.NewGuid(), 1), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A committed row whose object is gone. Answered as a fault rather than as a missing document: the
    /// document is plainly there, and telling a creator it is not would be a lie about their own library.
    /// </summary>
    [Fact]
    public async Task A_row_whose_object_is_gone_is_unavailable_rather_than_not_found()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(client, _fixture.WorkspaceA);

        // Behind the application's back, which is the only way this state arises: StoreAsync writes the
        // object before the row and removes it again if the save fails.
        await _store.DeleteAsync(BrandSourceObjectKey.Container, Assert.Single(_store.Keys), cancellation);

        var download = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id, 1), cancellation);
        var detail = await client.GetAsync(DocumentIn(_fixture.WorkspaceA, id), cancellation);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, download.StatusCode);
        Assert.Equal(
            BrandErrorCodes.SourceStorageUnavailable, (await BodyOf(download)).GetProperty("code").GetString());

        // And the metadata still reads, which is what makes 404 the wrong answer above.
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    }

    [Fact]
    public async Task Storage_that_cannot_be_reached_is_unavailable()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(client, _fixture.WorkspaceA);

        _store.Unavailable = true;

        var response = await client.GetAsync(
            ContentIn(_fixture.WorkspaceA, id, 1), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            BrandErrorCodes.SourceStorageUnavailable, (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_viewer_may_download_a_document()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(owner, _fixture.WorkspaceA);

        using var viewer = await _fixture.SignInAsync(
            ViewerEmail, Password, cancellationToken: TestContext.Current.CancellationToken);

        var response = await viewer.GetAsync(
            ContentIn(_fixture.WorkspaceA, id, 1), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>No address is issued for an object, so there is nothing in a download's headers to follow.</summary>
    [Fact]
    public async Task A_download_hands_back_no_address_for_the_object()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(client, _fixture.WorkspaceA);
        var objectKey = Assert.Single(_store.Keys);

        var response = await client.GetAsync(
            ContentIn(_fixture.WorkspaceA, id, 1), TestContext.Current.CancellationToken);

        var headers = string.Join(
            '\n',
            response.Headers.Concat(response.Content.Headers).Select(header => $"{header.Key}: {string.Join(',', header.Value)}"));

        Assert.DoesNotContain(objectKey, headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(BrandSourceObjectKey.Container, headers, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Location"));
    }

    // ---- What the response gives back ----

    /// <summary>
    /// A real provider holds a connection open for a streaming read, so a path that forgets to release one
    /// leaks it per request until the pool is exhausted. Every path is checked, including the two that send
    /// no body — those are exactly the ones where a dispose is easy to forget.
    /// </summary>
    [Fact]
    public async Task Every_path_gives_the_object_store_its_read_back()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(client, _fixture.WorkspaceA);
        var path = ContentIn(_fixture.WorkspaceA, id, 1);

        // 200. The body has to be read to completion before the response is over and the stream released.
        var sent = await client.GetAsync(path, cancellation);
        await sent.Content.ReadAsByteArrayAsync(cancellation);
        Assert.Equal(0, _store.OpenReads);

        // 304: opened to learn the tag, and sent nothing.
        var tag = sent.Headers.ETag!.ToString();
        var notModified = await client.GetAsync(
            path, new Dictionary<string, string> { ["If-None-Match"] = tag }, cancellation);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(0, _store.OpenReads);

        // 404: never opened at all, which is the other way to owe nothing.
        await client.GetAsync(ContentIn(_fixture.WorkspaceA, id, 99), cancellation);
        Assert.Equal(0, _store.OpenReads);

        // 503: the open failed, so there is nothing to give back and nothing left holding one.
        await _store.DeleteAsync(BrandSourceObjectKey.Container, Assert.Single(_store.Keys), cancellation);
        await client.GetAsync(path, cancellation);
        Assert.Equal(0, _store.OpenReads);
    }

    /// <summary>The size is stated, so a client can show progress rather than watch an unbounded stream.</summary>
    [Fact]
    public async Task The_download_states_its_length()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var file = BrandSourceSampleFiles.Pdf("house style");
        var id = await UploadAsync(client, _fixture.WorkspaceA, file: file);

        var response = await client.GetAsync(ContentIn(_fixture.WorkspaceA, id, 1), cancellation);

        Assert.Equal(file.Length, response.Content.Headers.ContentLength);
    }

    /// <summary>
    /// An uploaded HTML file is the one accepted format that can carry script, and three separate things stop
    /// it running.
    /// </summary>
    /// <remarks>
    /// Asserted here rather than taken on trust from the edge, because this is the route that serves creator
    /// bytes under a creator-chosen content type: the day the edge's policy is relaxed for some other reason,
    /// this is where it should be noticed.
    /// </remarks>
    [Fact]
    public async Task An_uploaded_html_file_is_served_so_that_nothing_in_it_can_run()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var id = await UploadAsync(
            client,
            _fixture.WorkspaceA,
            file: BrandSourceSampleFiles.Text("<p>House style</p>"),
            fileName: "house-style.html");

        var response = await client.GetAsync(
            ContentIn(_fixture.WorkspaceA, id, 1), TestContext.Current.CancellationToken);

        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        // Saved rather than rendered, not sniffed into anything else, and nothing it asks for may load.
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains(
            "default-src 'none'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")),
            StringComparison.Ordinal);
    }

    // ---- Two workspaces ----

    /// <summary>
    /// B's real document and version, asked for by A's owner under A's own slug. The ids are real; the answer
    /// must be identical to one invented, on both routes.
    /// </summary>
    [Fact]
    public async Task One_workspace_cannot_read_or_download_another_workspaces_document()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var inB = await UploadAsync(ownerB, _fixture.WorkspaceB);

        // Through A's own slug: B's slug would be refused by the membership policy before the route ran, and
        // would prove nothing about the data.
        var borrowedDetail = await BodyOf(await ownerA.GetAsync(DocumentIn(_fixture.WorkspaceA, inB), cancellation));
        var inventedDetail = await BodyOf(await ownerA.GetAsync(
            DocumentIn(_fixture.WorkspaceA, Guid.NewGuid()), cancellation));

        Assert.Equal(WithoutTrace(inventedDetail), WithoutTrace(borrowedDetail));
        Assert.Equal(BrandErrorCodes.SourceNotFound, borrowedDetail.GetProperty("code").GetString());

        var borrowedContent = await ownerA.GetAsync(ContentIn(_fixture.WorkspaceA, inB, 1), cancellation);
        var inventedContent = await ownerA.GetAsync(
            ContentIn(_fixture.WorkspaceA, Guid.NewGuid(), 1), cancellation);

        Assert.Equal(HttpStatusCode.NotFound, borrowedContent.StatusCode);
        Assert.Equal(WithoutTrace(await BodyOf(inventedContent)), WithoutTrace(await BodyOf(borrowedContent)));

        // And B still reads and downloads its own, so the refusals above are isolation rather than a bad seed.
        Assert.Equal(
            HttpStatusCode.OK,
            (await ownerB.GetAsync(DocumentIn(_fixture.WorkspaceB, inB), cancellation)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await ownerB.GetAsync(ContentIn(_fixture.WorkspaceB, inB, 1), cancellation)).StatusCode);
    }

    /// <summary>
    /// The one way the 503 could have been a probe: B's document with its object removed, asked for through
    /// A's slug.
    /// </summary>
    /// <remarks>
    /// Inside a workspace the 404/503 difference tells a member only what they could already read. Across one
    /// it would be a disclosure — "this id is a document somewhere, and its storage is broken" — so the order
    /// matters: the workspace-filtered lookup must miss before storage is ever touched. Asserted rather than
    /// left to that ordering, because the ordering is exactly what a later edit could reverse.
    /// </remarks>
    [Fact]
    public async Task A_broken_object_in_another_workspace_is_still_only_not_found()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var inB = await UploadAsync(ownerB, _fixture.WorkspaceB);
        await _store.DeleteAsync(BrandSourceObjectKey.Container, Assert.Single(_store.Keys), cancellation);

        var borrowed = await ownerA.GetAsync(ContentIn(_fixture.WorkspaceA, inB, 1), cancellation);
        var invented = await ownerA.GetAsync(ContentIn(_fixture.WorkspaceA, Guid.NewGuid(), 1), cancellation);

        Assert.Equal(HttpStatusCode.NotFound, borrowed.StatusCode);
        Assert.Equal(WithoutTrace(await BodyOf(invented)), WithoutTrace(await BodyOf(borrowed)));

        // And B, who may see it, gets the honest answer about their own broken storage.
        var own = await ownerB.GetAsync(ContentIn(_fixture.WorkspaceB, inB, 1), cancellation);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, own.StatusCode);
    }

    /// <summary>
    /// A real entity tag is a thing a caller can hold without being able to use it: B's tag for B's document,
    /// sent through A's slug, must be a 404 rather than a 304 that confirms the bytes are unchanged.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_entity_tag_cannot_be_used_to_confirm_a_document()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var inB = await UploadAsync(ownerB, _fixture.WorkspaceB);
        var tag = (await ownerB.GetAsync(ContentIn(_fixture.WorkspaceB, inB, 1), cancellation)).Headers.ETag!;

        var response = await ownerA.GetAsync(
            ContentIn(_fixture.WorkspaceA, inB, 1),
            new Dictionary<string, string> { ["If-None-Match"] = tag.ToString() },
            cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// Both workspaces hold a version 1, and neither reaches the other's through the shared number. The
    /// numbers are per document, so a version number is only ever meaningful inside one.
    /// </summary>
    [Fact]
    public async Task Two_workspaces_each_holding_a_version_one_stay_apart()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var inA = await UploadAsync(ownerA, _fixture.WorkspaceA, file: BrandSourceSampleFiles.Pdf("a-marker"));
        var inB = await UploadAsync(ownerB, _fixture.WorkspaceB, file: BrandSourceSampleFiles.Pdf("b-marker"));

        var fromA = await (await ownerA.GetAsync(ContentIn(_fixture.WorkspaceA, inA, 1), cancellation))
            .Content.ReadAsByteArrayAsync(cancellation);
        var fromB = await (await ownerB.GetAsync(ContentIn(_fixture.WorkspaceB, inB, 1), cancellation))
            .Content.ReadAsByteArrayAsync(cancellation);

        Assert.Equal(BrandSourceSampleFiles.Pdf("a-marker"), fromA);
        Assert.Equal(BrandSourceSampleFiles.Pdf("b-marker"), fromB);
        Assert.NotEqual(fromA, fromB);
    }

    /// <summary>The same ids under B's own slug, by a caller who is not a member there: a gate in front of the data.</summary>
    [Fact]
    public async Task A_non_member_asking_under_the_owning_workspaces_slug_is_refused()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var inB = await UploadAsync(ownerB, _fixture.WorkspaceB);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await ownerA.GetAsync(DocumentIn(_fixture.WorkspaceB, inB), cancellation)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await ownerA.GetAsync(ContentIn(_fixture.WorkspaceB, inB, 1), cancellation)).StatusCode);
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace) =>
        _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private static string SourcesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents";

    private static string DocumentIn(SeededWorkspace workspace, Guid documentId) =>
        $"{SourcesIn(workspace)}/{documentId}";

    private static string ContentIn(SeededWorkspace workspace, Guid documentId, int versionNumber) =>
        $"{DocumentIn(workspace, documentId)}/versions/{versionNumber}/content";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>Uploads one document and returns its id.</summary>
    private async Task<Guid> UploadAsync(
        GatewayClient client,
        SeededWorkspace workspace,
        byte[]? file = null,
        string fileName = "house-style.pdf",
        string? title = "House style",
        params string[] tags)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(file ?? BrandSourceSampleFiles.Pdf("house style"));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        form.Add(new StringContent(title ?? "House style"), "title");
        form.Add(new StringContent("StyleGuide"), "documentType");
        form.Add(new StringContent("Voice"), "purpose");

        foreach (var tag in tags)
        {
            form.Add(new StringContent(tag), "tags");
        }

        var response = await client.PostAsync(
            SourcesIn(workspace), form, Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Moves a document to a status no route can set yet.
    /// </summary>
    /// <remarks>
    /// Archive, restore and remove are later prompts, so there is no endpoint to drive. Written directly
    /// rather than skipped, because what a read does about each status is this feature's decision and belongs
    /// in this feature's tests.
    /// </remarks>
    private async Task SetStatusAsync(Guid documentId, BrandSourceDocumentStatus status, DateTimeOffset? archivedAt)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            _fixture.WorkspaceA.Id, _fixture.WorkspaceA.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var document = await db.BrandSourceDocuments.SingleAsync(
            candidate => candidate.Id == documentId, TestContext.Current.CancellationToken);

        document.Status = status;
        document.ArchivedAt = archivedAt;

        // A tombstone names when and who — CK_BrandSourceDocuments_Removed_Consistent refuses one that does
        // not, and refuses the pair on anything that is not removed.
        var removed = status == BrandSourceDocumentStatus.Removed;
        document.RemovedAt = removed ? DateTimeOffset.UtcNow : null;
        document.RemovedByMembershipId = removed ? Guid.NewGuid() : null;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A problem body with its trace id removed, so two refusals can be compared for being the same refusal
    /// rather than for having happened in the same request.
    /// </summary>
    private static string WithoutTrace(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(property => !property.Name.Contains("traceId", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Contains("correlation", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(property => property.Name, property => property.Value.ToString()));
}
