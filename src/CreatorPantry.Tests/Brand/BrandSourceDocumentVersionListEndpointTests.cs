using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <c>GET .../brand-source-documents/{id}/versions</c> through the real Gateway: the history a replacement
/// builds up, each version's own metadata and extraction, which row is current, keyset paging, what a row
/// must never carry, and workspace isolation. Rows are seeded directly so a test controls version count and
/// per-version extraction history, which no route can arrange yet.
/// </summary>
public sealed class BrandSourceDocumentVersionListEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string VersionsIn(SeededWorkspace workspace, Guid documentId, string query = "") =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents/{documentId}/versions{query}";

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static async Task<JsonElement> ListAsync(GatewayClient client, SeededWorkspace workspace, Guid documentId, string query = "")
    {
        var response = await client.GetAsync(VersionsIn(workspace, documentId, query), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private static List<int> Numbers(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("versionNumber").GetInt32())];

    /// <summary>One version to write: its file's facts and how its own text turned out.</summary>
    private sealed record VersionSeed(
        string FileName,
        string MediaType = "application/pdf",
        (BrandSourceExtractionStatus Status, BrandSourceExtractionOrigin Origin)[]? Extractions = null);

    /// <summary>
    /// Writes one document whose versions are numbered 1..n in the order given, the last being current — the
    /// shape a replacement leaves behind.
    /// </summary>
    private async Task<Guid> SeedAsync(
        SeededWorkspace workspace,
        BrandSourceDocumentStatus status = BrandSourceDocumentStatus.Active,
        string title = "House Style",
        params VersionSeed[] versions)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var member = Guid.NewGuid();

        var document = new BrandSourceDocument
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            Title = title,
            DocumentType = BrandSourceDocumentType.StyleGuide,
            Purpose = BrandSourcePurpose.WritingStyle,
            Status = status,
            CurrentVersionNumber = versions.Length,
            ArchivedAt = status == BrandSourceDocumentStatus.Archived ? Base : null,
            RemovedAt = status == BrandSourceDocumentStatus.Removed ? Base : null,
            RemovedByMembershipId = status == BrandSourceDocumentStatus.Removed ? member : null,
            CreatedAt = Base.AddDays(-1),
            UpdatedAt = Base,
            CreatedByMembershipId = member,
            UpdatedByMembershipId = member,
        };

        foreach (var (seed, index) in versions.Select((seed, index) => (seed, index)))
        {
            var number = index + 1;
            var version = new BrandSourceDocumentVersion
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                BrandSourceDocumentId = document.Id,
                VersionNumber = number,
                MediaType = seed.MediaType,

                // Distinct per version, so a row carrying another version's size is visible.
                SizeBytes = 1000 + number,
                ContentChecksum = "sha256:" + new string((char)('0' + number), 64),
                OriginalFileName = seed.FileName,
                ObjectKey = BrandSourceObjectKey.ForOriginal(workspace.Id, document.Id, Guid.NewGuid()),
                CreatedByMembershipId = member,

                // A version added later is stamped later, so "newest first" is not the same as "by id".
                CreatedAt = Base.AddMinutes(number),
            };
            document.Versions.Add(version);

            foreach (var (extraction, ordinal) in (seed.Extractions ?? []).Select((extraction, ordinal) => (extraction, ordinal)))
            {
                var succeeded = extraction.Status == BrandSourceExtractionStatus.Succeeded;
                db.BrandSourceExtractions.Add(new BrandSourceExtraction
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.Id,
                    BrandSourceDocumentVersionId = version.Id,
                    Ordinal = ordinal + 1,
                    Status = extraction.Status,
                    Origin = extraction.Origin,
                    ExtractedTextObjectKey = succeeded
                        ? BrandSourceObjectKey.ForExtractedText(workspace.Id, document.Id, version.Id, ordinal + 1)
                        : null,
                    ContentChecksum = succeeded ? "sha256:" + new string('f', 64) : null,
                    CreatedByMembershipId = extraction.Origin == BrandSourceExtractionOrigin.Corrected ? member : null,
                    CreatedAt = Base.AddMinutes(number).AddSeconds(ordinal + 1),
                });
            }
        }

        db.BrandSourceDocuments.Add(document);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return document.Id;
    }

    /// <summary>A document replaced twice: read, then replaced, then replaced again and read badly.</summary>
    private Task<Guid> SeedThreeVersionsAsync(SeededWorkspace workspace, BrandSourceDocumentStatus status = BrandSourceDocumentStatus.Active) =>
        SeedAsync(
            workspace,
            status,
            "House Style",
            new VersionSeed("first-draft.pdf", Extractions:
                [(BrandSourceExtractionStatus.Succeeded, BrandSourceExtractionOrigin.Extracted)]),
            new VersionSeed("scanned.pdf", Extractions:
                [(BrandSourceExtractionStatus.Unsupported, BrandSourceExtractionOrigin.Extracted)]),
            new VersionSeed("final.md", "text/markdown"));

    // ---- Shape ----

    [Fact]
    public async Task The_history_is_every_version_newest_first_with_its_own_file_and_its_own_text()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var documentId = await SeedThreeVersionsAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(VersionsIn(_fixture.WorkspaceA, documentId), TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var page = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal([3, 2, 1], Numbers(page));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);

        var items = page.GetProperty("items");

        // The current version: its own file, and no text read from it yet.
        Assert.Equal("final.md", items[0].GetProperty("originalFileName").GetString());
        Assert.Equal("text/markdown", items[0].GetProperty("mediaType").GetString());
        Assert.Equal(1003, items[0].GetProperty("sizeBytes").GetInt64());
        Assert.True(items[0].GetProperty("isCurrent").GetBoolean());
        Assert.Equal("NotExtracted", items[0].GetProperty("extraction").GetProperty("state").GetString());

        // A superseded version keeps the text it had. This is the only route that shows that.
        Assert.Equal("scanned.pdf", items[1].GetProperty("originalFileName").GetString());
        Assert.False(items[1].GetProperty("isCurrent").GetBoolean());
        Assert.Equal("Unsupported", items[1].GetProperty("extraction").GetProperty("state").GetString());

        Assert.Equal("first-draft.pdf", items[2].GetProperty("originalFileName").GetString());
        Assert.Equal("Succeeded", items[2].GetProperty("extraction").GetProperty("state").GetString());
        Assert.Equal("Extracted", items[2].GetProperty("extraction").GetProperty("origin").GetString());

        // The checksum is published deliberately — it is the download's entity tag — but nothing that
        // locates the bytes is.
        Assert.StartsWith("sha256:", items[0].GetProperty("contentChecksum").GetString());
        Assert.DoesNotContain("workspaces/", raw);
        Assert.DoesNotContain("objectKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("originals/", raw);
        Assert.DoesNotContain("extracted/", raw);
    }

    [Fact]
    public async Task Exactly_one_version_is_current_and_it_is_the_highest_numbered()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var documentId = await SeedThreeVersionsAsync(_fixture.WorkspaceA);

        var items = (await ListAsync(client, _fixture.WorkspaceA, documentId)).GetProperty("items");
        var current = items.EnumerateArray().Where(item => item.GetProperty("isCurrent").GetBoolean()).ToList();

        Assert.Single(current);
        Assert.Equal(3, current[0].GetProperty("versionNumber").GetInt32());
    }

    [Fact]
    public async Task A_document_never_replaced_has_a_history_of_one_current_version()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var documentId = await SeedAsync(_fixture.WorkspaceA, versions: new VersionSeed("only.pdf"));

        var page = await ListAsync(client, _fixture.WorkspaceA, documentId);

        Assert.Equal([1], Numbers(page));
        Assert.True(page.GetProperty("items")[0].GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task An_archived_documents_history_reads_normally()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var documentId = await SeedThreeVersionsAsync(_fixture.WorkspaceA, BrandSourceDocumentStatus.Archived);

        // Archiving is a shelf, not a deletion: every version is still there and still described.
        Assert.Equal([3, 2, 1], Numbers(await ListAsync(client, _fixture.WorkspaceA, documentId)));
    }

    [Fact]
    public async Task A_viewer_may_read_their_own_workspaces_history()
    {
        using var viewer = await _fixture.SignInAsync(
            _fixture.WorkspaceA.MemberEmail, cancellationToken: TestContext.Current.CancellationToken);
        var documentId = await SeedThreeVersionsAsync(_fixture.WorkspaceA);

        Assert.Equal([3, 2, 1], Numbers(await ListAsync(viewer, _fixture.WorkspaceA, documentId)));
    }

    // ---- Refusals ----

    [Fact]
    public async Task An_unknown_a_foreign_and_a_removed_document_are_one_answer()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        var inB = await SeedThreeVersionsAsync(_fixture.WorkspaceB);
        var removed = await SeedAsync(
            _fixture.WorkspaceA, BrandSourceDocumentStatus.Removed, "Withdrawn", new VersionSeed("gone.pdf"));

        var unknown = await ownerA.GetAsync(VersionsIn(_fixture.WorkspaceA, Guid.NewGuid()), cancellation);
        var foreign = await ownerA.GetAsync(VersionsIn(_fixture.WorkspaceA, inB), cancellation);
        var tombstone = await ownerA.GetAsync(VersionsIn(_fixture.WorkspaceA, removed), cancellation);

        var bodies = new List<JsonElement>();
        foreach (var response in new[] { unknown, foreign, tombstone })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            // Read once: the content stream is consumed, and the three are compared below.
            var body = await BodyOf(response);
            Assert.Equal(BrandErrorCodes.SourceNotFound, body.GetProperty("code").GetString());

            // A refusal says nothing about what it refused. The removed document was seeded with a title and
            // a filename of its own, and neither may appear in the answer.
            var raw = body.GetRawText();
            Assert.DoesNotContain("Withdrawn", raw);
            Assert.DoesNotContain("gone.pdf", raw);
            Assert.DoesNotContain("versionNumber", raw);
            Assert.DoesNotContain("items", raw);
            bodies.Add(body);
        }

        // Indistinguishable, so this route cannot be used to learn that a document exists out of reach. The
        // refusal's own wording is the `title`, which is where ProblemResults puts the error message.
        Assert.Single(bodies.Select(body => body.GetProperty("title").GetString()).Distinct());
    }

    [Fact]
    public async Task A_cursor_issued_for_another_document_is_refused_by_field()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var first = await SeedThreeVersionsAsync(_fixture.WorkspaceA);
        var second = await SeedThreeVersionsAsync(_fixture.WorkspaceA);

        var cursor = Uri.EscapeDataString(
            (await ListAsync(client, _fixture.WorkspaceA, first, "?limit=1")).GetProperty("nextCursor").GetString()!);

        // Still works where it was issued.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(VersionsIn(_fixture.WorkspaceA, first, $"?cursor={cursor}"), cancellation)).StatusCode);

        foreach (var query in new[] { $"?cursor={cursor}", "?cursor=not-a-cursor", "?cursor=%00" })
        {
            var target = query.Contains("not-a-cursor") || query.Contains("%00") ? first : second;
            var response = await client.GetAsync(VersionsIn(_fixture.WorkspaceA, target, query), cancellation);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await BodyOf(response);
            Assert.Equal(BrandErrorCodes.SourceInvalidRequest, body.GetProperty("code").GetString());
            Assert.True(body.GetProperty("errors").TryGetProperty("cursor", out _), query);
        }
    }

    // ---- Paging ----

    [Fact]
    public async Task Following_the_cursor_visits_every_version_once_newest_first()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var documentId = await SeedAsync(
            _fixture.WorkspaceA,
            BrandSourceDocumentStatus.Active,
            "Much replaced",
            [.. Enumerable.Range(1, 7).Select(number => new VersionSeed($"take-{number}.pdf"))]);

        var walked = new List<int>();
        var pages = 0;
        string? cursor = null;

        do
        {
            var page = await ListAsync(
                client, _fixture.WorkspaceA, documentId, "?limit=3" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}"));
            var items = page.GetProperty("items");

            Assert.InRange(items.GetArrayLength(), 1, 3);
            walked.AddRange(Numbers(page));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal([7, 6, 5, 4, 3, 2, 1], walked);
    }

    [Theory]
    [InlineData("?limit=0", 1)]
    [InlineData("?limit=-5", 1)]
    [InlineData("?limit=2", 2)]
    [InlineData("?limit=100000", 3)]
    public async Task The_page_size_is_clamped_rather_than_refused(string query, int count)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var documentId = await SeedThreeVersionsAsync(_fixture.WorkspaceA);

        var page = await ListAsync(client, _fixture.WorkspaceA, documentId, query);

        Assert.Equal(count, page.GetProperty("items").GetArrayLength());
        Assert.Equal(count < 3, page.GetProperty("nextCursor").ValueKind == JsonValueKind.String);
    }

    // ---- Isolation ----

    [Fact]
    public async Task Each_workspace_reads_only_its_own_versions_and_their_own_extractions()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        // The same title and the same filenames on both sides. Only A's first version was read, and B has a
        // fourth version A does not.
        var inA = await SeedAsync(
            _fixture.WorkspaceA,
            BrandSourceDocumentStatus.Active,
            "House Style",
            new VersionSeed("guide.pdf", Extractions: [(BrandSourceExtractionStatus.Succeeded, BrandSourceExtractionOrigin.Extracted)]),
            new VersionSeed("guide-2.pdf"));
        var inB = await SeedAsync(
            _fixture.WorkspaceB,
            BrandSourceDocumentStatus.Active,
            "House Style",
            new VersionSeed("guide.pdf"),
            new VersionSeed("guide-2.pdf"),
            new VersionSeed("guide-3.pdf"));

        var pageA = await ListAsync(ownerA, _fixture.WorkspaceA, inA);
        var pageB = await ListAsync(ownerB, _fixture.WorkspaceB, inB);

        Assert.Equal([2, 1], Numbers(pageA));
        Assert.Equal([3, 2, 1], Numbers(pageB));

        // No version id is shared, and A's extraction does not colour B's identically named version.
        var idsA = pageA.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        var idsB = pageB.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Empty(idsA.Intersect(idsB));
        Assert.Equal("Succeeded", pageA.GetProperty("items")[1].GetProperty("extraction").GetProperty("state").GetString());
        Assert.Equal("NotExtracted", pageB.GetProperty("items")[2].GetProperty("extraction").GetProperty("state").GetString());

        // A's cursor names a position in A's document. In B's it is refused, not reinterpreted.
        var cursorA = Uri.EscapeDataString(
            (await ListAsync(ownerA, _fixture.WorkspaceA, inA, "?limit=1")).GetProperty("nextCursor").GetString()!);
        var replayed = await ownerB.GetAsync(VersionsIn(_fixture.WorkspaceB, inB, $"?limit=1&cursor={cursorA}"), cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);

        // And B's owner asking A's workspace for A's document is told what a nonexistent workspace is told.
        var intoA = await ownerB.GetAsync(VersionsIn(_fixture.WorkspaceA, inA), cancellation);
        var intoNowhere = await ownerB.GetAsync(
            $"/api/v1/workspaces/no-such-kitchen/brand-source-documents/{inA}/versions", cancellation);
        Assert.Equal(HttpStatusCode.NotFound, intoA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, intoNowhere.StatusCode);
        Assert.Equal(
            (await BodyOf(intoNowhere)).GetProperty("code").GetString(),
            (await BodyOf(intoA)).GetProperty("code").GetString());
    }
}
