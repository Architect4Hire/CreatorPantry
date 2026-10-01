using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>GET .../recipes/{recipeId}/test-runs/{testRunId}</c> through the real Gateway — real cookie session, real
/// gateway-signed internal token, real API.
/// </summary>
/// <remarks>
/// <para>
/// The read the history was built without, and the reason the history can stay summaries: a row says a test found
/// two problems, and this says what they were. The assertions below are mostly about the pairing — that what a row
/// counts, this names, and that the token it returns is one an edit will actually accept.
/// </para>
/// <para>
/// The two 404s are the other half: which one a caller gets is the disclosure rule, so they are asserted by code
/// and not only by status.
/// </para>
/// </remarks>
public sealed class RecipeTestRunDetailEndpointTests : IAsyncLifetime
{
    /// <summary>A Viewer in Workspace A, to prove reading a test carries the lowest bar there is.</summary>
    private const string ViewerEmail = "test-detail-viewer-a@example.com";

    private const string Password = "correct horse battery";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        await AddMemberAsync(ViewerEmail, WorkspaceRole.Viewer, _fixture.WorkspaceA.Id);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    // ---- What the route returns ----

    [Fact]
    public async Task A_test_comes_back_whole_with_the_material_a_history_row_only_counts()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        var body = await BodyOf(await client.GetAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId), TestContext.Current.CancellationToken));

        Assert.Equal(seeded.RunId, body.GetProperty("id").GetGuid());
        Assert.Equal("SucceededWithIssues", body.GetProperty("outcome").GetString());

        // The two prose fields a row deliberately withholds, and the children it reduces to counts.
        Assert.Equal("Fan oven, ran hot.", body.GetProperty("environmentNotes").GetString());
        Assert.Equal("Used a loaf tin.", body.GetProperty("equipmentNotes").GetString());

        var observation = Assert.Single(body.GetProperty("observations").EnumerateArray());
        Assert.Equal("The crumb was close.", observation.GetProperty("text").GetString());
        Assert.Equal("Texture", observation.GetProperty("kind").GetString());

        var issue = Assert.Single(body.GetProperty("issues").EnumerateArray());
        Assert.Equal("Crumb too dense", issue.GetProperty("title").GetString());
        Assert.Equal("Major", issue.GetProperty("severity").GetString());

        // The link between the two, which is what makes a problem readable as coming from a note.
        Assert.Equal(observation.GetProperty("id").GetGuid(), issue.GetProperty("testObservationId").GetGuid());
    }

    /// <summary>
    /// A null resolution <em>is</em> the unresolved state; there is no second flag that could disagree with it.
    /// </summary>
    [Fact]
    public async Task An_undecided_problem_comes_back_with_a_null_resolution()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        var body = await BodyOf(await client.GetAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId), TestContext.Current.CancellationToken));

        var issue = Assert.Single(body.GetProperty("issues").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, issue.GetProperty("resolution").ValueKind);
    }

    [Fact]
    public async Task A_decided_problem_comes_back_with_what_was_done_about_it()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        var resolved = await client.PostAsJsonAsync(
            $"{RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId)}/issues/{seeded.IssueId}/resolution",
            new { kind = "WontFix", notes = "The density is the point." },
            cancellation);
        Assert.Equal(HttpStatusCode.Created, resolved.StatusCode);

        var body = await BodyOf(await client.GetAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId), cancellation));

        var resolution = Assert.Single(body.GetProperty("issues").EnumerateArray()).GetProperty("resolution");
        Assert.Equal("WontFix", resolution.GetProperty("kind").GetString());
        Assert.Equal("The density is the point.", resolution.GetProperty("notes").GetString());
    }

    /// <summary>
    /// The point of the route rather than a detail of it: before this existed, the only way to hold a usable token
    /// was to have just written the test, so a tester could not come back to a write-up the next morning.
    /// </summary>
    [Fact]
    public async Task The_token_it_returns_is_one_an_edit_accepts()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);
        var path = RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId);

        var read = await BodyOf(await client.GetAsync(path, cancellation));

        var edit = await client.PatchAsJsonAsync(
            path,
            new
            {
                expectedConcurrencyToken = read.GetProperty("concurrencyToken").GetString(),
                summaryNotes = "Corrected the morning after.",
            },
            cancellation);

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal("Corrected the morning after.", (await BodyOf(edit)).GetProperty("summaryNotes").GetString());
    }

    /// <summary>
    /// Reading is not standing by it. The edit stamps <c>updatedAt</c> deliberately; this must not, or every
    /// reader would silently claim the last word on somebody else's write-up.
    /// </summary>
    [Fact]
    public async Task Reading_a_test_changes_nothing_about_it()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);
        var path = RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId);

        var first = await BodyOf(await client.GetAsync(path, cancellation));
        var second = await BodyOf(await client.GetAsync(path, cancellation));

        Assert.Equal(
            first.GetProperty("updatedAt").GetDateTimeOffset(),
            second.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(
            first.GetProperty("concurrencyToken").GetString(),
            second.GetProperty("concurrencyToken").GetString());
    }

    /// <summary>
    /// Sorted explicitly rather than by whatever order the query returned, so the contract does not depend on a
    /// plan and a creator's notes cannot quietly rearrange themselves.
    /// </summary>
    [Fact]
    public async Task Notes_and_problems_come_back_in_the_order_they_were_recorded()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var created = await BodyOf(await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/test-runs",
            new
            {
                sourceVersionNumber = 1,
                testedAt = "2026-09-20T18:00:00Z",
                outcome = "Failed",
                observations = new object[]
                {
                    new { kind = "Timing", text = "First note." },
                    new { kind = "Texture", text = "Second note." },
                    new { kind = "Flavour", text = "Third note." },
                },
                issues = new object[]
                {
                    new { severity = "Minor", title = "First problem" },
                    new { severity = "Blocking", title = "Second problem" },
                },
            },
            cancellation));

        var body = await BodyOf(await client.GetAsync(
            RunIn(_fixture.WorkspaceA, recipeId, created.GetProperty("testRunId").GetGuid()), cancellation));

        Assert.Equal(
            ["First note.", "Second note.", "Third note."],
            body.GetProperty("observations").EnumerateArray()
                .Select(observation => observation.GetProperty("text").GetString()));

        Assert.Equal(
            ["First problem", "Second problem"],
            body.GetProperty("issues").EnumerateArray().Select(issue => issue.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task It_carries_no_workspace_no_asset_and_no_recipe_snapshot()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        var json = await (await client.GetAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId), cancellation))
            .Content.ReadAsStringAsync(cancellation);

        Assert.DoesNotContain("workspaceId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attachments", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mediaAsset", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("snapshot", json, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Who may read it ----

    /// <summary>The lowest bar there is: reading what the workspace's testers recorded is not deciding anything.</summary>
    [Fact]
    public async Task A_viewer_may_read_a_test()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedAsync(owner, _fixture.WorkspaceA);

        using var viewer = await _fixture.SignInAsync(ViewerEmail, Password, cancellationToken: cancellation);

        var response = await viewer.GetAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId), cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Archiving withdraws a recipe from content changes, not from its own history. The create and the edit
    /// refuse with <c>recipes.archived.conflict</c>; this is the read that must not.
    /// </summary>
    [Fact]
    public async Task An_archived_recipe_still_answers_for_its_own_tests()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        var detail = await BodyOf(await client.GetAsync(
            RecipeIn(_fixture.WorkspaceA, seeded.RecipeId), cancellation));

        var archived = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, seeded.RecipeId)}/archive",
            new { expectedConcurrencyToken = detail.GetProperty("concurrencyToken").GetString() },
            cancellation);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        var response = await client.GetAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId), cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(seeded.RunId, (await BodyOf(response)).GetProperty("id").GetGuid());
    }

    // ---- The two refusals ----

    /// <summary>
    /// Once the recipe has been shown to exist and be readable, naming a missing test discloses nothing a caller
    /// could not find by listing them — which is the condition <c>recipes.testRun.not_found</c> exists under.
    /// </summary>
    [Fact]
    public async Task An_unknown_test_of_a_visible_recipe_is_refused_as_a_missing_test()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(RunIn(_fixture.WorkspaceA, recipeId, Guid.NewGuid()), cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.TestRunNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A real test, asked for under the wrong recipe of the same workspace. Answered as a missing test rather than
    /// loaded and refused, so the id cannot be used to learn which recipe it belongs to.
    /// </summary>
    [Fact]
    public async Task A_test_of_another_recipe_is_refused_as_a_missing_test()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);
        var otherRecipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(RunIn(_fixture.WorkspaceA, otherRecipeId, seeded.RunId), cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.TestRunNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// The other half of the disclosure rule: a caller who may not see the recipe is told only that. Learning
    /// whether a test id is one of its tests would be learning something about a recipe never shown to them.
    /// </summary>
    [Fact]
    public async Task A_recipe_the_caller_cannot_see_is_refused_as_a_missing_recipe()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(
            RunIn(_fixture.WorkspaceA, Guid.NewGuid(), Guid.NewGuid()), cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- Two workspaces ----

    /// <summary>
    /// Workspace B's recipe and test, asked for by Workspace A's owner under A's own slug. The ids are real; the
    /// answer must be identical to one made up, down to the problem body.
    /// </summary>
    [Fact]
    public async Task One_workspace_cannot_read_another_workspaces_test_even_with_its_ids()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var inB = await SeedAsync(ownerB, _fixture.WorkspaceB);

        // Through A's own slug, which is the only workspace this caller is a member of: B's slug would be
        // refused by the membership policy before the route ran, and would prove nothing about the data.
        var borrowed = await ownerA.GetAsync(RunIn(_fixture.WorkspaceA, inB.RecipeId, inB.RunId), cancellation);
        var invented = await ownerA.GetAsync(
            RunIn(_fixture.WorkspaceA, Guid.NewGuid(), Guid.NewGuid()), cancellation);

        // Read once: the response content is a stream, and a second BodyOf would find it already consumed.
        var borrowedBody = await BodyOf(borrowed);

        Assert.Equal(HttpStatusCode.NotFound, borrowed.StatusCode);
        Assert.Equal(WithoutTrace(await BodyOf(invented)), WithoutTrace(borrowedBody));

        // Named as well as compared. Body equality alone would still pass if both paths regressed to
        // `recipes.testRun.not_found`, which would mean A had been told B's recipe exists.
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, borrowedBody.GetProperty("code").GetString());

        // And B still reads its own, so the refusal above is isolation rather than a broken seed.
        var own = await ownerB.GetAsync(RunIn(_fixture.WorkspaceB, inB.RecipeId, inB.RunId), cancellation);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    /// <summary>
    /// B's real test id under A's own readable recipe — the one case the recipe-visibility check cannot catch,
    /// because it passes.
    /// </summary>
    /// <remarks>
    /// Told apart from <c>A_test_of_another_recipe_is_refused_as_a_missing_test</c>, which uses a run of the
    /// same workspace: there the recipe predicate does the work, and here only the query filter can. It is also
    /// the case that proves which 404 the ordering produces once the recipe *has* been shown readable.
    /// </remarks>
    [Fact]
    public async Task Another_workspaces_test_under_a_readable_recipe_is_refused_as_a_missing_test()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var inB = await SeedAsync(ownerB, _fixture.WorkspaceB);
        var ownRecipeId = await SeedRecipeAsync(ownerA, _fixture.WorkspaceA);

        var response = await ownerA.GetAsync(RunIn(_fixture.WorkspaceA, ownRecipeId, inB.RunId), cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.TestRunNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// The same id under B's own slug, asked by a caller who is not a member there. Refused by membership, and
    /// never reaching the data at all — a second gate in front of the one above.
    /// </summary>
    [Fact]
    public async Task A_non_member_asking_under_the_owning_workspaces_slug_is_refused()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var inB = await SeedAsync(ownerB, _fixture.WorkspaceB);

        var response = await ownerA.GetAsync(RunIn(_fixture.WorkspaceB, inB.RecipeId, inB.RunId), cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace) =>
        _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private static string RecipeIn(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}";

    private static string RunIn(SeededWorkspace workspace, Guid recipeId, Guid runId) =>
        $"{RecipeIn(workspace, recipeId)}/test-runs/{runId}";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>A recipe whose version 1 exists, which is all a test needs to point at.</summary>
    private static async Task<Guid> SeedRecipeAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title = "Olive oil cake", prepTimeMinutes = 20, yieldText = "makes 12 muffins" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    /// <summary>A recipe, and a test of its version 1 with one note and one issue raised from it.</summary>
    private static async Task<SeededTest> SeedAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var recipeId = await SeedRecipeAsync(client, workspace);

        var response = await client.PostAsJsonAsync(
            $"{RecipeIn(workspace, recipeId)}/test-runs",
            new
            {
                sourceVersionNumber = 1,
                testedAt = "2026-09-20T18:00:00Z",
                outcome = "SucceededWithIssues",
                rating = 3,
                summaryNotes = "Went well enough.",
                environmentNotes = "Fan oven, ran hot.",
                equipmentNotes = "Used a loaf tin.",
                observations = new object[] { new { kind = "Texture", text = "The crumb was close." } },
                issues = new object[]
                {
                    new { severity = "Major", title = "Crumb too dense", observationIndex = 0 },
                },
            },
            cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await BodyOf(response);

        return new SeededTest(
            recipeId,
            created.GetProperty("testRunId").GetGuid(),
            created.GetProperty("issueIds").EnumerateArray().Single().GetGuid());
    }

    /// <summary>
    /// A problem body with its trace id removed, so two refusals can be compared for being the same refusal rather
    /// than for having happened in the same request.
    /// </summary>
    private static string WithoutTrace(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(property => !property.Name.Contains("traceId", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Contains("correlation", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(property => property.Name, property => property.Value.ToString()));

    private async Task AddMemberAsync(string email, WorkspaceRole role, params Guid[] workspaceIds)
    {
        var userId = await _fixture.Api.CreateUserAsync(email, Password);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        foreach (var workspaceId in workspaceIds)
        {
            db.WorkspaceMemberships.Add(new WorkspaceMembership
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = userId,
                Role = role,
                Status = WorkspaceMembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private sealed record SeededTest(Guid RecipeId, Guid RunId, Guid IssueId);
}
