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
/// The four lifecycle commands, the usage read and the removed-document bin, through the real Gateway over an
/// in-memory object store: 11A.9.
/// </summary>
/// <remarks>
/// <para>
/// Most of what is asserted here is what <em>did not</em> happen. Archiving, removing and restoring are the
/// three commands in this module that look destructive and are not: every test that moves a document also
/// checks that its version rows and its stored bytes are exactly where they were, and that a style-guide
/// version citing it still resolves afterwards.
/// </para>
/// <para>
/// The role split is the other half. An Editor shelves and soft-deletes; only an Owner sees the bin or
/// restores from it, and the bin is the only route in the module that answers for a removed document — so an
/// Editor who removes something genuinely cannot see it again.
/// </para>
/// </remarks>
public sealed class BrandSourceDocumentLifecycleEndpointTests : IAsyncLifetime
{
    private readonly InMemoryPrivateObjectStore _store = new();

    private readonly FakeMalwareScanGateway _scanner = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(_scanner);
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---- Archive ----

    [Fact]
    public async Task Archiving_shelves_the_document_without_moving_a_byte()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var bytes = BrandSourceSampleFiles.Pdf("house style");
        var seeded = await UploadAsync(client, _fixture.WorkspaceA, bytes);
        var objectKey = Assert.Single(_store.Keys);

        var response = await CommandAsync(client, _fixture.WorkspaceA, seeded.Id, "archive", seeded.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("Archived", body.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("archivedAt").ValueKind);

        // Still readable and still downloadable: a shelf, not a deletion.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(DocumentIn(_fixture.WorkspaceA, seeded.Id), cancellation)).StatusCode);
        var download = await client.GetAsync(ContentIn(_fixture.WorkspaceA, seeded.Id, 1), cancellation);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync(cancellation));
        Assert.Equal(objectKey, Assert.Single(_store.Keys));

        // Out of the default library listing, and into the archived one.
        Assert.Empty(await ListIdsAsync(client, _fixture.WorkspaceA, status: null));
        Assert.Equal([seeded.Id], await ListIdsAsync(client, _fixture.WorkspaceA, "Archived"));
    }

    [Fact]
    public async Task An_archived_document_refuses_a_replacement_until_it_comes_back()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);
        await CommandAsync(client, _fixture.WorkspaceA, seeded.Id, "archive", seeded.Token);

        var refused = await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, seeded.Token);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceArchivedConflict, Code(await BodyOf(refused)));

        await CommandAsync(client, _fixture.WorkspaceA, seeded.Id, "unarchive", await TokenOfAsync(client, _fixture.WorkspaceA, seeded.Id));

        var accepted = await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, await TokenOfAsync(client, _fixture.WorkspaceA, seeded.Id));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task Unarchiving_returns_the_document_to_active_and_keeps_the_date_it_was_shelved()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);

        var archived = await BodyOf(await CommandAsync(client, _fixture.WorkspaceA, seeded.Id, "archive", seeded.Token));
        var archivedAt = archived.GetProperty("archivedAt").GetDateTimeOffset();

        var body = await BodyOf(await CommandAsync(
            client, _fixture.WorkspaceA, seeded.Id, "unarchive", await TokenOfAsync(client, _fixture.WorkspaceA, seeded.Id)));

        Assert.Equal("Active", body.GetProperty("status").GetString());

        // Kept: it records when the document was last shelved, not whether it is shelved now.
        Assert.Equal(archivedAt, body.GetProperty("archivedAt").GetDateTimeOffset());
    }

    // ---- Repeat ----

    [Fact]
    public async Task Repeating_archive_unarchive_and_restore_changes_nothing_and_writes_no_audit_entry()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);

        await CommandAsync(client, _fixture.WorkspaceA, seeded.Id, "archive", seeded.Token);
        var token = await TokenOfAsync(client, _fixture.WorkspaceA, seeded.Id);
        var auditsBefore = await AuditCountAsync(BrandAuditActions.SourceDocumentArchived);

        var repeated = await CommandAsync(client, _fixture.WorkspaceA, seeded.Id, "archive", token);

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var body = await BodyOf(repeated);
        Assert.Equal("Archived", body.GetProperty("status").GetString());

        // No move, so no audit entry and no new token: a repeat must not put a lie in either record.
        Assert.Equal(auditsBefore, await AuditCountAsync(BrandAuditActions.SourceDocumentArchived));
        Assert.Equal(token, body.GetProperty("concurrencyToken").GetString());

        // Restoring a document that is not removed is the same kind of no-op.
        var restored = await CommandAsync(client, _fixture.WorkspaceA, seeded.Id, "restore", token);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(0, await AuditCountAsync(BrandAuditActions.SourceDocumentRestored));

        // And so is unarchiving twice.
        await CommandAsync(client, _fixture.WorkspaceA, seeded.Id, "unarchive", token);
        var active = await TokenOfAsync(client, _fixture.WorkspaceA, seeded.Id);
        var again = await CommandAsync(client, _fixture.WorkspaceA, seeded.Id, "unarchive", active);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await AuditCountAsync(BrandAuditActions.SourceDocumentUnarchived));
    }

    [Fact]
    public async Task Repeating_a_removal_answers_not_found_because_the_remover_can_no_longer_see_it()
    {
        using var editor = await EditorOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(editor, _fixture.WorkspaceA);

        Assert.Equal(HttpStatusCode.NoContent, (await CommandAsync(editor, _fixture.WorkspaceA, seeded.Id, "remove", seeded.Token)).StatusCode);

        var again = await CommandAsync(editor, _fixture.WorkspaceA, seeded.Id, "remove", seeded.Token);

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceNotFound, Code(await BodyOf(again)));
        Assert.Equal(1, await AuditCountAsync(BrandAuditActions.SourceDocumentRemoved));
    }

    // ---- Remove ----

    [Fact]
    public async Task Removing_answers_no_content_hides_the_document_everywhere_and_deletes_nothing()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var editor = await EditorOf(_fixture.WorkspaceA);
        var bytes = BrandSourceSampleFiles.Pdf("house style");
        var seeded = await UploadAsync(editor, _fixture.WorkspaceA, bytes);
        var objectKey = Assert.Single(_store.Keys);

        var response = await CommandAsync(editor, _fixture.WorkspaceA, seeded.Id, "remove", seeded.Token);

        // No body on purpose: a tombstone is published in one place only, and it is not this one.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);

        // Invisible to every route the remover can reach.
        foreach (var path in new[]
        {
            DocumentIn(_fixture.WorkspaceA, seeded.Id),
            ContentIn(_fixture.WorkspaceA, seeded.Id, 1),
            $"{DocumentIn(_fixture.WorkspaceA, seeded.Id)}/usage",
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await editor.GetAsync(path, cancellation)).StatusCode);
        }

        Assert.Empty(await ListIdsAsync(editor, _fixture.WorkspaceA, status: null));
        Assert.Empty(await ListIdsAsync(editor, _fixture.WorkspaceA, "Archived"));
        Assert.Equal(HttpStatusCode.NotFound, (await ReplaceAsync(editor, _fixture.WorkspaceA, seeded.Id, seeded.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CommandAsync(editor, _fixture.WorkspaceA, seeded.Id, "archive", seeded.Token)).StatusCode);

        // And nothing was deleted: the row, its version and its bytes are all exactly where they were.
        var document = await InScopeAsync(_fixture.WorkspaceA, services => services
            .GetRequiredService<CreatorPantryDbContext>().BrandSourceDocuments.SingleAsync(cancellation));
        Assert.Equal(BrandSourceDocumentStatus.Removed, document.Status);
        Assert.NotNull(document.RemovedAt);
        Assert.NotNull(document.RemovedByMembershipId);

        Assert.Equal(1, await VersionCountAsync(_fixture.WorkspaceA));
        Assert.Equal(objectKey, Assert.Single(_store.Keys));
        Assert.Equal(bytes, await BytesOfAsync(_fixture.WorkspaceA, objectKey));
    }

    [Fact]
    public async Task A_document_can_be_removed_straight_from_active_or_from_the_shelf()
    {
        using var editor = await EditorOf(_fixture.WorkspaceA);
        var fromActive = await UploadAsync(editor, _fixture.WorkspaceA, BrandSourceSampleFiles.Pdf("one"));
        var fromShelf = await UploadAsync(editor, _fixture.WorkspaceA, BrandSourceSampleFiles.Pdf("two"));

        await CommandAsync(editor, _fixture.WorkspaceA, fromShelf.Id, "archive", fromShelf.Token);

        Assert.Equal(HttpStatusCode.NoContent, (await CommandAsync(
            editor, _fixture.WorkspaceA, fromActive.Id, "remove", fromActive.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await CommandAsync(
            editor, _fixture.WorkspaceA, fromShelf.Id, "remove",
            await TokenFromBinAsync(await OwnerOf(_fixture.WorkspaceA), _fixture.WorkspaceA, fromShelf.Id) ?? fromShelf.Token)).StatusCode);
    }

    // ---- Linked and held ----

    [Fact]
    public async Task A_guide_version_still_cites_the_document_after_it_is_removed()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var editor = await EditorOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(editor, _fixture.WorkspaceA);
        var guideVersionId = await SeedGuideCitingAsync(seeded.VersionId, approved: true);

        Assert.Equal(HttpStatusCode.NoContent, (await CommandAsync(
            editor, _fixture.WorkspaceA, seeded.Id, "remove", seeded.Token)).StatusCode);

        var (link, approvals) = await InScopeAsync(_fixture.WorkspaceA, async services =>
        {
            var db = services.GetRequiredService<CreatorPantryDbContext>();
            return (
                await db.BrandStyleGuideSourceLinks.SingleAsync(cancellation),
                await db.BrandStyleGuideApprovals.CountAsync(cancellation));
        });

        // The citation pins a version, and that version was never deleted — which is the whole reason a
        // removal is allowed while an approved guide cites it.
        Assert.Equal(seeded.VersionId, link.BrandSourceDocumentVersionId);
        Assert.Equal(guideVersionId, link.BrandStyleGuideVersionId);
        Assert.Equal(1, approvals);
        Assert.Single(_store.Keys);
    }

    [Fact]
    public async Task Usage_reports_the_holds_and_an_approved_guide_is_what_makes_a_document_held()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var bytes = BrandSourceSampleFiles.Pdf("house style");
        var seeded = await UploadAsync(client, _fixture.WorkspaceA, bytes);

        // Nothing points at it yet.
        var empty = await BodyOf(await client.GetAsync(UsageIn(_fixture.WorkspaceA, seeded.Id), cancellation));
        Assert.Equal(1, empty.GetProperty("versionCount").GetInt32());
        Assert.Equal(bytes.Length, empty.GetProperty("storedBytes").GetInt64());
        Assert.False(empty.GetProperty("isHeld").GetBoolean());
        Assert.Empty(empty.GetProperty("holds").EnumerateArray());

        // A draft guide is a link, not a hold: it may still be superseded.
        await SeedGuideCitingAsync(seeded.VersionId, approved: false, displayName: "Draft voice");
        var linked = await BodyOf(await client.GetAsync(UsageIn(_fixture.WorkspaceA, seeded.Id), cancellation));
        Assert.False(linked.GetProperty("isHeld").GetBoolean());
        var draft = Assert.Single(linked.GetProperty("holds").EnumerateArray());
        Assert.Equal("StyleGuideVersion", draft.GetProperty("kind").GetString());
        Assert.Equal("Draft voice", draft.GetProperty("holderName").GetString());
        Assert.Equal(1, draft.GetProperty("sourceVersionNumber").GetInt32());
        Assert.False(draft.GetProperty("isApproved").GetBoolean());

        // An approved one is the fact a retention job has to honour.
        await SeedGuideCitingAsync(seeded.VersionId, approved: true, displayName: "Everyday voice");
        var held = await BodyOf(await client.GetAsync(UsageIn(_fixture.WorkspaceA, seeded.Id), cancellation));
        Assert.True(held.GetProperty("isHeld").GetBoolean());
        Assert.Equal(2, held.GetProperty("holds").GetArrayLength());

        // A storage location is not a thing a confirmation screen needs.
        var raw = await (await client.GetAsync(UsageIn(_fixture.WorkspaceA, seeded.Id), cancellation))
            .Content.ReadAsStringAsync(cancellation);
        Assert.DoesNotContain("workspaces/", raw);
        Assert.DoesNotContain("objectKey", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Usage_counts_every_version_and_reads_for_an_archived_document()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var first = BrandSourceSampleFiles.Pdf("one");
        var second = BrandSourceSampleFiles.Pdf("a longer second file");
        var seeded = await UploadAsync(client, _fixture.WorkspaceA, first);

        Assert.Equal(HttpStatusCode.Created, (await ReplaceAsync(client, _fixture.WorkspaceA, seeded.Id, seeded.Token, second)).StatusCode);
        await CommandAsync(client, _fixture.WorkspaceA, seeded.Id, "archive", await TokenOfAsync(client, _fixture.WorkspaceA, seeded.Id));

        var body = await BodyOf(await client.GetAsync(UsageIn(_fixture.WorkspaceA, seeded.Id), cancellation));

        Assert.Equal(2, body.GetProperty("versionCount").GetInt32());
        Assert.Equal(first.Length + second.Length, body.GetProperty("storedBytes").GetInt64());
    }

    // ---- The bin and the restore ----

    [Fact]
    public async Task An_owner_sees_the_bin_and_restores_from_it_while_an_editor_can_do_neither()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var editor = await EditorOf(_fixture.WorkspaceA);
        using var owner = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(editor, _fixture.WorkspaceA);

        await CommandAsync(editor, _fixture.WorkspaceA, seeded.Id, "remove", seeded.Token);

        // The editor who removed it cannot see it again, and cannot bring it back.
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync(BinIn(_fixture.WorkspaceA), cancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await CommandAsync(editor, _fixture.WorkspaceA, seeded.Id, "restore", seeded.Token)).StatusCode);

        // The owner can. This is the one route that publishes a removed document's token.
        var bin = await BodyOf(await owner.GetAsync(BinIn(_fixture.WorkspaceA), cancellation));
        var row = Assert.Single(bin.GetProperty("items").EnumerateArray());
        Assert.Equal(seeded.Id, row.GetProperty("id").GetGuid());
        Assert.Equal("House style", row.GetProperty("title").GetString());
        Assert.Equal(1, row.GetProperty("currentVersionNumber").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("removedAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("removedByMembershipId").ValueKind);

        var restored = await CommandAsync(
            owner, _fixture.WorkspaceA, seeded.Id, "restore", row.GetProperty("concurrencyToken").GetString());

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        // Back to the shelf, not into everyone's picker.
        Assert.Equal("Archived", (await BodyOf(restored)).GetProperty("status").GetString());
        Assert.Empty(await ListIdsAsync(editor, _fixture.WorkspaceA, status: null));
        Assert.Equal([seeded.Id], await ListIdsAsync(editor, _fixture.WorkspaceA, "Archived"));

        // The bin is empty again, and the stamps are gone because the schema refuses them on a live row.
        Assert.Empty((await BodyOf(await owner.GetAsync(BinIn(_fixture.WorkspaceA), cancellation))).GetProperty("items").EnumerateArray());
        var document = await InScopeAsync(_fixture.WorkspaceA, services => services
            .GetRequiredService<CreatorPantryDbContext>().BrandSourceDocuments.SingleAsync(cancellation));
        Assert.Null(document.RemovedAt);
        Assert.Null(document.RemovedByMembershipId);

        // Which makes these two entries the only lasting record that it was ever removed.
        Assert.Equal(1, await AuditCountAsync(BrandAuditActions.SourceDocumentRemoved));
        Assert.Equal(1, await AuditCountAsync(BrandAuditActions.SourceDocumentRestored));
    }

    [Fact]
    public async Task The_bin_pages_newest_removal_first()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await OwnerOf(_fixture.WorkspaceA);
        var ids = new List<Guid>();

        for (var index = 0; index < 3; index++)
        {
            var seeded = await UploadAsync(owner, _fixture.WorkspaceA, BrandSourceSampleFiles.Pdf($"file {index}"));
            await CommandAsync(owner, _fixture.WorkspaceA, seeded.Id, "remove", seeded.Token);
            ids.Add(seeded.Id);
        }

        var first = await BodyOf(await owner.GetAsync($"{BinIn(_fixture.WorkspaceA)}?limit=2", cancellation));
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());

        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var second = await BodyOf(await owner.GetAsync($"{BinIn(_fixture.WorkspaceA)}?cursor={Uri.EscapeDataString(cursor!)}", cancellation));
        Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);

        var paged = first.GetProperty("items").EnumerateArray()
            .Concat(second.GetProperty("items").EnumerateArray())
            .Select(item => item.GetProperty("id").GetGuid())
            .ToList();

        // Newest removal first, which is the reverse of the order they were removed in.
        Assert.Equal(Enumerable.Reverse(ids), paged);
    }

    // ---- Concurrency ----

    [Theory]
    [InlineData("archive")]
    [InlineData("unarchive")]
    [InlineData("remove")]
    [InlineData("restore")]
    public async Task A_token_that_is_not_this_documents_is_a_conflict_on_every_command(string command)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);

        var response = await CommandAsync(client, _fixture.WorkspaceA, seeded.Id, command, SomeOtherToken());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceConflict, Code(await BodyOf(response)));

        // The document did not move, and nothing was written about a move that did not happen.
        var document = await InScopeAsync(_fixture.WorkspaceA, services => services
            .GetRequiredService<CreatorPantryDbContext>().BrandSourceDocuments.SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(BrandSourceDocumentStatus.Active, document.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task A_missing_or_malformed_token_is_a_request_error_by_field(string? token)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var seeded = await UploadAsync(client, _fixture.WorkspaceA);

        var response = await CommandAsync(client, _fixture.WorkspaceA, seeded.Id, "archive", token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.SourceInvalidRequest, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    // ---- Isolation ----

    [Fact]
    public async Task One_workspace_cannot_shelve_remove_or_restore_the_others_document()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        var inA = await UploadAsync(ownerA, _fixture.WorkspaceA, BrandSourceSampleFiles.Pdf("a's file"));
        var inB = await UploadAsync(ownerB, _fixture.WorkspaceB, BrandSourceSampleFiles.Pdf("b's file"));

        // B is an owner of B, so these reach the routes and are refused by A's document not existing there.
        foreach (var command in new[] { "archive", "unarchive", "remove", "restore" })
        {
            var response = await CommandAsync(ownerB, _fixture.WorkspaceB, inA.Id, command, inA.Token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(BrandErrorCodes.SourceNotFound, Code(await BodyOf(response)));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.GetAsync(UsageIn(_fixture.WorkspaceB, inA.Id), cancellation)).StatusCode);

        // Each workspace removes its own, and neither bin shows the other's.
        await CommandAsync(ownerA, _fixture.WorkspaceA, inA.Id, "remove", inA.Token);
        await CommandAsync(ownerB, _fixture.WorkspaceB, inB.Id, "remove", inB.Token);

        var binA = await BodyOf(await ownerA.GetAsync(BinIn(_fixture.WorkspaceA), cancellation));
        var binB = await BodyOf(await ownerB.GetAsync(BinIn(_fixture.WorkspaceB), cancellation));

        Assert.Equal(inA.Id, Assert.Single(binA.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(inB.Id, Assert.Single(binB.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        // The four foreign commands above were tried while A's document was still live, so none of them
        // exercised the read-through. Restore is the one command that can see a tombstone, and now there is
        // a real one to aim at: it must still be invisible from B.
        var foreignRestore = await CommandAsync(ownerB, _fixture.WorkspaceB, inA.Id, "restore", inA.Token);
        Assert.Equal(HttpStatusCode.NotFound, foreignRestore.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceNotFound, Code(await BodyOf(foreignRestore)));

        // And it did not bring A's document back: A's bin still holds it.
        var binAfter = await BodyOf(await ownerA.GetAsync(BinIn(_fixture.WorkspaceA), cancellation));
        Assert.Equal(inA.Id, Assert.Single(binAfter.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        // Both documents' rows and bytes are still their own workspace's.
        Assert.Equal(1, await VersionCountAsync(_fixture.WorkspaceA));
        Assert.Equal(1, await VersionCountAsync(_fixture.WorkspaceB));
        Assert.Equal(2, _store.Keys.Count);
    }


    [Fact]
    public async Task A_bin_cursor_from_one_workspace_is_refused_in_the_other()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        // Two removals in A, so its bin issues a cursor; one in B, so B's bin is not simply empty.
        foreach (var body in new[] { "one", "two" })
        {
            var seeded = await UploadAsync(ownerA, _fixture.WorkspaceA, BrandSourceSampleFiles.Pdf(body));
            await CommandAsync(ownerA, _fixture.WorkspaceA, seeded.Id, "remove", seeded.Token);
        }

        var inB = await UploadAsync(ownerB, _fixture.WorkspaceB, BrandSourceSampleFiles.Pdf("b's file"));
        await CommandAsync(ownerB, _fixture.WorkspaceB, inB.Id, "remove", inB.Token);

        var page = await BodyOf(await ownerA.GetAsync($"{BinIn(_fixture.WorkspaceA)}?limit=1", cancellation));
        var cursor = page.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var replayed = await ownerB.GetAsync(
            $"{BinIn(_fixture.WorkspaceB)}?cursor={Uri.EscapeDataString(cursor!)}", cancellation);

        // A cursor carries a fingerprint of the scope it was issued for, and the workspace is part of it — so
        // one workspace's position cannot be used to resume another's bin.
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Equal(BrandErrorCodes.SourceInvalidRequest, Code(await BodyOf(replayed)));
    }

    [Fact]
    public async Task A_viewer_may_read_the_usage_but_cannot_move_a_document_or_see_the_bin()
    {
        var cancellation = TestContext.Current.CancellationToken;

        // Workspace B's seeded member is a Viewer, which is the role below every bar these commands set.
        using var owner = await OwnerOf(_fixture.WorkspaceB);
        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: cancellation);
        var seeded = await UploadAsync(owner, _fixture.WorkspaceB);

        foreach (var command in new[] { "archive", "unarchive", "remove", "restore" })
        {
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await CommandAsync(viewer, _fixture.WorkspaceB, seeded.Id, command, seeded.Token)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(BinIn(_fixture.WorkspaceB), cancellation)).StatusCode);

        // The confirmation read is a Viewer's, though: it says what points at a document and nothing private.
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(UsageIn(_fixture.WorkspaceB, seeded.Id), cancellation)).StatusCode);

        // Nothing moved.
        var document = await InScopeAsync(_fixture.WorkspaceB, services => services
            .GetRequiredService<CreatorPantryDbContext>().BrandSourceDocuments.SingleAsync(cancellation));
        Assert.Equal(BrandSourceDocumentStatus.Active, document.Status);
    }

    // ---- Helpers ----

    private static string SourcesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents";

    private static string DocumentIn(SeededWorkspace workspace, Guid documentId) => $"{SourcesIn(workspace)}/{documentId}";

    private static string ContentIn(SeededWorkspace workspace, Guid documentId, int versionNumber) =>
        $"{DocumentIn(workspace, documentId)}/versions/{versionNumber}/content";

    private static string UsageIn(SeededWorkspace workspace, Guid documentId) => $"{DocumentIn(workspace, documentId)}/usage";

    private static string BinIn(SeededWorkspace workspace) => $"{SourcesIn(workspace)}/removed";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static string Key() => Guid.NewGuid().ToString("N");

    /// <summary>A token this API would have issued, for a row that is not this one.</summary>
    private static string SomeOtherToken() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray().AsSpan(0, BrandConcurrencyToken.ByteLength));

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Workspace A's seeded member is an Editor, which is the bar these commands sit at.</summary>
    private async Task<GatewayClient> EditorOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.MemberEmail, cancellationToken: TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> CommandAsync(
        GatewayClient client, SeededWorkspace workspace, Guid documentId, string command, string? token) =>
        client.PostAsJsonAsync(
            $"{DocumentIn(workspace, documentId)}/{command}",
            new { expectedConcurrencyToken = token },
            TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> ReplaceAsync(
        GatewayClient client, SeededWorkspace workspace, Guid documentId, string? token, byte[]? file = null)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(file ?? BrandSourceSampleFiles.Pdf("replacement"));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "house-style.pdf");

        if (token is not null)
        {
            form.Add(new StringContent(token), "expectedConcurrencyToken");
        }

        return client.PostAsync(
            $"{DocumentIn(workspace, documentId)}/versions", form, Key(), TestContext.Current.CancellationToken);
    }

    private async Task<SeededDocument> UploadAsync(GatewayClient client, SeededWorkspace workspace, byte[]? file = null)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(file ?? BrandSourceSampleFiles.Pdf("house style"));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", "house-style.pdf");
        form.Add(new StringContent("House style"), "title");
        form.Add(new StringContent("StyleGuide"), "documentType");
        form.Add(new StringContent("Voice"), "purpose");

        var response = await client.PostAsync(SourcesIn(workspace), form, Key(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await BodyOf(response);

        return new SeededDocument(
            body.GetProperty("id").GetGuid(),
            body.GetProperty("currentVersion").GetProperty("id").GetGuid(),
            body.GetProperty("concurrencyToken").GetString()!);
    }

    private async Task<string> TokenOfAsync(GatewayClient client, SeededWorkspace workspace, Guid documentId) =>
        (await BodyOf(await client.GetAsync(DocumentIn(workspace, documentId), TestContext.Current.CancellationToken)))
            .GetProperty("concurrencyToken").GetString()!;

    /// <summary>The token of one removed document, from the only route that publishes one.</summary>
    private async Task<string?> TokenFromBinAsync(GatewayClient owner, SeededWorkspace workspace, Guid documentId)
    {
        using var client = owner;
        var bin = await BodyOf(await client.GetAsync(BinIn(workspace), TestContext.Current.CancellationToken));

        return bin.GetProperty("items").EnumerateArray()
            .Where(row => row.GetProperty("id").GetGuid() == documentId)
            .Select(row => row.GetProperty("concurrencyToken").GetString())
            .FirstOrDefault();
    }

    private async Task<IReadOnlyList<Guid>> ListIdsAsync(GatewayClient client, SeededWorkspace workspace, string? status)
    {
        var path = status is null ? SourcesIn(workspace) : $"{SourcesIn(workspace)}?status={status}";
        var body = await BodyOf(await client.GetAsync(path, TestContext.Current.CancellationToken));

        return [.. body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];
    }

    private async Task<int> AuditCountAsync(string action)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AuditLogs
            .IgnoreQueryFilters()
            .CountAsync(log => log.Action == action, TestContext.Current.CancellationToken);
    }

    private Task<int> VersionCountAsync(SeededWorkspace workspace) => InScopeAsync(workspace, services => services
        .GetRequiredService<CreatorPantryDbContext>().BrandSourceDocumentVersions.CountAsync(TestContext.Current.CancellationToken));

    private Task<byte[]> BytesOfAsync(SeededWorkspace workspace, string objectKey) => InScopeAsync(workspace, async services =>
    {
        await using var content = await services
            .GetRequiredService<Domain.Modules.Brand.Gateways.IBrandSourceObjectGateway>()
            .OpenReadAsync(objectKey, TestContext.Current.CancellationToken);
        using var buffer = new MemoryStream();
        await content!.Content.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        return buffer.ToArray();
    });

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider);
    }

    /// <summary>A guide version citing one source version, approved or not. Returns the guide version's id.</summary>
    private Task<Guid> SeedGuideCitingAsync(Guid sourceVersionId, bool approved, string displayName = "Everyday voice") =>
        InScopeAsync(_fixture.WorkspaceA, async services =>
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
                DisplayName = displayName,
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

            if (approved)
            {
                db.BrandStyleGuideApprovals.Add(new BrandStyleGuideApproval
                {
                    WorkspaceId = _fixture.WorkspaceA.Id,
                    BrandStyleGuideVersionId = guideVersionId,
                    ApprovedByMembershipId = membershipId,
                    ApprovedAt = now,
                });
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return guideVersionId;
        });

    private sealed record SeededDocument(Guid Id, Guid VersionId, string Token);
}
