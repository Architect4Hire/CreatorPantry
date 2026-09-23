using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>PATCH /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}</c> through the real Gateway — real cookie
/// session, real gateway-signed internal token, real API — for what an edit does on the wire, what it
/// refuses, and what the other workspace can see.
/// </summary>
/// <remarks>
/// <para>
/// These run against SQLite, where <c>SqliteRowVersionModelCustomizer</c> fills <c>Recipe.RowVersion</c> on
/// insert and never bumps it on update. So the conflict tested here is the one reached by quoting a token
/// that was never this recipe's — the explicit comparison in Business. The other conflict, where the token
/// was right when the creator opened the recipe and wrong by the time they saved, needs a server that moves
/// the token and lives in <c>RecipeDataLayerTests</c> against real SQL Server.
/// </para>
/// <para>
/// Bodies are anonymous objects on purpose. A field named in the object is present on the wire and a field
/// omitted is absent, which is exactly the distinction under test — constructing a
/// <c>UpdateRecipeViewModel</c> here would test the serializer against itself.
/// </para>
/// </remarks>
public sealed class RecipeUpdateEndpointTests : IAsyncLifetime
{
    private const string ContributorEmail = "contributor-a@example.com";

    /// <summary>
    /// One person who contributes to both workspaces — the only way to exercise the workspace component of
    /// the idempotency scope, which is keyed on user, workspace, operation and key together.
    /// </summary>
    private const string BothWorkspacesEmail = "contributor-ab@example.com";

    private const string Password = "correct horse battery";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    private Guid _contributorMembershipId;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        // Contributor is the boundary this route turns on, and the shared fixture seeds only Owner plus
        // Editor in A and Viewer in B. Its own documentation says a feature needing another role
        // combination should add it rather than stretch the fixture every other feature depends on.
        _contributorMembershipId =
            (await AddMemberAsync(ContributorEmail, WorkspaceRole.Contributor, _fixture.WorkspaceA.Id))[_fixture.WorkspaceA.Id];

        await AddMemberAsync(
            BothWorkspacesEmail, WorkspaceRole.Contributor, _fixture.WorkspaceA.Id, _fixture.WorkspaceB.Id);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string RecipeIn(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}";

    /// <summary>Creates a recipe and reads it back, so a test starts from a real recipe and a real token.</summary>
    private async Task<(Guid RecipeId, string Token, JsonElement Detail)> SeedAsync(
        GatewayClient client, SeededWorkspace workspace, object? body = null)
    {
        var token = TestContext.Current.CancellationToken;
        var created = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            body ?? new { title = "Olive oil cake", headnote = "The one my grandmother made.", tags = new[] { "weeknight" } },
            token);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var detail = await (await client.GetAsync(created.Headers.Location!.ToString(), token))
            .Content.ReadFromJsonAsync<JsonElement>(token);

        return (detail.GetProperty("id").GetGuid(), detail.GetProperty("concurrencyToken").GetString()!, detail);
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    // ---- Submitted-field semantics ----

    [Fact]
    public async Task An_edit_changes_what_it_names_and_leaves_the_rest()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, title = "Lemon olive oil cake" },
            TestContext.Current.CancellationToken);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Lemon olive oil cake", body.GetProperty("title").GetString());

        // The field the body never mentioned. A client that omits what it does not know about must not blank
        // it — the failure this whole contract exists to prevent.
        Assert.Equal("The one my grandmother made.", body.GetProperty("headnote").GetString());
        Assert.Equal(2, body.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    [Fact]
    public async Task A_field_sent_as_null_is_cleared()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, headnote = (string?)null },
            TestContext.Current.CancellationToken);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(body.GetProperty("headnote").GetString());
        Assert.Equal("Olive oil cake", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_submitted_tag_list_replaces_the_whole_set()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var body = await BodyOf(await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, tags = new[] { "citrus" } },
            TestContext.Current.CancellationToken));

        var tag = Assert.Single(body.GetProperty("tags").EnumerateArray());
        Assert.Equal("citrus", tag.GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_empty_tag_list_removes_every_tag()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var body = await BodyOf(await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, tags = Array.Empty<string>() },
            TestContext.Current.CancellationToken));

        Assert.Empty(body.GetProperty("tags").EnumerateArray());
    }

    [Fact]
    public async Task A_recipe_can_be_archived()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var body = await BodyOf(await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, status = "Archived" },
            TestContext.Current.CancellationToken));

        // Refused on a create, accepted here: archiving is a state a recipe is moved to.
        Assert.Equal("Archived", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_writes_no_version()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, created) = await SeedAsync(client, _fixture.WorkspaceA);

        var body = await BodyOf(await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, title = "Olive oil cake", reason = "No change, honestly." },
            TestContext.Current.CancellationToken));

        // Still version 1, and the recipe's own timestamp untouched: a version recording no change is noise
        // in a history the creator reads, and stamping UpdatedAt would invalidate every token their
        // collaborators hold.
        Assert.Equal(1, body.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
        Assert.Equal(created.GetProperty("updatedAt").GetString(), body.GetProperty("updatedAt").GetString());
        Assert.Equal(token, body.GetProperty("concurrencyToken").GetString());
    }

    [Fact]
    public async Task The_response_is_the_whole_recipe_an_editor_can_rebind_from()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var body = await BodyOf(await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, title = "Lemon cake" },
            TestContext.Current.CancellationToken));

        // The same shape a read returns, so no second request is needed — and above all the token the next
        // edit must quote.
        Assert.False(string.IsNullOrEmpty(body.GetProperty("concurrencyToken").GetString()));
        Assert.Equal(recipeId, body.GetProperty("id").GetGuid());
        Assert.True(body.TryGetProperty("ingredientGroups", out _));
    }

    // ---- Conflict ----

    [Fact]
    public async Task An_edit_quoting_a_token_this_recipe_never_had_is_a_conflict()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = "CAcGBQQDAgE=", title = "Lemon cake" },
            TestContext.Current.CancellationToken);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("recipes.recipe.conflict", problem.GetProperty("code").GetString());

        // Nothing landed, and the creator's own attempt is still theirs to retry: a re-read shows the recipe
        // unchanged and hands back a usable token.
        var reread = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken));
        Assert.Equal("Olive oil cake", reread.GetProperty("title").GetString());
        Assert.Equal(1, reread.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    [Fact]
    public async Task A_token_that_could_never_have_been_issued_is_a_bad_request_not_a_conflict()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = "not-a-token", title = "Lemon cake" },
            TestContext.Current.CancellationToken);
        var problem = await BodyOf(response);

        // A client bug reported as a conflict would send a creator looking for a collaborator who was
        // never there.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("recipes.recipe.invalid_request", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    // ---- Validation ----

    [Fact]
    public async Task An_edit_with_no_token_is_refused()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, _, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId), new { title = "Lemon cake" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_title_cannot_be_cleared()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, title = (string?)null },
            TestContext.Current.CancellationToken);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "A recipe must keep a title.",
            problem.GetProperty("errors").GetProperty("title")[0].GetString());
    }

    [Fact]
    public async Task An_invariant_is_judged_against_the_recipe_the_edit_would_produce()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(
            client, _fixture.WorkspaceA, new { title = "Slow cake", cookTimeMinutes = 90 });

        var response = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, totalTimeMinutes = 30 },
            TestContext.Current.CancellationToken);
        var problem = await BodyOf(response);

        // The body alone is unobjectionable. It is wrong only because of a cook time it never mentioned.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("totalTimeMinutes", out _));
    }

    // ---- Roles ----

    [Fact]
    public async Task A_contributor_may_edit()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(owner, _fixture.WorkspaceA);

        using var contributor = await _fixture.SignInAsync(ContributorEmail, cancellationToken: TestContext.Current.CancellationToken);
        var response = await contributor.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, title = "Someone else's edit" },
            TestContext.Current.CancellationToken);

        // Editing a recipe another member started is a collaboration, not an escalation.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_not_edit()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(owner, _fixture.WorkspaceB);

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: TestContext.Current.CancellationToken);
        var response = await viewer.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceB, recipeId),
            new { expectedConcurrencyToken = token, title = "Not allowed" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Workspace isolation ----

    [Fact]
    public async Task A_recipe_of_the_other_workspace_cannot_be_edited_and_is_not_disclosed()
    {
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(ownerA, _fixture.WorkspaceA);

        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var response = await ownerB.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceB, recipeId),
            new { expectedConcurrencyToken = token, title = "Stolen cake" },
            TestContext.Current.CancellationToken);
        var problem = await BodyOf(response);

        // 404 and not 403: a workspace must not be able to learn that another's recipe exists by being
        // refused it, and the answer is identical to an id that was never real.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("recipes.recipe.not_found", problem.GetProperty("code").GetString());

        var untouched = await BodyOf(await ownerA.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken));
        Assert.Equal("Olive oil cake", untouched.GetProperty("title").GetString());
        Assert.Equal(1, untouched.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    [Fact]
    public async Task An_unknown_recipe_answers_exactly_as_another_workspaces_does()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, Guid.NewGuid()),
            new { expectedConcurrencyToken = "AQIDBAUGBwg=", title = "Nothing here" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("recipes.recipe.not_found", (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Two_workspaces_can_edit_identically_named_recipes_without_touching_each_other()
    {
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        var a = await SeedAsync(ownerA, _fixture.WorkspaceA);
        var b = await SeedAsync(ownerB, _fixture.WorkspaceB);

        await ownerA.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, a.RecipeId),
            new { expectedConcurrencyToken = a.Token, title = "A's edit", tags = new[] { "a-only" } },
            TestContext.Current.CancellationToken);

        var untouched = await BodyOf(await ownerB.GetAsync(RecipeIn(_fixture.WorkspaceB, b.RecipeId), TestContext.Current.CancellationToken));

        Assert.Equal("Olive oil cake", untouched.GetProperty("title").GetString());
        Assert.Equal("weeknight", Assert.Single(untouched.GetProperty("tags").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(1, untouched.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    [Fact]
    public async Task A_new_tag_lands_in_the_editing_workspace_only()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, tags = new[] { "sourdough" } },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, await CountTagsAsync(_fixture.WorkspaceA.Id, "sourdough"));
        Assert.Equal(0, await CountTagsAsync(_fixture.WorkspaceB.Id, "sourdough"));
    }

    [Fact]
    public async Task A_tag_the_other_workspace_already_has_is_created_here_rather_than_borrowed()
    {
        var cancellation = TestContext.Current.CancellationToken;

        // B gets "weeknight" first; A's recipe is seeded with no tags at all, so the lookup A is about to do
        // can only match B's row — which is precisely the leak worth catching. The sibling test above uses a
        // name neither workspace has, so it cannot see this.
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);
        await SeedAsync(ownerB, _fixture.WorkspaceB);

        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(
            ownerA, _fixture.WorkspaceA, new { title = "Untagged cake", tags = Array.Empty<string>() });

        Assert.Equal(0, await CountTagsAsync(_fixture.WorkspaceA.Id, "weeknight"));
        Assert.Equal(1, await CountTagsAsync(_fixture.WorkspaceB.Id, "weeknight"));

        var body = await BodyOf(await ownerA.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, tags = new[] { "weeknight" } },
            cancellation));

        // A row of A's own, and B's vocabulary untouched. Two workspaces holding a "weeknight" each is a
        // supported state — the unique key is (WorkspaceId, NormalizedName) — and borrowing would link one
        // creator's recipe to another creator's vocabulary row.
        Assert.Equal(1, await CountTagsAsync(_fixture.WorkspaceA.Id, "weeknight"));
        Assert.Equal(1, await CountTagsAsync(_fixture.WorkspaceB.Id, "weeknight"));

        var linkedId = Assert.Single(body.GetProperty("tags").EnumerateArray()).GetProperty("workspaceTagId").GetGuid();
        Assert.Equal(await TagIdAsync(_fixture.WorkspaceA.Id, "weeknight"), linkedId);
    }

    // ---- Idempotency ----

    [Fact]
    public async Task A_replayed_edit_returns_the_original_response_and_writes_no_second_version()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);
        var edit = new { expectedConcurrencyToken = token, title = "Lemon cake" };

        var first = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId), edit, "edit-key-1", TestContext.Current.CancellationToken);
        var second = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId), edit, "edit-key-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(first.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal("true", second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());

        // Byte for byte the original answer. A replay is stored as serialized JSON and read back, so this is
        // also what proves the response shape survives that round trip — every required property, every
        // nested collection.
        Assert.Equal(
            (await BodyOf(first)).GetRawText(),
            (await BodyOf(second)).GetRawText());

        // Without the key the retry would be answered as a conflict, and the client could not tell "someone
        // else edited this" from "my own edit already landed".
        var reread = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken));
        Assert.Equal(2, reread.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    [Fact]
    public async Task The_same_key_for_a_different_edit_is_refused()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var (recipeId, token, _) = await SeedAsync(client, _fixture.WorkspaceA);

        await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, title = "Lemon cake" },
            "edit-key-1",
            TestContext.Current.CancellationToken);

        var second = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, title = "Orange cake" },
            "edit-key-1",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, (await BodyOf(second)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_idempotency_key_reaches_only_the_workspace_it_was_used_in()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(BothWorkspacesEmail, cancellationToken: cancellation);

        var inA = await SeedAsync(client, _fixture.WorkspaceA);
        var inB = await SeedAsync(client, _fixture.WorkspaceB);

        await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, inA.RecipeId),
            new { expectedConcurrencyToken = inA.Token, title = "A's edit" },
            "shared-key",
            cancellation);

        var second = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceB, inB.RecipeId),
            new { expectedConcurrencyToken = inB.Token, title = "B's edit" },
            "shared-key",
            cancellation);

        // The same person, the same key, the same operation, two workspaces. If the scope dropped its
        // workspace component this would collide with A's record and be refused as key reuse — or worse,
        // replay A's recipe into B's response.
        var body = await BodyOf(second);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(second.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal("B's edit", body.GetProperty("title").GetString());
        Assert.Equal(inB.RecipeId, body.GetProperty("id").GetGuid());
    }

    // ---- Attribution ----

    [Fact]
    public async Task The_version_records_who_made_the_edit_not_who_made_the_recipe()
    {
        var cancellation = TestContext.Current.CancellationToken;

        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedAsync(owner, _fixture.WorkspaceA);

        using var contributor = await _fixture.SignInAsync(ContributorEmail, cancellationToken: cancellation);
        await contributor.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, title = "Someone else's edit" },
            cancellation);

        // Read from the database, because the wire deliberately withholds authorship — and attribution that
        // is wrong in an immutable archive cannot be corrected later.
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var versions = await db.RecipeVersions
            .IgnoreQueryFilters()
            .Where(version => version.RecipeId == recipeId)
            .OrderBy(version => version.VersionNumber)
            .Select(version => new { version.VersionNumber, version.CreatedByMembershipId })
            .ToListAsync(cancellation);

        var recipe = await db.Recipes
            .IgnoreQueryFilters()
            .Where(candidate => candidate.Id == recipeId)
            .Select(candidate => new { candidate.CreatedByMembershipId, candidate.UpdatedByMembershipId })
            .SingleAsync(cancellation);

        Assert.Equal(2, versions.Count);
        Assert.Equal(_contributorMembershipId, versions[1].CreatedByMembershipId);
        Assert.Equal(_contributorMembershipId, recipe.UpdatedByMembershipId);

        // Authorship of the recipe itself is not rewritten by someone else's edit, and version 1 still names
        // the creator who wrote it.
        Assert.Equal(versions[0].CreatedByMembershipId, recipe.CreatedByMembershipId);
        Assert.NotEqual(_contributorMembershipId, recipe.CreatedByMembershipId);
    }

    private async Task<Guid> TagIdAsync(Guid workspaceId, string normalizedName)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.WorkspaceTags
            .IgnoreQueryFilters()
            .Where(tag => tag.WorkspaceId == workspaceId && tag.NormalizedName == normalizedName)
            .Select(tag => tag.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountTagsAsync(Guid workspaceId, string normalizedName)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // Counted by workspace id directly. A filtered query returning nothing would prove the filter works,
        // not which workspace the row actually landed in — and that is the question isolation turns on.
        return await db.WorkspaceTags
            .IgnoreQueryFilters()
            .CountAsync(
                tag => tag.WorkspaceId == workspaceId && tag.NormalizedName == normalizedName,
                TestContext.Current.CancellationToken);
    }

    /// <summary>Creates one user and gives them the same role in each named workspace.</summary>
    /// <returns>The membership id per workspace — which is the identity a version records, not the user id.</returns>
    private async Task<IReadOnlyDictionary<Guid, Guid>> AddMemberAsync(
        string email, WorkspaceRole role, params Guid[] workspaceIds)
    {
        var userId = await _fixture.Api.CreateUserAsync(email, Password);
        var memberships = workspaceIds.ToDictionary(workspaceId => workspaceId, _ => Guid.NewGuid());

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        foreach (var (workspaceId, membershipId) in memberships)
        {
            db.WorkspaceMemberships.Add(new WorkspaceMembership
            {
                Id = membershipId,
                WorkspaceId = workspaceId,
                UserId = userId,
                Role = role,
                Status = WorkspaceMembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();

        return memberships;
    }
}
