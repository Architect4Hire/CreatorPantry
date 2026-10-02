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
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <c>GET .../brand-style-guides/{guideId}/versions</c> through the real Gateway: the order, the keyset, the
/// derived status and active marker, the stale-source count, and that one workspace's history is invisible
/// from the other.
/// </summary>
/// <remarks>
/// Versions after the first, approvals and the workspace default are seeded directly, as in
/// <see cref="BrandStyleGuideReadEndpointTests"/>: nothing in the API writes them yet (11A.15 edits, 11A.16c
/// activates), and the list has to be right about them before it does. Replacements are made through the real
/// route, because staleness is a fact about what a replacement leaves behind.
/// </remarks>
public sealed class BrandStyleGuideVersionListEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Moment = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(new InMemoryPrivateObjectStore());
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(new FakeMalwareScanGateway());
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---- Order and shape ----

    [Fact]
    public async Task The_history_is_newest_first_and_carries_metadata_rather_than_content()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "House voice",
            questionnaire = new { voice = "Warm." },
        });

        var second = await AddVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId);
        var third = await AddVersionAsync(_fixture.WorkspaceA, guide.Id, 3, second);

        var response = await ListAsync(client, _fixture.WorkspaceA, guide.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var page = await BodyOf(response);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);

        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([3, 2, 1], items.Select(item => item.GetProperty("versionNumber").GetInt32()));
        Assert.Equal([third, second, guide.VersionId], items.Select(item => item.GetProperty("id").GetGuid()));

        // Every version here is a draft, nothing is the workspace default, and nothing cites a source.
        Assert.All(items, item =>
        {
            Assert.Equal("Draft", item.GetProperty("status").GetString());
            Assert.False(item.GetProperty("isActive").GetBoolean());
            Assert.Equal(0, item.GetProperty("sourceCount").GetInt32());
            Assert.Equal(0, item.GetProperty("staleSourceCount").GetInt32());
            Assert.NotEqual(Guid.Empty, item.GetProperty("createdByMembershipId").GetGuid());
        });

        // The actor and the reason are the version's own, not the guide's.
        Assert.Equal("Revision 3", items[0].GetProperty("changeReason").GetString());
        Assert.Equal(Moment.AddDays(3), items[0].GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, items[^1].GetProperty("changeReason").ValueKind);

        // A list of versions, not of what they say.
        var raw = page.GetRawText();
        Assert.All(
            new[] { "sections", "rules", "sourceDocuments", "body", "Warm.", "displayName", "concurrencyToken", "workspaceId" },
            forbidden => Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Status_comes_from_approval_and_the_active_marker_from_the_workspace_default()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "House voice" });
        var second = await AddVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId);

        // Version 1 is approved and is what the workspace writes with; version 2 is an unapproved edit.
        await ApproveAsync(_fixture.WorkspaceA, guide.VersionId);
        await ActivateAsync(_fixture.WorkspaceA, guide.VersionId);

        var items = await ItemsAsync(client, _fixture.WorkspaceA, guide.Id);

        Assert.Equal("Draft", items[0].GetProperty("status").GetString());
        Assert.False(items[0].GetProperty("isActive").GetBoolean());
        Assert.Equal(second, items[0].GetProperty("id").GetGuid());

        Assert.Equal("Approved", items[1].GetProperty("status").GetString());
        Assert.True(items[1].GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task An_approved_version_is_not_active_and_another_guides_default_marks_nothing_here()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var mine = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "Mine" });
        var other = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "Other" });

        // Approved here, but the workspace writes with the other guide: approval is not activation.
        await ApproveAsync(_fixture.WorkspaceA, mine.VersionId);
        await ApproveAsync(_fixture.WorkspaceA, other.VersionId);
        await ActivateAsync(_fixture.WorkspaceA, other.VersionId);

        var row = Assert.Single(await ItemsAsync(client, _fixture.WorkspaceA, mine.Id));

        Assert.Equal("Approved", row.GetProperty("status").GetString());
        Assert.False(row.GetProperty("isActive").GetBoolean());

        // And the guide that does hold the default says so.
        Assert.True(Assert.Single(await ItemsAsync(client, _fixture.WorkspaceA, other.Id)).GetProperty("isActive").GetBoolean());
    }

    // ---- Staleness ----

    [Fact]
    public async Task A_citation_is_stale_only_once_its_document_has_moved_past_it()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var replaced = await UploadAsync(client, _fixture.WorkspaceA, "house-style.pdf");
        var untouched = await UploadAsync(client, _fixture.WorkspaceA, "newsletter.pdf");

        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new
        {
            displayName = "Sourced",
            sourceDocuments = new[]
            {
                new { documentId = replaced.Id, versionNumber = 1 },
                new { documentId = untouched.Id, versionNumber = 1 },
            },
        });

        // Both citations are still each document's current version.
        var fresh = Assert.Single(await ItemsAsync(client, _fixture.WorkspaceA, guide.Id));
        Assert.Equal(2, fresh.GetProperty("sourceCount").GetInt32());
        Assert.Equal(0, fresh.GetProperty("staleSourceCount").GetInt32());

        await ReplaceAsync(client, _fixture.WorkspaceA, replaced);

        // The citation still pins version 1; the document is now on version 2, so that one is superseded.
        var stale = Assert.Single(await ItemsAsync(client, _fixture.WorkspaceA, guide.Id));
        Assert.Equal(2, stale.GetProperty("sourceCount").GetInt32());
        Assert.Equal(1, stale.GetProperty("staleSourceCount").GetInt32());

        // Shelving the other document does not change what the guide was written from.
        await ArchiveAsync(_fixture.WorkspaceA, untouched.Id);

        var archived = Assert.Single(await ItemsAsync(client, _fixture.WorkspaceA, guide.Id));
        Assert.Equal(2, archived.GetProperty("sourceCount").GetInt32());
        Assert.Equal(1, archived.GetProperty("staleSourceCount").GetInt32());
    }

    // ---- Paging ----

    [Fact]
    public async Task Following_the_cursor_visits_every_version_once_in_order()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "Long history" });

        var parent = guide.VersionId;
        for (var number = 2; number <= 7; number++)
        {
            parent = await AddVersionAsync(_fixture.WorkspaceA, guide.Id, number, parent);
        }

        var walked = new List<int>();
        var pages = 0;
        string? cursor = null;

        do
        {
            var page = await BodyOf(await ListAsync(
                client, _fixture.WorkspaceA, guide.Id,
                "?limit=2" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}")));
            var items = page.GetProperty("items");

            Assert.InRange(items.GetArrayLength(), 1, 2);
            walked.AddRange(items.EnumerateArray().Select(item => item.GetProperty("versionNumber").GetInt32()));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        // Seven versions in pages of two: four pages, and the last one does not offer a cursor.
        Assert.Equal(4, pages);
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
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "Three" });
        var second = await AddVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId);
        await AddVersionAsync(_fixture.WorkspaceA, guide.Id, 3, second);

        var page = await BodyOf(await ListAsync(client, _fixture.WorkspaceA, guide.Id, query));

        Assert.Equal(count, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_cursor_is_refused_on_another_guide_and_when_it_is_not_a_cursor()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var guide = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "First" });
        var sibling = await CreateGuideAsync(client, _fixture.WorkspaceA, new { displayName = "Second" });
        await AddVersionAsync(_fixture.WorkspaceA, guide.Id, 2, guide.VersionId);
        await AddVersionAsync(_fixture.WorkspaceA, sibling.Id, 2, sibling.VersionId);

        var cursor = Uri.EscapeDataString(
            (await BodyOf(await ListAsync(client, _fixture.WorkspaceA, guide.Id, "?limit=1"))).GetProperty("nextCursor").GetString()!);

        // The position still works on the guide it was issued for, at any page size.
        Assert.Equal(
            HttpStatusCode.OK,
            (await ListAsync(client, _fixture.WorkspaceA, guide.Id, $"?limit=5&cursor={cursor}")).StatusCode);

        // On the sibling it names a position in a history it does not belong to: refused, not reinterpreted.
        foreach (var query in new[] { $"?cursor={cursor}", "?cursor=not-a-cursor", "?cursor=%00" })
        {
            var guideId = query.StartsWith("?cursor=not", StringComparison.Ordinal) ? guide.Id : sibling.Id;
            var response = await ListAsync(client, _fixture.WorkspaceA, guideId, query);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await BodyOf(response);
            Assert.Equal(BrandErrorCodes.GuideInvalidRequest, body.GetProperty("code").GetString());
            Assert.True(body.GetProperty("errors").TryGetProperty("cursor", out _), query);
        }
    }

    // ---- Not found and isolation ----

    [Fact]
    public async Task An_unknown_guide_and_another_workspaces_are_the_same_answer()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);
        var inB = await CreateGuideAsync(ownerB, _fixture.WorkspaceB, new { displayName = "B's guide" });

        var foreign = await ListAsync(ownerA, _fixture.WorkspaceA, inB.Id);
        var unknown = await ListAsync(ownerA, _fixture.WorkspaceA, Guid.NewGuid());
        var malformed = await ownerA.GetAsync($"{GuidesIn(_fixture.WorkspaceA)}/not-a-guid/versions", cancellation);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Equal(BrandErrorCodes.GuideNotFound, (await BodyOf(foreign)).GetProperty("code").GetString());
        Assert.Equal(BrandErrorCodes.GuideNotFound, (await BodyOf(unknown)).GetProperty("code").GetString());

        // A member of B asking A's route is told what a workspace that does not exist is told.
        var intoA = await ListAsync(ownerB, _fixture.WorkspaceA, inB.Id);
        var intoNowhere = await ownerB.GetAsync(
            $"/api/v1/workspaces/no-such-kitchen/brand-style-guides/{inB.Id}/versions", cancellation);
        Assert.Equal(HttpStatusCode.NotFound, intoA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, intoNowhere.StatusCode);
    }

    [Fact]
    public async Task Each_workspace_lists_only_its_own_history()
    {
        using var ownerA = await OwnerOf(_fixture.WorkspaceA);
        using var ownerB = await OwnerOf(_fixture.WorkspaceB);

        // The same guide name and the same version count on both sides, so nothing but ownership separates them.
        var inA = await CreateGuideAsync(ownerA, _fixture.WorkspaceA, new { displayName = "House voice" });
        var inB = await CreateGuideAsync(ownerB, _fixture.WorkspaceB, new { displayName = "House voice" });
        await AddVersionAsync(_fixture.WorkspaceA, inA.Id, 2, inA.VersionId);
        await AddVersionAsync(_fixture.WorkspaceB, inB.Id, 2, inB.VersionId);
        await AddVersionAsync(_fixture.WorkspaceB, inB.Id, 3, inB.VersionId);

        // B's approval and B's default never reach across.
        await ApproveAsync(_fixture.WorkspaceB, inB.VersionId);
        await ActivateAsync(_fixture.WorkspaceB, inB.VersionId);

        var fromA = await ItemsAsync(ownerA, _fixture.WorkspaceA, inA.Id);
        var fromB = await ItemsAsync(ownerB, _fixture.WorkspaceB, inB.Id);

        Assert.Equal(2, fromA.Count);
        Assert.Equal(3, fromB.Count);
        Assert.All(fromA, item =>
        {
            Assert.Equal("Draft", item.GetProperty("status").GetString());
            Assert.False(item.GetProperty("isActive").GetBoolean());
        });
        Assert.Single(fromB, item => item.GetProperty("isActive").GetBoolean());

        // No version id crosses the boundary in either direction.
        var idsInB = fromB.Select(item => item.GetProperty("id").GetGuid()).ToHashSet();
        Assert.DoesNotContain(fromA.Select(item => item.GetProperty("id").GetGuid()), idsInB.Contains);

        // A's cursor names a position in A's history. In B it is refused, not reinterpreted.
        var cursorA = Uri.EscapeDataString(
            (await BodyOf(await ListAsync(ownerA, _fixture.WorkspaceA, inA.Id, "?limit=1"))).GetProperty("nextCursor").GetString()!);
        var replayed = await ListAsync(ownerB, _fixture.WorkspaceB, inB.Id, $"?limit=1&cursor={cursorA}");
        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
    }

    [Fact]
    public async Task A_viewer_can_list_versions()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceB);
        var guide = await CreateGuideAsync(owner, _fixture.WorkspaceB, new { displayName = "Readable" });

        using var viewer = await _fixture.SignInAsync(
            _fixture.WorkspaceB.MemberEmail, cancellationToken: TestContext.Current.CancellationToken);
        var response = await ListAsync(viewer, _fixture.WorkspaceB, guide.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await BodyOf(response)).GetProperty("items").GetArrayLength());
    }

    // ---- Harness ----

    /// <summary>A guide as created, and the id of its version 1.</summary>
    private sealed record SeededGuide(Guid Id, Guid VersionId);

    /// <summary>A source document as uploaded, with the token a replacement of it has to quote.</summary>
    private sealed record SeededDocument(Guid Id, string Token);

    private static string GuidesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-style-guides";

    private static string SourcesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-source-documents";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private Task<HttpResponseMessage> ListAsync(
        GatewayClient client, SeededWorkspace workspace, Guid guideId, string query = "") =>
        client.GetAsync($"{GuidesIn(workspace)}/{guideId}/versions{query}", TestContext.Current.CancellationToken);

    private async Task<List<JsonElement>> ItemsAsync(GatewayClient client, SeededWorkspace workspace, Guid guideId)
    {
        var response = await ListAsync(client, workspace, guideId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await BodyOf(response)).GetProperty("items").EnumerateArray()];
    }

    private async Task<SeededGuide> CreateGuideAsync(GatewayClient client, SeededWorkspace workspace, object body)
    {
        var response = await client.PostAsJsonAsync(
            GuidesIn(workspace), body, Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await BodyOf(response);

        return new SeededGuide(
            created.GetProperty("id").GetGuid(), created.GetProperty("version").GetProperty("id").GetGuid());
    }

    private async Task<SeededDocument> UploadAsync(GatewayClient client, SeededWorkspace workspace, string fileName)
    {
        var response = await client.PostAsync(
            SourcesIn(workspace), FileForm(fileName), Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await BodyOf(response);

        return new SeededDocument(
            created.GetProperty("id").GetGuid(), created.GetProperty("concurrencyToken").GetString()!);
    }

    /// <summary>Replaces the document's file, which moves it to version 2 and supersedes every citation of 1.</summary>
    private async Task ReplaceAsync(GatewayClient client, SeededWorkspace workspace, SeededDocument document)
    {
        var form = FileForm("house-style-v2.pdf", "the second file", metadata: false);
        form.Add(new StringContent(document.Token), "expectedConcurrencyToken");

        var response = await client.PostAsync(
            $"{SourcesIn(workspace)}/{document.Id}/versions",
            form,
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static MultipartFormDataContent FileForm(
        string fileName, string contents = "house style", bool metadata = true)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(BrandSourceSampleFiles.Pdf(contents));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);

        if (metadata)
        {
            form.Add(new StringContent(fileName), "title");
            form.Add(new StringContent("StyleGuide"), "documentType");
            form.Add(new StringContent("Voice"), "purpose");
        }

        return form;
    }

    private async Task<T> InScopeAsync<T>(SeededWorkspace workspace, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    /// <summary>Adds a later version, as an edit will (11A.15), and returns its id.</summary>
    private Task<Guid> AddVersionAsync(SeededWorkspace workspace, Guid guideId, int number, Guid parentId) =>
        InScopeAsync(workspace, async db =>
        {
            var version = new BrandStyleGuideVersion
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                BrandStyleGuideId = guideId,
                VersionNumber = number,
                ParentVersionId = parentId,
                ChangeReason = $"Revision {number}",
                CreatedByMembershipId = Guid.NewGuid(),
                CreatedAt = Moment.AddDays(number),
            };
            db.BrandStyleGuideVersions.Add(version);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return version.Id;
        });

    /// <summary>Approves a version without activating it, as 11A.16c's approval step will.</summary>
    private Task ApproveAsync(SeededWorkspace workspace, Guid versionId) =>
        InScopeAsync(workspace, async db =>
        {
            db.BrandStyleGuideApprovals.Add(new BrandStyleGuideApproval
            {
                WorkspaceId = workspace.Id,
                BrandStyleGuideVersionId = versionId,
                Reason = "Signed off",
                ApprovedByMembershipId = Guid.NewGuid(),
                ApprovedAt = Moment,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return 0;
        });

    /// <summary>Makes an approved version the workspace default, as 11A.16c will.</summary>
    private Task ActivateAsync(SeededWorkspace workspace, Guid versionId) =>
        InScopeAsync(workspace, async db =>
        {
            db.BrandStyleGuideDefaults.Add(new BrandStyleGuideDefault
            {
                WorkspaceId = workspace.Id,
                BrandStyleGuideVersionId = versionId,
                Reason = "Launch voice",
                ActivatedByMembershipId = Guid.NewGuid(),
                ActivatedAt = Moment.AddHours(1),
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return 0;
        });

    /// <summary>Shelves a source document, which changes nothing about what a guide cited from it.</summary>
    private Task ArchiveAsync(SeededWorkspace workspace, Guid documentId) =>
        InScopeAsync(workspace, async db =>
        {
            var document = await db.BrandSourceDocuments.SingleAsync(
                candidate => candidate.Id == documentId, TestContext.Current.CancellationToken);
            document.Status = BrandSourceDocumentStatus.Archived;
            document.ArchivedAt = Moment;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            return 0;
        });
}
