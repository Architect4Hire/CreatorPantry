using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Gateway;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceSlug}/recipes</c> through the real Gateway — real cookie session, real
/// gateway-signed internal token, real API — for the published page shape, the filters as a client actually
/// spells them in a URL, the stable refusal codes, and what a cursor cannot reach.
/// </summary>
public sealed class RecipeSearchEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string RecipesIn(SeededWorkspace workspace, string query = "") =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes{query}";

    // ---- The published page ----

    [Fact]
    public async Task An_empty_library_is_an_empty_page_rather_than_a_not_found()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(RecipesIn(_fixture.WorkspaceA), TestContext.Current.CancellationToken);
        var body = await ReadAsync(response);

        // A member asking about their own empty library is not a 404. There is nothing here whose existence needs
        // hiding — their membership of this workspace is not in doubt by the time the action runs.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_created_recipe_appears_in_the_library_with_its_version_and_review_state()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA, "Olive oil cake");

        var body = await ReadAsync(await client.GetAsync(
            RecipesIn(_fixture.WorkspaceA), TestContext.Current.CancellationToken));
        var item = body.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("Olive oil cake", item.GetProperty("title").GetString());

        // Enums as the names the schema publishes, not integers a client would have to map privately.
        Assert.Equal("Draft", item.GetProperty("status").GetString());
        Assert.Equal(1, item.GetProperty("latestVersionNumber").GetInt32());
        Assert.Equal("Draft", item.GetProperty("latestVersionReadiness").GetString());

        // A recipe created through the contract has no ingredient lines yet, so nothing is unresolved.
        Assert.False(item.GetProperty("hasUnmatchedIngredients").GetBoolean());
    }

    /// <summary>
    /// The same rule the detail read follows: <c>WorkspaceId</c> and the membership columns never leave the
    /// server. Here it has a visible consequence — a library card cannot name who wrote a recipe — which is
    /// recorded rather than discovered.
    /// </summary>
    [Fact]
    public async Task A_summary_carries_no_ownership_or_authorship_detail()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA, "Olive oil cake");

        var response = await client.GetAsync(RecipesIn(_fixture.WorkspaceA), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("workspaceId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("membership", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rowVersion", json, StringComparison.OrdinalIgnoreCase);

        // Pinned as the exact property set, so a field added later is a decision someone makes rather than a
        // leak nobody noticed.
        var item = (await ReadAsync(response)).GetProperty("items").EnumerateArray().Single();

        Assert.Equal(
            (string[])
            [
                "id", "title", "description", "status", "cuisineId", "courseId", "createdAt", "updatedAt",
                "latestVersionNumber", "latestVersionReadiness", "hasUnmatchedIngredients",
            ],
            item.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Every_member_including_a_viewer_may_search()
    {
        // Workspace B's second member is a Viewer — the lowest role there is, and the one this must admit.
        using var client = await SignInAsync(_fixture.WorkspaceB, asOwner: false);

        var response = await client.GetAsync(RecipesIn(_fixture.WorkspaceB), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Filters, as a client spells them ----

    [Fact]
    public async Task A_comma_separated_status_filter_narrows_the_library()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        await CreateAsync(client, _fixture.WorkspaceA, "A draft");
        await CreateAsync(client, _fixture.WorkspaceA, "A ready one", status: "Ready");

        // Two states, not three: a create refuses Archived deliberately, and archiving is its own command that
        // does not exist yet. The repository tests cover all three against real SQL.
        var titles = await TitlesAsync(client, _fixture.WorkspaceA, "?status=Draft,Ready");

        Assert.Equal(["A draft", "A ready one"], titles.Order(StringComparer.Ordinal));

        Assert.Equal(["A ready one"], await TitlesAsync(client, _fixture.WorkspaceA, "?status=Ready"));
    }

    [Fact]
    public async Task A_search_term_matches_the_title()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        await CreateAsync(client, _fixture.WorkspaceA, "Olive oil cake");
        await CreateAsync(client, _fixture.WorkspaceA, "Weeknight chilli");

        Assert.Equal(["Olive oil cake"], await TitlesAsync(client, _fixture.WorkspaceA, "?search=olive"));
    }

    [Fact]
    public async Task The_sort_parameter_accepts_only_the_named_orderings()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        await CreateAsync(client, _fixture.WorkspaceA, "Cake");
        await CreateAsync(client, _fixture.WorkspaceA, "Apple pie");

        Assert.Equal(["Apple pie", "Cake"], await TitlesAsync(client, _fixture.WorkspaceA, "?sort=Title"));

        // Not a column name. An arbitrary sort expression is refused rather than interpreted.
        var refused = await client.GetAsync(
            RecipesIn(_fixture.WorkspaceA, "?sort=UpdatedAt%20DESC"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(RecipeErrorCodes.SearchInvalidRequest, (await ReadAsync(refused)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Mine_returns_only_the_callers_own_recipes()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceA);
        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);

        await CreateAsync(owner, _fixture.WorkspaceA, "By the owner");
        await CreateAsync(member, _fixture.WorkspaceA, "By the member");

        Assert.Equal(["By the owner"], await TitlesAsync(owner, _fixture.WorkspaceA, "?mine=true"));
        Assert.Equal(["By the member"], await TitlesAsync(member, _fixture.WorkspaceA, "?mine=true"));
    }

    // ---- Paging ----

    [Fact]
    public async Task Following_the_cursor_walks_the_whole_library_once()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        for (var index = 0; index < 5; index++)
        {
            await CreateAsync(client, _fixture.WorkspaceA, $"Recipe {index}");
        }

        var seen = new List<string>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var query = cursor is null ? "?limit=2" : $"?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var body = await ReadAsync(await client.GetAsync(
                RecipesIn(_fixture.WorkspaceA, query), TestContext.Current.CancellationToken));

            seen.AddRange(body.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("title").GetString()!));

            // The total spans every page, so it is the same on each one.
            Assert.Equal(5, body.GetProperty("totalCount").GetInt32());

            cursor = body.GetProperty("nextCursor").GetString();
            if (cursor is null)
            {
                break;
            }
        }

        Assert.Null(cursor);
        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_total_can_be_declined()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA, "Olive oil cake");

        var body = await ReadAsync(await client.GetAsync(
            RecipesIn(_fixture.WorkspaceA, "?includeTotal=false"), TestContext.Current.CancellationToken));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("totalCount").ValueKind);
        Assert.Single(body.GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// Changing a filter invalidates the cursor issued under the old one, and says so with its own code — so a
    /// paging client starts the list again rather than retrying a cursor that will never be accepted, and never
    /// receives a page that silently skipped rows.
    /// </summary>
    [Fact]
    public async Task A_cursor_is_refused_once_the_filters_change_under_it()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        await CreateAsync(client, _fixture.WorkspaceA, "One");
        await CreateAsync(client, _fixture.WorkspaceA, "Two");

        var first = await ReadAsync(await client.GetAsync(
            RecipesIn(_fixture.WorkspaceA, "?limit=1"), TestContext.Current.CancellationToken));
        var cursor = first.GetProperty("nextCursor").GetString()!;

        var refused = await client.GetAsync(
            RecipesIn(_fixture.WorkspaceA, $"?limit=1&sort=Title&cursor={Uri.EscapeDataString(cursor)}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, (await ReadAsync(refused)).GetProperty("code").GetString());
    }

    /// <summary>The page size is clamped, not refused: a client cannot fail a read by asking for too much.</summary>
    [Fact]
    public async Task An_oversized_page_is_clamped_rather_than_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        await CreateAsync(client, _fixture.WorkspaceA, "Olive oil cake");

        var response = await client.GetAsync(
            RecipesIn(_fixture.WorkspaceA, "?limit=100000"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await ReadAsync(response)).GetProperty("items").EnumerateArray());
    }

    // ---- Refusals ----

    [Fact]
    public async Task An_unknown_filter_value_is_refused_with_a_stable_code_naming_the_parameter()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(
            RecipesIn(_fixture.WorkspaceA, "?status=Published"), TestContext.Current.CancellationToken);
        var problem = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(RecipeErrorCodes.SearchInvalidRequest, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("status", out _));
    }

    [Fact]
    public async Task A_malformed_id_in_a_list_is_refused_naming_that_parameter()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(
            RecipesIn(_fixture.WorkspaceA, "?tag=not-a-guid"), TestContext.Current.CancellationToken);
        var problem = await ReadAsync(response);

        // The payoff of splitting comma-separated lists ourselves: this module names the parameter, rather than
        // the framework's body-oriented binding failure standing in for it.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.SearchInvalidRequest, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("tag", out _));
    }

    // ---- Two workspaces ----

    /// <summary>
    /// Both workspaces hold a recipe with the same title, so isolation cannot pass by the two being
    /// distinguishable.
    /// </summary>
    [Fact]
    public async Task Each_workspace_lists_and_counts_only_its_own_recipes()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        await CreateAsync(ownerA, _fixture.WorkspaceA, "Shared title");
        await CreateAsync(ownerB, _fixture.WorkspaceB, "Shared title");
        await CreateAsync(ownerB, _fixture.WorkspaceB, "Only in B");

        var inA = await ReadAsync(await ownerA.GetAsync(
            RecipesIn(_fixture.WorkspaceA), TestContext.Current.CancellationToken));
        var inB = await ReadAsync(await ownerB.GetAsync(
            RecipesIn(_fixture.WorkspaceB), TestContext.Current.CancellationToken));

        Assert.Equal(1, inA.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, inB.GetProperty("totalCount").GetInt32());

        Assert.Empty(Ids(inA).Intersect(Ids(inB)));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_list_the_others_recipes()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        await CreateAsync(ownerA, _fixture.WorkspaceA, "Olive oil cake");

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var response = await ownerB.GetAsync(RecipesIn(_fixture.WorkspaceA), TestContext.Current.CancellationToken);

        // B's owner names A's slug: 404, not 403, so the reply does not confirm that Workspace A exists
        // (tenancy.md).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A cursor minted in one workspace, replayed against the other by a member of it. The workspace is folded
    /// into the cursor's scope precisely so this is a refusal rather than a page — nothing in the shared paging
    /// kernel would have done that, because reference data has no workspace to bind.
    /// </summary>
    [Fact]
    public async Task A_cursor_minted_in_one_workspace_is_refused_by_the_other()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        await CreateAsync(ownerA, _fixture.WorkspaceA, "A one");
        await CreateAsync(ownerA, _fixture.WorkspaceA, "A two");

        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        await CreateAsync(ownerB, _fixture.WorkspaceB, "B one");
        await CreateAsync(ownerB, _fixture.WorkspaceB, "B two");

        var fromA = await ReadAsync(await ownerA.GetAsync(
            RecipesIn(_fixture.WorkspaceA, "?limit=1"), TestContext.Current.CancellationToken));
        var cursor = fromA.GetProperty("nextCursor").GetString()!;

        var response = await ownerB.GetAsync(
            RecipesIn(_fixture.WorkspaceB, $"?limit=1&cursor={Uri.EscapeDataString(cursor)}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.CursorInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail,
            cancellationToken: TestContext.Current.CancellationToken);

    private async Task CreateAsync(
        GatewayClient client, SeededWorkspace workspace, string title, string status = "Draft")
    {
        var response = await client.PostAsJsonAsync(
            RecipesIn(workspace),
            new { title, status },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<IReadOnlyList<string>> TitlesAsync(
        GatewayClient client, SeededWorkspace workspace, string query)
    {
        var body = await ReadAsync(await client.GetAsync(
            RecipesIn(workspace, query), TestContext.Current.CancellationToken));

        return [.. body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("title").GetString()!)];
    }

    private static IEnumerable<Guid> Ids(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid());

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
