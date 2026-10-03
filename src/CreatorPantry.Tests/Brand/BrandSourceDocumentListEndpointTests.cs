using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <c>GET .../brand-source-documents</c> through the real Gateway: each filter and their combination, keyset
/// paging, the item shape and what it must never carry, and workspace isolation. Rows are seeded directly so a
/// test controls status, edit time and extraction history, which no route can set yet.
/// </summary>
public sealed class BrandSourceDocumentListEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string SourcesIn(SeededWorkspace workspace, string query = "") =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents{query}";

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static async Task<JsonElement> ListAsync(GatewayClient client, SeededWorkspace workspace, string query = "")
    {
        var response = await client.GetAsync(SourcesIn(workspace, query), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private static List<string> Titles(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("title").GetString()!)];

    private sealed record Seed(
        string Title,
        int MinutesAgo = 0,
        BrandSourceDocumentType Type = BrandSourceDocumentType.StyleGuide,
        BrandSourceDocumentStatus Status = BrandSourceDocumentStatus.Active,
        string? Channel = null,
        string[]? Tags = null,
        string FileName = "document.pdf",
        int Versions = 1,
        (BrandSourceExtractionStatus Status, BrandSourceExtractionOrigin Origin)[]? Extractions = null,
        BrandSourcePurpose Purpose = BrandSourcePurpose.Voice,

        /// <summary>Attaches the extractions to version 1 instead of the current one. Needs two versions.</summary>
        bool ExtractSupersededVersion = false);

    /// <summary>Writes documents, their versions, tags and extraction history from the workspace's own scope.</summary>
    private async Task SeedAsync(SeededWorkspace workspace, params Seed[] seeds)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var member = Guid.NewGuid();
        var tags = await db.BrandSourceTags.ToDictionaryAsync(tag => tag.NormalizedName, TestContext.Current.CancellationToken);

        foreach (var seed in seeds)
        {
            var updatedAt = Base.AddMinutes(-seed.MinutesAgo);
            var document = new BrandSourceDocument
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                Title = seed.Title,
                DocumentType = seed.Type,
                Purpose = seed.Purpose,
                ChannelKey = seed.Channel,
                Status = seed.Status,
                CurrentVersionNumber = seed.Versions,
                ArchivedAt = seed.Status == BrandSourceDocumentStatus.Archived ? updatedAt : null,
                RemovedAt = seed.Status == BrandSourceDocumentStatus.Removed ? updatedAt : null,
                RemovedByMembershipId = seed.Status == BrandSourceDocumentStatus.Removed ? member : null,
                CreatedAt = updatedAt.AddDays(-1),
                UpdatedAt = updatedAt,
                CreatedByMembershipId = member,
                UpdatedByMembershipId = member,
            };

            BrandSourceDocumentVersion current = null!;
            for (var number = 1; number <= seed.Versions; number++)
            {
                current = new BrandSourceDocumentVersion
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.Id,
                    BrandSourceDocumentId = document.Id,
                    VersionNumber = number,
                    MediaType = "application/pdf",
                    SizeBytes = 1000 + number,
                    ContentChecksum = "sha256:" + new string('0', 64),

                    // Only the last version carries the seed's filename, so a search proves it reads the current one.
                    OriginalFileName = number == seed.Versions ? seed.FileName : $"superseded-{number}.pdf",
                    ObjectKey = BrandSourceObjectKey.ForOriginal(workspace.Id, document.Id, Guid.NewGuid()),
                    CreatedByMembershipId = member,
                    CreatedAt = updatedAt,
                };
                document.Versions.Add(current);
            }

            // Where the extraction history hangs. Version 1 when the seed says so, which is how a document
            // whose file was replaced after its text was read is written.
            var extracted = seed.ExtractSupersededVersion ? document.Versions.First(version => version.VersionNumber == 1) : current;

            foreach (var name in seed.Tags ?? [])
            {
                var normalized = NameNormalization.NormalizeName(name);
                if (!tags.TryGetValue(normalized, out var tag))
                {
                    tag = new BrandSourceTag { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, Name = name, NormalizedName = normalized, CreatedAt = Base };
                    tags[normalized] = tag;
                    db.BrandSourceTags.Add(tag);
                }

                document.Tags.Add(new BrandSourceDocumentTag { WorkspaceId = workspace.Id, BrandSourceDocumentId = document.Id, BrandSourceTagId = tag.Id });
            }

            db.BrandSourceDocuments.Add(document);

            foreach (var (extraction, index) in (seed.Extractions ?? []).Select((extraction, index) => (extraction, index)))
            {
                var succeeded = extraction.Status == BrandSourceExtractionStatus.Succeeded;
                db.BrandSourceExtractions.Add(new BrandSourceExtraction
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.Id,
                    BrandSourceDocumentVersionId = extracted.Id,
                    Ordinal = index + 1,
                    Status = extraction.Status,
                    Origin = extraction.Origin,
                    ExtractedTextObjectKey = succeeded
                        ? BrandSourceObjectKey.ForExtractedText(workspace.Id, document.Id, extracted.Id, index + 1)
                        : null,
                    ContentChecksum = succeeded ? "sha256:" + new string('1', 64) : null,
                    CreatedByMembershipId = extraction.Origin == BrandSourceExtractionOrigin.Corrected ? member : null,
                    CreatedAt = updatedAt.AddMinutes(index + 1),
                });
            }
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A library that exercises every filter at once.</summary>
    private Task SeedLibraryAsync(SeededWorkspace workspace) => SeedAsync(
        workspace,
        new Seed("House Style", 1, Tags: ["Launch", "Evergreen"], FileName: "Brand-Guide.pdf", Versions: 2,
            Purpose: BrandSourcePurpose.WritingStyle),
        new Seed("Autumn newsletter", 2, BrandSourceDocumentType.Newsletter, Tags: ["Launch"], FileName: "autumn.md"),
        new Seed("Reel captions", 3, BrandSourceDocumentType.SocialSample, Channel: "instagram", Tags: ["Holiday"]),
        new Seed("Pin descriptions", 4, BrandSourceDocumentType.SocialSample, Channel: "pinterest",
            Purpose: BrandSourcePurpose.VisualDirection),
        new Seed("Old house style", 5, Status: BrandSourceDocumentStatus.Archived, Tags: ["Launch"],
            Purpose: BrandSourcePurpose.WritingStyle),
        new Seed("Withdrawn sample", 6, Status: BrandSourceDocumentStatus.Removed));

    /// <summary>A library whose documents differ only in how their current version's text turned out.</summary>
    private Task SeedExtractionsAsync(SeededWorkspace workspace) => SeedAsync(
        workspace,

        // A failure then a success: the latest attempt is what either reads or filters.
        new Seed("Retried", 1, Extractions:
        [
            (BrandSourceExtractionStatus.Failed, BrandSourceExtractionOrigin.Extracted),
            (BrandSourceExtractionStatus.Succeeded, BrandSourceExtractionOrigin.Extracted),
        ]),
        new Seed("Scanned", 2, Extractions: [(BrandSourceExtractionStatus.Unsupported, BrandSourceExtractionOrigin.Extracted)]),
        new Seed("Broken", 3, Extractions: [(BrandSourceExtractionStatus.Failed, BrandSourceExtractionOrigin.Extracted)]),

        // Read once, then its file was replaced. The text on record belongs to version 1, so the current
        // version has not been extracted — the state is about the file that is there now.
        new Seed("Replaced", 4, Versions: 2, ExtractSupersededVersion: true,
            Extractions: [(BrandSourceExtractionStatus.Succeeded, BrandSourceExtractionOrigin.Extracted)]),
        new Seed("Untouched", 5));

    // ---- Shape ----

    [Fact]
    public async Task The_default_list_is_the_active_library_newest_edit_first_with_current_version_metadata()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await SeedLibraryAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(SourcesIn(_fixture.WorkspaceA), TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var page = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(new[] { "House Style", "Autumn newsletter", "Reel captions", "Pin descriptions" }, Titles(page));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);

        var first = page.GetProperty("items")[0];
        Assert.Equal("StyleGuide", first.GetProperty("documentType").GetString());
        Assert.Equal("Active", first.GetProperty("status").GetString());
        Assert.Equal(new[] { "Evergreen", "Launch" }, first.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));

        // The second of two versions, not the first.
        var version = first.GetProperty("currentVersion");
        Assert.Equal(2, version.GetProperty("versionNumber").GetInt32());
        Assert.Equal("Brand-Guide.pdf", version.GetProperty("originalFileName").GetString());
        Assert.Equal("application/pdf", version.GetProperty("mediaType").GetString());
        Assert.Equal(1002, version.GetProperty("sizeBytes").GetInt64());

        Assert.Equal("NotExtracted", first.GetProperty("extraction").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("extraction").GetProperty("origin").ValueKind);

        // Nothing that locates or fingerprints the bytes.
        Assert.DoesNotContain("workspaces/", raw);
        Assert.DoesNotContain("sha256", raw);
        Assert.DoesNotContain("objectKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checksum", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Extraction_state_is_how_the_current_versions_latest_attempt_ended()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await SeedAsync(
            _fixture.WorkspaceA,
            new Seed("Retried", 1, Extractions:
            [
                (BrandSourceExtractionStatus.Failed, BrandSourceExtractionOrigin.Extracted),
                (BrandSourceExtractionStatus.Succeeded, BrandSourceExtractionOrigin.Extracted),
            ]),
            new Seed("Corrected", 2, Extractions:
            [
                (BrandSourceExtractionStatus.Succeeded, BrandSourceExtractionOrigin.Extracted),
                (BrandSourceExtractionStatus.Succeeded, BrandSourceExtractionOrigin.Corrected),
            ]),
            new Seed("Scanned", 3, Extractions: [(BrandSourceExtractionStatus.Unsupported, BrandSourceExtractionOrigin.Extracted)]),
            new Seed("Broken", 4, Extractions: [(BrandSourceExtractionStatus.Failed, BrandSourceExtractionOrigin.Extracted)]),
            new Seed("Untouched", 5));

        var raw = await (await client.GetAsync(SourcesIn(_fixture.WorkspaceA), TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var states = JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray().ToDictionary(
            item => item.GetProperty("title").GetString()!,
            item => (
                State: item.GetProperty("extraction").GetProperty("state").GetString(),
                Origin: item.GetProperty("extraction").GetProperty("origin").GetString()));

        Assert.Equal(("Succeeded", "Extracted"), states["Retried"]);
        Assert.Equal(("Succeeded", "Corrected"), states["Corrected"]);
        Assert.Equal(("Unsupported", "Extracted"), states["Scanned"]);
        Assert.Equal(("Failed", "Extracted"), states["Broken"]);
        Assert.Equal(("NotExtracted", null), states["Untouched"]);

        // The state is reported; the text and where it lives are not.
        Assert.DoesNotContain("extracted/", raw);
    }

    // ---- Filters ----

    /// <summary>
    /// The extraction filter and the extraction each row reports are the same fact, so a filtered page can
    /// never hold a row that describes itself as something else. Asserted together for that reason: the
    /// expected titles and the state every one of them reports are checked in the same pass.
    /// </summary>
    [Theory]
    [InlineData("Succeeded", "Retried")]
    [InlineData("succeeded", "Retried")]
    [InlineData("Unsupported", "Scanned")]
    [InlineData("Failed", "Broken")]
    [InlineData("NotExtracted", "Replaced|Untouched")]
    public async Task The_extraction_filter_selects_the_current_versions_latest_attempt(string state, string expected)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await SeedExtractionsAsync(_fixture.WorkspaceA);

        var page = await ListAsync(client, _fixture.WorkspaceA, $"?extractionState={state}");

        Assert.Equal(expected.Split('|', StringSplitOptions.RemoveEmptyEntries), Titles(page));

        foreach (var item in page.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(
                state,
                item.GetProperty("extraction").GetProperty("state").GetString(),
                ignoreCase: true);
        }
    }

    [Fact]
    public async Task The_extraction_filter_combines_with_the_others_and_every_state_accounts_for_every_document()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await SeedExtractionsAsync(_fixture.WorkspaceA);
        var states = new[] { "Succeeded", "Unsupported", "Failed", "NotExtracted" };

        var everyState = new List<Guid>();
        foreach (var state in states)
        {
            var page = await ListAsync(client, _fixture.WorkspaceA, $"?extractionState={state}");
            everyState.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        }

        // The four states partition the library: every document matches exactly one of them.
        var whole = await ListAsync(client, _fixture.WorkspaceA, "?limit=100");
        var all = whole.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(5, all.Count);
        Assert.Equal(all.Count, everyState.Distinct().Count());
        Assert.Empty(all.Except(everyState));

        // And it narrows alongside a filter answered by a column on the document itself.
        Assert.Equal(["Retried"], Titles(await ListAsync(client, _fixture.WorkspaceA, "?extractionState=Succeeded&search=retr")));
        Assert.Empty(Titles(await ListAsync(client, _fixture.WorkspaceA, "?extractionState=Succeeded&purpose=VisualDirection")));
        Assert.Empty(Titles(await ListAsync(client, _fixture.WorkspaceA, "?extractionState=Failed&status=Archived")));
    }

    [Theory]
    [InlineData("?status=Archived", "Old house style")]
    [InlineData("?status=active&documentType=socialSample", "Reel captions|Pin descriptions")]
    [InlineData("?documentType=Newsletter", "Autumn newsletter")]
    [InlineData("?channelKey=instagram", "Reel captions")]
    [InlineData("?purpose=WritingStyle", "House Style")]
    [InlineData("?purpose=writingstyle&status=Archived", "Old house style")]
    [InlineData("?purpose=Voice", "Autumn newsletter|Reel captions")]
    [InlineData("?purpose=VisualDirection", "Pin descriptions")]
    [InlineData("?purpose=NotMyVoice", "")]
    [InlineData("?purpose=Voice&documentType=SocialSample", "Reel captions")]
    [InlineData("?purpose=WritingStyle&documentType=Newsletter", "")]
    [InlineData("?tag=Launch", "House Style|Autumn newsletter")]
    [InlineData("?tag=LAUNCH&tag=holiday", "House Style|Autumn newsletter|Reel captions")]
    [InlineData("?tag=launch&tag=evergreen", "House Style|Autumn newsletter")]
    [InlineData("?search=HOUSE", "House Style")]
    [InlineData("?search=brand-guide", "House Style")]
    [InlineData("?search=superseded", "")]
    [InlineData("?search=house&status=Archived", "Old house style")]
    [InlineData("?search=s", "House Style|Autumn newsletter|Reel captions|Pin descriptions")]
    [InlineData("?documentType=SocialSample&channelKey=instagram&tag=Holiday&search=reel", "Reel captions")]
    [InlineData("?documentType=SocialSample&channelKey=instagram&tag=Launch", "")]
    [InlineData("?channelKey=myspace", "")]
    [InlineData("?tag=no-such-tag", "")]
    public async Task Filters_select_what_they_describe_alone_and_combined(string query, string expected)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await SeedLibraryAsync(_fixture.WorkspaceA);

        var page = await ListAsync(client, _fixture.WorkspaceA, query);

        Assert.Equal(expected.Split('|', StringSplitOptions.RemoveEmptyEntries), Titles(page));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("?status=Removed", "status")]
    [InlineData("?status=3", "status")]
    [InlineData("?status=Active,Archived", "status")]
    [InlineData("?documentType=Spreadsheet", "documentType")]
    [InlineData("?documentType=1", "documentType")]

    // Two names that OR into a third declared value. Refused as the list it is, not answered as PublishedPost.
    [InlineData("?documentType=StyleGuide,WritingSample", "documentType")]
    [InlineData("?purpose=Loud", "purpose")]
    [InlineData("?purpose=1", "purpose")]
    [InlineData("?purpose=Voice,Background", "purpose")]
    [InlineData("?extractionState=Pending", "extractionState")]
    [InlineData("?extractionState=Extracted", "extractionState")]

    // NotExtracted is zero, and a number is never a way to name a state.
    [InlineData("?extractionState=0", "extractionState")]
    public async Task A_filter_value_that_is_not_one_of_its_names_is_refused_by_field(string query, string field)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await SeedLibraryAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(SourcesIn(_fixture.WorkspaceA, query), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.SourceInvalidRequest, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task An_overlong_search_and_too_many_tags_are_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var cancellation = TestContext.Current.CancellationToken;

        var search = await client.GetAsync(SourcesIn(_fixture.WorkspaceA, "?search=" + new string('a', 129)), cancellation);
        var tags = await client.GetAsync(
            SourcesIn(_fixture.WorkspaceA, "?" + string.Join('&', Enumerable.Range(0, 11).Select(index => $"tag=t{index}"))), cancellation);

        Assert.Equal(HttpStatusCode.BadRequest, search.StatusCode);
        Assert.True((await BodyOf(search)).GetProperty("errors").TryGetProperty("search", out _));
        Assert.Equal(HttpStatusCode.BadRequest, tags.StatusCode);
        Assert.True((await BodyOf(tags)).GetProperty("errors").TryGetProperty("tag", out _));
    }

    // ---- Paging ----

    [Fact]
    public async Task Following_the_cursor_visits_every_document_once_in_order_even_across_tied_edit_times()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        // Eleven documents, seven of them edited in the same instant: a page boundary must fall inside the tie.
        await SeedAsync(_fixture.WorkspaceA, [.. Enumerable.Range(0, 11).Select(index =>
            new Seed($"Document {index:00}", index < 7 ? 10 : index, Tags: ["Launch"]))]);

        var whole = await ListAsync(client, _fixture.WorkspaceA, "?limit=100&tag=launch");
        var expected = whole.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(11, expected.Count);

        var walked = new List<Guid>();
        var pages = 0;
        string? cursor = null;

        do
        {
            var page = await ListAsync(
                client, _fixture.WorkspaceA, "?limit=3&tag=launch" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}"));
            var items = page.GetProperty("items");

            Assert.InRange(items.GetArrayLength(), 1, 3);
            walked.AddRange(items.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(4, pages);
        Assert.Equal(expected, walked);
        Assert.Equal(11, walked.Distinct().Count());
    }

    [Theory]
    [InlineData("?limit=0", 1)]
    [InlineData("?limit=-5", 1)]
    [InlineData("?limit=2", 2)]
    [InlineData("?limit=100000", 4)]
    public async Task The_page_size_is_clamped_rather_than_refused(string query, int count)
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await SeedLibraryAsync(_fixture.WorkspaceA);

        var page = await ListAsync(client, _fixture.WorkspaceA, query);

        Assert.Equal(count, page.GetProperty("items").GetArrayLength());
        Assert.Equal(count < 4, page.GetProperty("nextCursor").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task The_list_is_bounded_however_large_the_library()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await SeedAsync(_fixture.WorkspaceA, [.. Enumerable.Range(0, 130).Select(index => new Seed($"Document {index:000}", index))]);

        var unspecified = await ListAsync(client, _fixture.WorkspaceA);
        var greedy = await ListAsync(client, _fixture.WorkspaceA, "?limit=100000");

        Assert.Equal(25, unspecified.GetProperty("items").GetArrayLength());
        Assert.Equal(100, greedy.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.String, greedy.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task A_cursor_is_refused_under_any_other_filter_set_and_when_it_is_not_a_cursor()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        await SeedLibraryAsync(_fixture.WorkspaceA);
        var cancellation = TestContext.Current.CancellationToken;

        var cursor = Uri.EscapeDataString((await ListAsync(client, _fixture.WorkspaceA, "?limit=1")).GetProperty("nextCursor").GetString()!);

        // The same position still works under the filters it was issued for, at any page size and tag order.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(SourcesIn(_fixture.WorkspaceA, $"?limit=2&status=Active&cursor={cursor}"), cancellation)).StatusCode);

        foreach (var query in new[] { $"?documentType=Newsletter&cursor={cursor}", $"?status=Archived&cursor={cursor}", $"?search=house&cursor={cursor}", $"?tag=Launch&cursor={cursor}", $"?purpose=Voice&cursor={cursor}", $"?extractionState=NotExtracted&cursor={cursor}", "?cursor=not-a-cursor", "?cursor=%00" })
        {
            var response = await client.GetAsync(SourcesIn(_fixture.WorkspaceA, query), cancellation);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await BodyOf(response);
            Assert.Equal(BrandErrorCodes.SourceInvalidRequest, body.GetProperty("code").GetString());
            Assert.True(body.GetProperty("errors").TryGetProperty("cursor", out _), query);
        }
    }

    // ---- Isolation ----

    [Fact]
    public async Task Each_workspace_lists_only_its_own_documents_tags_and_extractions()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        using var viewerB = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: cancellation);

        // The same titles, tag, channel and filename on both sides, so nothing but ownership tells them apart.
        // Each workspace's copy stands somewhere different with its text, and only B has a third document.
        await SeedAsync(
            _fixture.WorkspaceA,
            new Seed("House Style", 1, Channel: "instagram", Tags: ["Launch"], FileName: "guide.pdf",
                Extractions: [(BrandSourceExtractionStatus.Succeeded, BrandSourceExtractionOrigin.Extracted)]),
            new Seed("Newsletter", 2, Tags: ["Launch"]));
        await SeedAsync(
            _fixture.WorkspaceB,

            // B's copy also holds a Succeeded row, but a later attempt failed. Its state is Failed, so a
            // `Succeeded` filter that ignored the later-ordinal check would return it — in either workspace.
            new Seed("House Style", 1, Channel: "instagram", Tags: ["Launch"], FileName: "guide.pdf", Extractions:
            [
                (BrandSourceExtractionStatus.Succeeded, BrandSourceExtractionOrigin.Extracted),
                (BrandSourceExtractionStatus.Failed, BrandSourceExtractionOrigin.Extracted),
            ]),
            new Seed("Newsletter", 2, Tags: ["Launch"],
                Extractions: [(BrandSourceExtractionStatus.Unsupported, BrandSourceExtractionOrigin.Extracted)]),
            new Seed("Only in B", 3, Tags: ["Launch", "Private"]));

        // `?extractionState=Succeeded` is the sharpest of these: both workspaces hold a Succeeded extraction,
        // but only A's is its document's latest, so a filter whose subqueries reached across the boundary —
        // or ignored the later-ordinal check — would return B's identically named document as well.
        foreach (var query in new[] { string.Empty, "?tag=launch", "?search=house", "?channelKey=instagram", "?search=guide.pdf", "?purpose=Voice", "?extractionState=Succeeded" })
        {
            var inA = await ListAsync(ownerA, _fixture.WorkspaceA, query);
            var inB = await ListAsync(ownerB, _fixture.WorkspaceB, query);
            var idsA = inA.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
            var idsB = inB.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();

            Assert.NotEmpty(idsA);
            Assert.Empty(idsA.Intersect(idsB));
            Assert.DoesNotContain("Only in B", Titles(inA));
        }

        // A's extraction does not colour B's identically named document, and B's private tag is not A's.
        var houseA = (await ListAsync(ownerA, _fixture.WorkspaceA, "?search=house")).GetProperty("items")[0];
        var houseB = (await ListAsync(ownerB, _fixture.WorkspaceB, "?search=house")).GetProperty("items")[0];
        Assert.Equal("Succeeded", houseA.GetProperty("extraction").GetProperty("state").GetString());
        Assert.Equal("Failed", houseB.GetProperty("extraction").GetProperty("state").GetString());

        // Every state, both ways round: each workspace's answer is about its own rows only. B's superseded
        // Succeeded row must not answer a Succeeded filter in either workspace, and A's unread Newsletter must
        // not be pulled in by B's Unsupported one.
        foreach (var (state, inA, inB) in new[]
        {
            ("Succeeded", (string[])["House Style"], (string[])[]),
            ("Failed", [], ["House Style"]),
            ("Unsupported", [], ["Newsletter"]),
            ("NotExtracted", ["Newsletter"], ["Only in B"]),
        })
        {
            Assert.Equal(inA, Titles(await ListAsync(ownerA, _fixture.WorkspaceA, $"?extractionState={state}")));
            Assert.Equal(inB, Titles(await ListAsync(ownerB, _fixture.WorkspaceB, $"?extractionState={state}")));
        }

        Assert.Empty(Titles(await ListAsync(ownerA, _fixture.WorkspaceA, "?tag=Private")));
        Assert.Equal(new[] { "Only in B" }, Titles(await ListAsync(ownerB, _fixture.WorkspaceB, "?tag=Private")));

        // A Viewer may read their own workspace's library.
        Assert.Equal(3, (await ListAsync(viewerB, _fixture.WorkspaceB)).GetProperty("items").GetArrayLength());

        // A's cursor names a position in A's library. In B it is refused, not reinterpreted.
        var cursorA = Uri.EscapeDataString((await ListAsync(ownerA, _fixture.WorkspaceA, "?limit=1")).GetProperty("nextCursor").GetString()!);
        var replayed = await ownerB.GetAsync(SourcesIn(_fixture.WorkspaceB, $"?limit=1&cursor={cursorA}"), cancellation);
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);

        // And B's owner asking for A's library is told what a nonexistent workspace is told.
        var intoA = await ownerB.GetAsync(SourcesIn(_fixture.WorkspaceA), cancellation);
        var intoNowhere = await ownerB.GetAsync("/api/v1/workspaces/no-such-kitchen/brand-source-documents", cancellation);
        Assert.Equal(HttpStatusCode.NotFound, intoA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, intoNowhere.StatusCode);
        Assert.Equal(
            (await BodyOf(intoNowhere)).GetProperty("code").GetString(),
            (await BodyOf(intoA)).GetProperty("code").GetString());
    }
}
