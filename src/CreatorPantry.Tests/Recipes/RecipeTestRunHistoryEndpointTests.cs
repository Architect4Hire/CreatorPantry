using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>GET /api/v1/workspaces/{slug}/recipes/{recipeId}/test-runs</c> through the real Gateway — real cookie
/// session, real gateway-signed internal token, real API — for the published page shape, what a row never carries,
/// how the filters and the summary agree, and what one workspace can learn about the other's testing.
/// </summary>
public sealed class RecipeTestRunHistoryEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string TestRunsIn(SeededWorkspace workspace, Guid recipeId, string query = "") =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/test-runs{query}";

    // ---- The published page ----

    [Fact]
    public async Task A_recipe_nobody_has_tested_has_an_empty_history_rather_than_a_missing_one()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);

        // Zeros, not a null summary: null means "you did not ask", and an untested recipe is a real answer.
        var summary = body.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("unresolvedIssueCount").GetInt32());
    }

    /// <summary>
    /// The restriction this route exists under, asserted on the raw JSON as well as on the property set: a history
    /// carries counts where the tests carry material, so a snapshot or an asset arriving nested inside some future
    /// field would still be caught.
    /// </summary>
    [Fact]
    public async Task A_row_carries_no_attachment_no_snapshot_and_no_child_text()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        await RecordAsync(client, _fixture.WorkspaceA, recipeId, new
        {
            sourceVersionNumber = 1,
            testedAt = "2026-09-20T18:00:00Z",
            outcome = "SucceededWithIssues",
            rating = 4,
            summaryNotes = "Good but dense.",
            environmentNotes = "Fan oven, ran hot.",
            equipmentNotes = "Used a loaf tin, not a cake tin.",
            observations = new[] { new { kind = "Texture", text = "Crumb tightened near the base." } },
            issues = new[] { new { severity = "Major", title = "Collapsed in the tin", observationIndex = 0 } },
        });

        var response = await client.GetAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // `attachmentCount` is a number and stays; what must be absent is anything that could carry an asset. A
        // count of photographs is not a photograph, and it is the most this route can say about assets it cannot
        // authorize until the media library arrives.
        Assert.DoesNotContain("\"attachments\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mediaAsset", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("snapshot", json, StringComparison.OrdinalIgnoreCase);

        // Phrases that exist only inside a child row or a withheld column, so their absence says the text was not
        // read rather than only that no field is named after it.
        Assert.DoesNotContain("Crumb tightened", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Collapsed in the tin", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fan oven", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("loaf tin", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("workspaceId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rowVersion", json, StringComparison.OrdinalIgnoreCase);

        // Pinned as the exact property set, so a field added later is a decision someone makes rather than a leak
        // nobody noticed.
        var row = (await ReadAsync(response)).GetProperty("items").EnumerateArray().Single();

        Assert.Equal(
            (string[])
            [
                "id", "recipeVersionId", "versionNumber", "testedAt", "testedByMembershipId", "testedByName",
                "outcome", "rating", "summaryNotes", "actualYieldText", "actualYieldQuantity", "actualYieldUnitId",
                "actualPrepTimeMinutes", "actualCookTimeMinutes", "actualRestTimeMinutes", "actualTotalTimeMinutes",
                "observationCount", "issueCount", "unresolvedIssueCount", "attachmentCount", "createdAt",
                "updatedAt", "concurrencyToken",
            ],
            row.EnumerateObject().Select(property => property.Name));

        // The counts stand in for the material that is absent.
        Assert.Equal(1, row.GetProperty("observationCount").GetInt32());
        Assert.Equal(1, row.GetProperty("issueCount").GetInt32());
        Assert.Equal(1, row.GetProperty("unresolvedIssueCount").GetInt32());
        Assert.Equal(0, row.GetProperty("attachmentCount").GetInt32());

        // Enums as the names the schema publishes, not integers a client would have to map privately.
        Assert.Equal("SucceededWithIssues", row.GetProperty("outcome").GetString());

        // The prose field a row does keep, and the version it was cooked against by number.
        Assert.Equal("Good but dense.", row.GetProperty("summaryNotes").GetString());
        Assert.Equal(1, row.GetProperty("versionNumber").GetInt32());
    }

    /// <summary>
    /// The tester is named as well as identified. The name is what a creator reads; the id is what
    /// <c>?testedBy=</c> takes, and a client can only send one it was given.
    /// </summary>
    [Fact]
    public async Task A_row_names_the_tester_and_publishes_the_id_the_filter_accepts()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        await RecordAsync(client, _fixture.WorkspaceA, recipeId);

        var row = await SingleRowAsync(client, _fixture.WorkspaceA, recipeId);
        var testerId = row.GetProperty("testedByMembershipId").GetGuid();

        Assert.NotEqual(Guid.Empty, testerId);
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("testedByName").ValueKind);

        var filtered = await ReadAsync(await client.GetAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId, $"?testedBy={testerId:D}"),
            TestContext.Current.CancellationToken));

        Assert.Single(filtered.GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// No route reads one test on its own yet, so the summary is where a token for an edit comes from. Proved by
    /// spending it: the edit succeeds, which it would not on a token the run had moved past.
    /// </summary>
    [Fact]
    public async Task The_token_a_row_carries_is_one_an_edit_accepts()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var testRunId = await RecordAsync(client, _fixture.WorkspaceA, recipeId);

        var row = await SingleRowAsync(client, _fixture.WorkspaceA, recipeId);

        var edit = await client.PatchAsJsonAsync(
            $"{TestRunsIn(_fixture.WorkspaceA, recipeId)}/{testRunId}",
            new
            {
                expectedConcurrencyToken = row.GetProperty("concurrencyToken").GetString(),
                rating = 5,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
    }

    [Fact]
    public async Task Every_member_including_a_viewer_may_read_a_recipes_test_history()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB);
        var recipeId = await SeedRecipeAsync(owner, _fixture.WorkspaceB);
        await RecordAsync(owner, _fixture.WorkspaceB, recipeId);

        // Workspace B's second member is a Viewer — the lowest role there is, and the one this must admit.
        using var viewer = await SignInAsync(_fixture.WorkspaceB, asOwner: false);

        var response = await viewer.GetAsync(
            TestRunsIn(_fixture.WorkspaceB, recipeId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await ReadAsync(response)).GetProperty("items").EnumerateArray());
    }

    // ---- Ordering, filters and the summary ----

    [Fact]
    public async Task The_list_is_most_recently_cooked_first()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        await RecordAsync(client, _fixture.WorkspaceA, recipeId, TestedOn("2026-09-18T10:00:00Z", "oldest"));
        await RecordAsync(client, _fixture.WorkspaceA, recipeId, TestedOn("2026-09-22T10:00:00Z", "newest"));
        await RecordAsync(client, _fixture.WorkspaceA, recipeId, TestedOn("2026-09-20T10:00:00Z", "middle"));

        var rows = await RowsAsync(client, _fixture.WorkspaceA, recipeId);

        Assert.Equal(["newest", "middle", "oldest"], rows.Select(row => row.GetProperty("summaryNotes").GetString()));
    }

    [Fact]
    public async Task Combined_filters_narrow_the_list_and_the_summary_together()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        await RecordAsync(client, _fixture.WorkspaceA, recipeId, new
        {
            sourceVersionNumber = 1,
            testedAt = "2026-09-20T10:00:00Z",
            outcome = "Failed",
            summaryNotes = "wanted",
            issues = new[] { new { severity = "Blocking", title = "Sank" } },
        });
        await RecordAsync(client, _fixture.WorkspaceA, recipeId, new
        {
            sourceVersionNumber = 1,
            testedAt = "2026-09-20T11:00:00Z",
            outcome = "Succeeded",
            summaryNotes = "wrong outcome",
        });
        await RecordAsync(client, _fixture.WorkspaceA, recipeId, new
        {
            sourceVersionNumber = 1,
            testedAt = "2026-09-01T10:00:00Z",
            outcome = "Failed",
            summaryNotes = "outside the range",
        });

        var body = await ReadAsync(await client.GetAsync(
            TestRunsIn(
                _fixture.WorkspaceA,
                recipeId,
                "?version=1&outcome=Failed&issues=HasUnresolved"
                    + "&testedFrom=2026-09-15T00:00:00Z&testedBefore=2026-09-25T00:00:00Z"),
            TestContext.Current.CancellationToken));

        var row = body.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("wanted", row.GetProperty("summaryNotes").GetString());

        // The summary describes the filtered set, so it agrees with the row above rather than with the whole
        // history. A summary that ignored the filters would say three here.
        var summary = body.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("byOutcome").GetProperty("Failed").GetInt32());
        Assert.Equal(1, summary.GetProperty("runsWithUnresolvedIssues").GetInt32());
        Assert.Equal(1, summary.GetProperty("unresolvedIssueCount").GetInt32());
    }

    /// <summary>
    /// Every verdict is named even where nothing holds it, so a screen showing four figures never has to tell "no
    /// failures" from "failures not counted".
    /// </summary>
    [Fact]
    public async Task The_summary_names_every_outcome_including_the_ones_at_zero()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        await RecordAsync(client, _fixture.WorkspaceA, recipeId);

        var byOutcome = (await ReadAsync(await client.GetAsync(
                TestRunsIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken)))
            .GetProperty("summary")
            .GetProperty("byOutcome");

        Assert.Equal(
            Enum.GetNames<TestRunOutcome>().Order(),
            byOutcome.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task The_summary_can_be_turned_off()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        await RecordAsync(client, _fixture.WorkspaceA, recipeId);

        var body = await ReadAsync(await client.GetAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId, "?includeSummary=false"),
            TestContext.Current.CancellationToken));

        Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("summary").ValueKind);
    }

    [Fact]
    public async Task A_filter_that_cannot_be_read_is_refused_naming_the_parameter()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId, "?outcome=Burnt"), TestContext.Current.CancellationToken);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.TestRunInvalidRequest, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("outcome", out _));
    }

    // ---- Paging ----

    [Fact]
    public async Task Following_the_cursor_yields_every_test_once()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        for (var day = 1; day <= 5; day++)
        {
            await RecordAsync(
                client, _fixture.WorkspaceA, recipeId, TestedOn($"2026-09-{day:00}T10:00:00Z", $"test {day}"));
        }

        var seen = new List<Guid>();
        string? cursor = null;

        do
        {
            var query = cursor is null ? "?limit=2" : $"?limit=2&cursor={Uri.EscapeDataString(cursor)}";
            var body = await ReadAsync(await client.GetAsync(
                TestRunsIn(_fixture.WorkspaceA, recipeId, query), TestContext.Current.CancellationToken));

            seen.AddRange(body.GetProperty("items").EnumerateArray()
                .Select(row => row.GetProperty("id").GetGuid()));

            cursor = body.GetProperty("nextCursor").ValueKind is JsonValueKind.Null
                ? null
                : body.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    /// <summary>
    /// A cursor is a position in one ordered set, and the filters decide which set. Following one into a different
    /// filter is refused rather than resuming from a row that may not be in the new set at all.
    /// </summary>
    [Fact]
    public async Task A_cursor_replayed_under_different_filters_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        for (var day = 1; day <= 3; day++)
        {
            await RecordAsync(client, _fixture.WorkspaceA, recipeId, TestedOn($"2026-09-{day:00}T10:00:00Z"));
        }

        var first = await ReadAsync(await client.GetAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId, "?limit=1"), TestContext.Current.CancellationToken));
        var cursor = first.GetProperty("nextCursor").GetString()!;

        var response = await client.GetAsync(
            TestRunsIn(
                _fixture.WorkspaceA, recipeId, $"?limit=1&outcome=Failed&cursor={Uri.EscapeDataString(cursor)}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_cursor_that_is_not_one_is_refused_rather_than_ignored()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId, "?cursor=not-a-cursor"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, (await ReadAsync(response)).GetProperty("code").GetString());
    }

    // ---- Workspace isolation ----

    /// <summary>
    /// An unknown recipe and another workspace's recipe answer identically, down to the problem body: the seam
    /// cannot tell them apart and must not be able to, because a different answer would confirm that the second one
    /// exists.
    /// </summary>
    [Fact]
    public async Task An_unknown_recipe_and_another_workspaces_recipe_answer_the_same_way()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var inB = await SeedRecipeAsync(ownerB, _fixture.WorkspaceB);
        await RecordAsync(ownerB, _fixture.WorkspaceB, inB);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);

        var unknown = await ownerA.GetAsync(
            TestRunsIn(_fixture.WorkspaceA, Guid.NewGuid()), TestContext.Current.CancellationToken);
        var othersRecipe = await ownerA.GetAsync(
            TestRunsIn(_fixture.WorkspaceA, inB), TestContext.Current.CancellationToken);

        var unknownBody = await ReadAsync(unknown);
        var othersBody = await ReadAsync(othersRecipe);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, othersRecipe.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, unknownBody.GetProperty("code").GetString());
        Assert.Equal(WithoutTrace(unknownBody), WithoutTrace(othersBody));
    }

    /// <summary>
    /// Each workspace's history holds only its own tests, and the summaries agree with the lists. Both recipes carry
    /// the same title and both tests the same notes, so isolation cannot pass by the two being distinguishable.
    /// </summary>
    [Fact]
    public async Task Each_workspace_reads_only_its_own_testing()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var inA = await SeedRecipeAsync(ownerA, _fixture.WorkspaceA);
        var inB = await SeedRecipeAsync(ownerB, _fixture.WorkspaceB);

        await RecordAsync(ownerA, _fixture.WorkspaceA, inA, TestedOn("2026-09-20T10:00:00Z", "shared notes"));
        await RecordAsync(ownerB, _fixture.WorkspaceB, inB, TestedOn("2026-09-20T10:00:00Z", "shared notes"));
        await RecordAsync(ownerB, _fixture.WorkspaceB, inB, TestedOn("2026-09-21T10:00:00Z", "shared notes"));

        var historyA = await ReadAsync(await ownerA.GetAsync(
            TestRunsIn(_fixture.WorkspaceA, inA), TestContext.Current.CancellationToken));
        var historyB = await ReadAsync(await ownerB.GetAsync(
            TestRunsIn(_fixture.WorkspaceB, inB), TestContext.Current.CancellationToken));

        var idsA = historyA.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetGuid());
        var idsB = historyB.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("id").GetGuid());

        Assert.Single(idsA);
        Assert.Equal(2, idsB.Count());
        Assert.Empty(idsA.Intersect(idsB));

        Assert.Equal(1, historyA.GetProperty("summary").GetProperty("totalCount").GetInt32());
        Assert.Equal(2, historyB.GetProperty("summary").GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// A cursor issued for one workspace's history, replayed by the other workspace's owner against their own
    /// recipe, is refused rather than paging anything. The workspace and the recipe are both in the fingerprint, so
    /// there is no version of this that quietly returns rows.
    /// </summary>
    [Fact]
    public async Task A_cursor_from_one_workspace_cannot_page_the_others_history()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var inA = await SeedRecipeAsync(ownerA, _fixture.WorkspaceA);
        var inB = await SeedRecipeAsync(ownerB, _fixture.WorkspaceB);

        for (var day = 1; day <= 2; day++)
        {
            await RecordAsync(ownerA, _fixture.WorkspaceA, inA, TestedOn($"2026-09-{day:00}T10:00:00Z"));
            await RecordAsync(ownerB, _fixture.WorkspaceB, inB, TestedOn($"2026-09-{day:00}T10:00:00Z"));
        }

        var fromA = await ReadAsync(await ownerA.GetAsync(
            TestRunsIn(_fixture.WorkspaceA, inA, "?limit=1"), TestContext.Current.CancellationToken));
        var cursor = fromA.GetProperty("nextCursor").GetString()!;

        var replayed = await ownerB.GetAsync(
            TestRunsIn(_fixture.WorkspaceB, inB, $"?limit=1&cursor={Uri.EscapeDataString(cursor)}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Equal(RecipeErrorCodes.CursorInvalidRequest, (await ReadAsync(replayed)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A tester id learned from one workspace's history is not a way into the other's. It filters nothing there,
    /// because the tests it would match are not visible — not because the filter rejected it.
    /// </summary>
    [Fact]
    public async Task A_tester_id_from_one_workspace_finds_nothing_in_the_other()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var inA = await SeedRecipeAsync(ownerA, _fixture.WorkspaceA);
        var inB = await SeedRecipeAsync(ownerB, _fixture.WorkspaceB);

        await RecordAsync(ownerA, _fixture.WorkspaceA, inA);
        await RecordAsync(ownerB, _fixture.WorkspaceB, inB);

        var testerInA = (await SingleRowAsync(ownerA, _fixture.WorkspaceA, inA))
            .GetProperty("testedByMembershipId").GetGuid();

        var body = await ReadAsync(await ownerB.GetAsync(
            TestRunsIn(_fixture.WorkspaceB, inB, $"?testedBy={testerInA:D}"),
            TestContext.Current.CancellationToken));

        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(0, body.GetProperty("summary").GetProperty("totalCount").GetInt32());
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail,
            cancellationToken: TestContext.Current.CancellationToken);

    private static object TestedOn(string testedAt, string? summaryNotes = null) => new
    {
        sourceVersionNumber = 1,
        testedAt,
        outcome = "Succeeded",
        summaryNotes,
    };

    /// <summary>A recipe whose version 1 exists, which is all a test needs to point at.</summary>
    private static async Task<Guid> SeedRecipeAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title = "Olive oil cake", prepTimeMinutes = 20, yieldText = "makes 12 muffins" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await ReadAsync(response)).GetProperty("recipeId").GetGuid();
    }

    private static async Task<Guid> RecordAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId, object? body = null)
    {
        var response = await client.PostAsJsonAsync(
            TestRunsIn(workspace, recipeId),
            body ?? TestedOn("2026-09-20T18:00:00Z"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await ReadAsync(response)).GetProperty("testRunId").GetGuid();
    }

    private static async Task<List<JsonElement>> RowsAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId, string query = "")
    {
        var body = await ReadAsync(await client.GetAsync(
            TestRunsIn(workspace, recipeId, query), TestContext.Current.CancellationToken));

        return [.. body.GetProperty("items").EnumerateArray()];
    }

    private static async Task<JsonElement> SingleRowAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId) =>
        Assert.Single(await RowsAsync(client, workspace, recipeId));

    /// <summary>
    /// A problem body with its trace id removed, so two refusals can be compared for being the same refusal rather
    /// than for having happened in the same request.
    /// </summary>
    private static string WithoutTrace(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(property => !property.Name.Contains("traceId", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Contains("correlation", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(property => property.Name, property => property.Value.ToString()));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
