using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Gateway;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/versions</c> through the real Gateway — real
/// cookie session, real gateway-signed internal token, real API — for the published page shape, what the list
/// never carries, how it pages, and what one workspace can learn about the other's recipes.
/// </summary>
public sealed class RecipeVersionHistoryEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string VersionsOf(SeededWorkspace workspace, Guid recipeId, string query = "") =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/versions{query}";

    // ---- The published page ----

    /// <summary>
    /// A recipe created and never edited has exactly one version, and it is version 1 with no parent. The
    /// floor of the whole feature: a history is never empty for a recipe the caller may read, which is what
    /// makes an empty answer impossible and a 404 unambiguous.
    /// </summary>
    [Fact]
    public async Task A_new_recipe_has_one_version_and_it_descends_from_nothing()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.GetAsync(
            VersionsOf(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken));
        var entry = body.GetProperty("items").EnumerateArray().Single();

        Assert.Equal(1, entry.GetProperty("versionNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("parentVersionId").ValueKind);

        // Null on the last page, so a client loops until it is null rather than comparing counts against a page
        // size the server may have clamped.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);

        // Enums as the names the schema publishes, not integers a client would have to map privately.
        Assert.Equal("CreatorEdit", entry.GetProperty("source").GetString());
        Assert.Equal("Draft", entry.GetProperty("readiness").GetString());
    }

    [Fact]
    public async Task Every_member_including_a_viewer_may_read_a_recipes_history()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB);
        var recipeId = await CreateAsync(owner, _fixture.WorkspaceB);

        // Workspace B's second member is a Viewer — the lowest role there is, and the one this must admit.
        using var viewer = await SignInAsync(_fixture.WorkspaceB, asOwner: false);

        var response = await viewer.GetAsync(
            VersionsOf(_fixture.WorkspaceB, recipeId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await ReadAsync(response)).GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// The restriction this route exists under: a history lists metadata, never content. Asserted on the raw
    /// JSON as well as on the property set, so a snapshot arriving nested inside some future field would still
    /// be caught.
    /// </summary>
    [Fact]
    public async Task A_history_entry_carries_no_snapshot_and_no_ownership_detail()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, headnote: "The one my grandmother made.");

        var response = await client.GetAsync(
            VersionsOf(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("snapshot", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("document", json, StringComparison.OrdinalIgnoreCase);

        // A phrase that exists only inside the recipe's content, so its absence says the archive was not read
        // rather than only that no field is named after it.
        Assert.DoesNotContain("grandmother", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("workspaceId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("membership", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rowVersion", json, StringComparison.OrdinalIgnoreCase);

        // Pinned as the exact property set, so a field added later is a decision someone makes rather than a
        // leak nobody noticed.
        var entry = (await ReadAsync(response)).GetProperty("items").EnumerateArray().Single();

        Assert.Equal(
            (string[])
            [
                "id", "versionNumber", "source", "readiness", "reason", "createdAt", "createdByName",
                "parentVersionId", "restoredFromVersionId", "aiProposalId",
            ],
            entry.EnumerateObject().Select(property => property.Name));
    }

    // ---- Newest first, and lineage ----

    [Fact]
    public async Task The_list_is_newest_first_and_each_version_names_the_one_it_came_from()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        await EditAsync(client, _fixture.WorkspaceA, recipeId, "Lemon olive oil cake", "Brighter with lemon.");
        await EditAsync(client, _fixture.WorkspaceA, recipeId, "Lemon and thyme cake", "Added thyme.");

        var entries = (await ReadAsync(await client.GetAsync(
                VersionsOf(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken)))
            .GetProperty("items").EnumerateArray().ToList();

        Assert.Equal([3, 2, 1], entries.Select(entry => entry.GetProperty("versionNumber").GetInt32()));

        // Lineage: each version's parent is the one below it, and version 1 descends from nothing. Chained by
        // id rather than by number, because the number is exactly what a restore will stop agreeing with.
        Assert.Equal(
            entries[1].GetProperty("id").GetGuid(), entries[0].GetProperty("parentVersionId").GetGuid());
        Assert.Equal(
            entries[2].GetProperty("id").GetGuid(), entries[1].GetProperty("parentVersionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, entries[2].GetProperty("parentVersionId").ValueKind);

        // The creator's words, kept on the version they were given for.
        Assert.Equal("Added thyme.", entries[0].GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, entries[2].GetProperty("reason").ValueKind);
    }

    /// <summary>
    /// The proposal link. No route writes an <c>AiProposalAccepted</c> version yet — accepting a proposal is a
    /// later seam — so what is pinned here is that the field is published and null until one does, which is the
    /// half a client builds against today.
    /// </summary>
    [Fact]
    public async Task A_version_written_by_a_creator_carries_no_proposal_link()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);
        await EditAsync(client, _fixture.WorkspaceA, recipeId, "Lemon olive oil cake");

        var entries = (await ReadAsync(await client.GetAsync(
                VersionsOf(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken)))
            .GetProperty("items").EnumerateArray().ToList();

        Assert.All(entries, entry =>
        {
            Assert.Equal("CreatorEdit", entry.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("aiProposalId").ValueKind);
        });
    }

    /// <summary>
    /// The history is the record of what happened, so an edit that changed nothing must not appear in it — and
    /// the numbering must not skip to account for one.
    /// </summary>
    [Fact]
    public async Task An_edit_that_changed_nothing_adds_no_entry()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        await EditAsync(client, _fixture.WorkspaceA, recipeId, "Olive oil cake", "No change, honestly.");

        var entries = (await ReadAsync(await client.GetAsync(
                VersionsOf(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken)))
            .GetProperty("items").EnumerateArray().ToList();

        Assert.Equal([1], entries.Select(entry => entry.GetProperty("versionNumber").GetInt32()));
    }

    // ---- Paging ----

    [Fact]
    public async Task Following_the_cursor_walks_the_whole_history_once()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        for (var edit = 0; edit < 4; edit++)
        {
            await EditAsync(client, _fixture.WorkspaceA, recipeId, $"Cake take {edit}");
        }

        var seen = new List<int>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var query = cursor is null ? "?limit=2" : $"?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var body = await ReadAsync(await client.GetAsync(
                VersionsOf(_fixture.WorkspaceA, recipeId, query), TestContext.Current.CancellationToken));

            seen.AddRange(body.GetProperty("items").EnumerateArray()
                .Select(entry => entry.GetProperty("versionNumber").GetInt32()));

            cursor = body.GetProperty("nextCursor").GetString();
            if (cursor is null)
            {
                break;
            }
        }

        // Five versions, once each, still newest first across the page boundaries — the failure a keyset exists
        // to prevent is a repeated or skipped row at exactly those boundaries.
        Assert.Null(cursor);
        Assert.Equal([5, 4, 3, 2, 1], seen);
    }

    /// <summary>The page size is clamped, not refused: a client cannot fail a read by asking for too much.</summary>
    [Fact]
    public async Task An_oversized_page_is_clamped_rather_than_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            VersionsOf(_fixture.WorkspaceA, recipeId, "?limit=100000"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await ReadAsync(response)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_cursor_that_is_not_one_is_refused_with_the_cursors_own_code()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            VersionsOf(_fixture.WorkspaceA, recipeId, "?cursor=not-a-cursor"), TestContext.Current.CancellationToken);
        var problem = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("cursor", out _));
    }

    /// <summary>
    /// The recipe is folded into the cursor's scope precisely so this is a refusal rather than a page. Without
    /// it, page two of one recipe's history would decode cleanly against another's and return its versions from
    /// the same number downwards — a wrong answer, which is worse than an error.
    /// </summary>
    [Fact]
    public async Task A_cursor_minted_for_one_recipe_is_refused_on_another()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var first = await CreateAsync(client, _fixture.WorkspaceA, title: "Olive oil cake");
        var second = await CreateAsync(client, _fixture.WorkspaceA, title: "Weeknight chilli");

        await EditAsync(client, _fixture.WorkspaceA, first, "Lemon olive oil cake");
        await EditAsync(client, _fixture.WorkspaceA, second, "Weeknight chilli, hotter");

        var page = await ReadAsync(await client.GetAsync(
            VersionsOf(_fixture.WorkspaceA, first, "?limit=1"), TestContext.Current.CancellationToken));
        var cursor = page.GetProperty("nextCursor").GetString()!;

        var response = await client.GetAsync(
            VersionsOf(_fixture.WorkspaceA, second, $"?limit=1&cursor={Uri.EscapeDataString(cursor)}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.CursorInvalidRequest, (await ReadAsync(response)).GetProperty("code").GetString());
    }

    // ---- Two workspaces ----

    /// <summary>
    /// The restriction stated plainly: an unknown recipe and another workspace's recipe are one answer, down to
    /// the bytes of the body. Anything that distinguished them would let a caller enumerate recipe ids across
    /// the boundary (tenancy.md).
    /// </summary>
    [Fact]
    public async Task An_unknown_recipe_and_another_workspaces_recipe_answer_identically()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var inB = await CreateAsync(ownerB, _fixture.WorkspaceB);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);

        var unknown = await ownerA.GetAsync(
            VersionsOf(_fixture.WorkspaceA, Guid.NewGuid()), TestContext.Current.CancellationToken);
        var othersRecipe = await ownerA.GetAsync(
            VersionsOf(_fixture.WorkspaceA, inB), TestContext.Current.CancellationToken);

        // Read once each: the content stream is consumed by the first read, and both assertions below need it.
        var unknownBody = await ReadAsync(unknown);
        var othersBody = await ReadAsync(othersRecipe);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, othersRecipe.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, unknownBody.GetProperty("code").GetString());

        // Field for field, less the trace id every problem carries.
        Assert.Equal(WithoutTrace(unknownBody), WithoutTrace(othersBody));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_read_a_history_through_the_others_slug()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        var inA = await CreateAsync(ownerA, _fixture.WorkspaceA);

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var response = await ownerB.GetAsync(
            VersionsOf(_fixture.WorkspaceA, inA), TestContext.Current.CancellationToken);

        // B's owner names A's slug: 404, not 403, so the reply does not confirm that Workspace A exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Two recipes with the same history depth in the two workspaces, so isolation cannot pass by the two being
    /// distinguishable — and the version ids of one must never appear in the other's page.
    /// </summary>
    [Fact]
    public async Task Each_workspace_reads_only_its_own_recipes_history()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var inA = await CreateAsync(ownerA, _fixture.WorkspaceA, title: "Shared title");
        var inB = await CreateAsync(ownerB, _fixture.WorkspaceB, title: "Shared title");

        await EditAsync(ownerA, _fixture.WorkspaceA, inA, "A's edit");
        await EditAsync(ownerB, _fixture.WorkspaceB, inB, "B's edit");

        var historyA = await ReadAsync(await ownerA.GetAsync(
            VersionsOf(_fixture.WorkspaceA, inA), TestContext.Current.CancellationToken));
        var historyB = await ReadAsync(await ownerB.GetAsync(
            VersionsOf(_fixture.WorkspaceB, inB), TestContext.Current.CancellationToken));

        Assert.Equal(2, historyA.GetProperty("items").GetArrayLength());
        Assert.Equal(2, historyB.GetProperty("items").GetArrayLength());
        Assert.Empty(VersionIds(historyA).Intersect(VersionIds(historyB)));
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail,
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<Guid> CreateAsync(
        GatewayClient client, SeededWorkspace workspace, string title = "Olive oil cake", string? headnote = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title, headnote },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await ReadAsync(response)).GetProperty("recipeId").GetGuid();
    }

    /// <summary>
    /// Edits the recipe, which is the only way to write a second version — reading the current token first,
    /// because an edit must quote the state it was composed against.
    /// </summary>
    private static async Task EditAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId, string title, string? reason = null)
    {
        var token = TestContext.Current.CancellationToken;
        var route = $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}";

        var detail = await ReadAsync(await client.GetAsync(route, token));

        var response = await client.PatchAsJsonAsync(
            route,
            new { expectedConcurrencyToken = detail.GetProperty("concurrencyToken").GetString(), title, reason },
            token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static IEnumerable<Guid> VersionIds(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(entry => entry.GetProperty("id").GetGuid());

    /// <summary>
    /// A problem body with its trace id removed, so two refusals can be compared for being the same refusal
    /// rather than for having happened in the same request.
    /// </summary>
    private static string WithoutTrace(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(property => !property.Name.Contains("traceId", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Contains("correlation", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(property => property.Name, property => property.Value.ToString()));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
