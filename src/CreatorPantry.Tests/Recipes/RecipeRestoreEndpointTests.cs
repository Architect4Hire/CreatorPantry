using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>POST /api/v1/workspaces/{slug}/recipes/{recipeId}/versions/{versionNumber}/restore</c> through the real
/// Gateway — real cookie session, real gateway-signed internal token, real API — for what a restore does on
/// the wire, who may ask for it, what it refuses, and what the other workspace can see.
/// </summary>
/// <remarks>
/// <para>
/// These run against SQLite, where <c>SqliteModelCustomizer</c> fills <c>Recipe.RowVersion</c> on insert and
/// never bumps it on update. Two consequences worth knowing while reading: the conflict exercised here is the
/// one reached by quoting a token that was never this recipe's, and a repeat restore is answered as the no-op
/// it is rather than as a conflict. The server-moved-token race needs a database that moves the token and
/// lives in <c>RecipeDataLayerTests</c>.
/// </para>
/// <para>
/// Bodies are anonymous objects on purpose, so that what is on the wire is what the test wrote — building a
/// <see cref="RestoreRecipeVersionViewModel"/> here would test the serializer against itself.
/// </para>
/// </remarks>
public sealed class RecipeRestoreEndpointTests : IAsyncLifetime
{
    /// <summary>
    /// Contributor is the role immediately below the bar, and the shared fixture seeds only Owner plus Editor
    /// in A and Viewer in B — so the one role that proves restoring is not merely editing has to be added.
    /// </summary>
    private const string ContributorEmail = "restore-contributor-a@example.com";

    private const string Password = "correct horse battery";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        await AddMemberAsync(ContributorEmail, WorkspaceRole.Contributor, _fixture.WorkspaceA.Id);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string RecipeIn(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}";

    private static string RestoreIn(SeededWorkspace workspace, Guid recipeId, int versionNumber) =>
        $"{RecipeIn(workspace, recipeId)}/versions/{versionNumber}/restore";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>
    /// A recipe with a history worth restoring: created as version 1, then edited into version 2. Returns
    /// the recipe as it stands after the edit, so a restore has a current token to quote and a version 1 to
    /// go back to.
    /// </summary>
    private async Task<(Guid RecipeId, string Token, JsonElement Detail)> SeedWithHistoryAsync(
        GatewayClient client, SeededWorkspace workspace)
    {
        var cancellation = TestContext.Current.CancellationToken;

        var created = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title = "Olive oil cake", headnote = "The one my grandmother made.", tags = new[] { "weeknight" } },
            cancellation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var first = await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), cancellation));
        var recipeId = first.GetProperty("id").GetGuid();

        var edited = await client.PatchAsJsonAsync(
            RecipeIn(workspace, recipeId),
            new
            {
                expectedConcurrencyToken = first.GetProperty("concurrencyToken").GetString(),
                title = "Lemon olive oil cake",
                headnote = (string?)null,
                tags = new[] { "citrus" },
            },
            cancellation);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var detail = await BodyOf(edited);

        return (recipeId, detail.GetProperty("concurrencyToken").GetString()!, detail);
    }

    // ---- What a restore does ----

    [Fact]
    public async Task A_restore_puts_the_recipe_back_and_writes_a_new_current_version()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, edited) = await SeedWithHistoryAsync(client, _fixture.WorkspaceA);

        Assert.Equal(2, edited.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());

        var response = await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1),
            new { expectedConcurrencyToken = token, reason = "Tuesday's edit broke it." },
            cancellation);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The content of version 1 is back — including the field the edit cleared and the tags it replaced.
        Assert.Equal("Olive oil cake", body.GetProperty("title").GetString());
        Assert.Equal("The one my grandmother made.", body.GetProperty("headnote").GetString());
        Assert.Equal("weeknight", Assert.Single(body.GetProperty("tags").EnumerateArray()).GetProperty("name").GetString());

        // As a new version, never by making the old row current again.
        var current = body.GetProperty("currentVersion");
        Assert.Equal(3, current.GetProperty("versionNumber").GetInt32());
        Assert.Equal("Restore", current.GetProperty("source").GetString());
        Assert.Equal("Tuesday's edit broke it.", current.GetProperty("reason").GetString());
    }

    /// <summary>
    /// The response is the whole recipe in the shape a read returns, so an editor can rebind from it rather
    /// than making a second request — above all for the token the next write must quote.
    /// </summary>
    [Fact]
    public async Task The_response_is_the_whole_recipe_an_editor_can_rebind_from()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedWithHistoryAsync(client, _fixture.WorkspaceA);

        var body = await BodyOf(await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1), new { expectedConcurrencyToken = token }, cancellation));

        Assert.Equal(recipeId, body.GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("concurrencyToken").GetString()));
        Assert.True(body.TryGetProperty("ingredientGroups", out _));
        Assert.True(body.TryGetProperty("instructionGroups", out _));
    }

    /// <summary>
    /// History is appended to, not rewritten: the restored version is still there, still numbered 1, and the
    /// new version records both what it replaced and where its content came from.
    /// </summary>
    [Fact]
    public async Task The_history_grows_and_records_both_ancestors()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedWithHistoryAsync(client, _fixture.WorkspaceA);

        await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1), new { expectedConcurrencyToken = token }, cancellation);

        var history = await BodyOf(await client.GetAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/versions", cancellation));
        var versions = history.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal([3, 2, 1], versions.Select(version => version.GetProperty("versionNumber").GetInt32()));

        var restored = versions[0];
        Assert.Equal("Restore", restored.GetProperty("source").GetString());

        // What it replaced: version 2. Where the content came from: version 1. Two different rows, which is
        // the whole reason the lineage carries two edges.
        Assert.Equal(versions[1].GetProperty("id").GetGuid(), restored.GetProperty("parentVersionId").GetGuid());
        Assert.Equal(versions[2].GetProperty("id").GetGuid(), restored.GetProperty("restoredFromVersionId").GetGuid());

        // And the two older rows are untouched — an ordinary edit still says it was one.
        Assert.Equal("CreatorEdit", versions[1].GetProperty("source").GetString());
        Assert.Null(versions[1].GetProperty("restoredFromVersionId").GetString());
    }

    /// <summary>
    /// The second defence against a duplicate version, and the one that needs no idempotency key: restoring
    /// content the recipe already has writes nothing.
    /// </summary>
    [Fact]
    public async Task A_restore_that_changes_nothing_writes_no_version()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedWithHistoryAsync(client, _fixture.WorkspaceA);

        var first = await BodyOf(await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1), new { expectedConcurrencyToken = token }, cancellation));

        var second = await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1),
            new { expectedConcurrencyToken = first.GetProperty("concurrencyToken").GetString() },
            cancellation);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(3, (await BodyOf(second)).GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    /// <summary>
    /// Restoring the current version is a legitimate request whose honest answer is the recipe, unchanged —
    /// not a refusal, and not a version recording nothing.
    /// </summary>
    [Fact]
    public async Task Restoring_the_current_version_changes_nothing()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedWithHistoryAsync(client, _fixture.WorkspaceA);

        var body = await BodyOf(await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 2), new { expectedConcurrencyToken = token }, cancellation));

        Assert.Equal("Lemon olive oil cake", body.GetProperty("title").GetString());
        Assert.Equal(2, body.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    /// <summary>
    /// A step that moved between groups, restored through the real seam. This is the case the reconciler
    /// exists for: the row has to be reparented rather than deleted and reinserted, because two entities with
    /// one key cannot be tracked at once and the delete-then-insert it implies would not survive a save.
    /// Asserted here, against real EF change tracking, and not only against object graphs.
    /// </summary>
    [Fact]
    public async Task A_step_that_moved_between_groups_is_restored_through_a_real_save()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);

        var created = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes",
            new
            {
                title = "Two-stage cake",
                instructions = new object[]
                {
                    new { title = "Batter", steps = new object[] { new { text = "Whisk the eggs." } } },
                    new { title = "To finish", steps = new object[] { new { text = "Dust with sugar." } } },
                },
            },
            cancellation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var first = await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), cancellation));
        var recipeId = first.GetProperty("id").GetGuid();

        var groups = first.GetProperty("instructionGroups").EnumerateArray().ToArray();
        var batterId = groups[0].GetProperty("id").GetGuid();
        var finishId = groups[1].GetProperty("id").GetGuid();
        var stepId = groups[0].GetProperty("steps").EnumerateArray().Single().GetProperty("id").GetGuid();
        var finishStepId = groups[1].GetProperty("steps").EnumerateArray().Single().GetProperty("id").GetGuid();

        // Move the whisking step into the second group, keeping its id — which is what makes this a move
        // rather than a deletion and an insertion.
        var moved = await client.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new
            {
                expectedConcurrencyToken = first.GetProperty("concurrencyToken").GetString(),
                instructions = new object[]
                {
                    new { id = batterId, title = "Batter", steps = Array.Empty<object>() },
                    new
                    {
                        id = finishId,
                        title = "To finish",
                        steps = new object[]
                        {
                            new { id = finishStepId, text = "Dust with sugar." },
                            new { id = stepId, text = "Whisk the eggs." },
                        },
                    },
                },
            },
            cancellation);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var afterMove = await BodyOf(moved);
        Assert.Empty(afterMove.GetProperty("instructionGroups").EnumerateArray()
            .Single(group => group.GetProperty("id").GetGuid() == batterId)
            .GetProperty("steps").EnumerateArray());

        // Now put it back.
        var response = await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1),
            new { expectedConcurrencyToken = afterMove.GetProperty("concurrencyToken").GetString() },
            cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var restored = await BodyOf(response);
        var batter = restored.GetProperty("instructionGroups").EnumerateArray()
            .Single(group => group.GetProperty("id").GetGuid() == batterId);

        // The same step, back under its original group and still carrying its original id.
        var step = Assert.Single(batter.GetProperty("steps").EnumerateArray());
        Assert.Equal(stepId, step.GetProperty("id").GetGuid());
        Assert.Equal("Whisk the eggs.", step.GetProperty("text").GetString());

        Assert.Equal(
            finishStepId,
            restored.GetProperty("instructionGroups").EnumerateArray()
                .Single(group => group.GetProperty("id").GetGuid() == finishId)
                .GetProperty("steps").EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    // ---- Roles ----

    [Fact]
    public async Task An_editor_may_restore()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedWithHistoryAsync(owner, _fixture.WorkspaceA);

        // The fixture's member in A is an Editor.
        Assert.Equal(WorkspaceRole.Editor, _fixture.WorkspaceA.MemberRole);

        using var editor = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: cancellation);
        var response = await editor.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1), new { expectedConcurrencyToken = token }, cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The role that proves restoring is not merely editing: a Contributor may change this recipe through
    /// <c>PATCH</c> and may not discard everything since version 1 in one request.
    /// </summary>
    [Fact]
    public async Task A_contributor_may_edit_but_may_not_restore()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedWithHistoryAsync(owner, _fixture.WorkspaceA);

        using var contributor = await _fixture.SignInAsync(ContributorEmail, cancellationToken: cancellation);

        var restore = await contributor.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1), new { expectedConcurrencyToken = token }, cancellation);

        Assert.Equal(HttpStatusCode.Forbidden, restore.StatusCode);

        var edit = await contributor.PatchAsJsonAsync(
            RecipeIn(_fixture.WorkspaceA, recipeId),
            new { expectedConcurrencyToken = token, title = "An ordinary contribution" },
            cancellation);

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_not_restore()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedWithHistoryAsync(owner, _fixture.WorkspaceB);

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: cancellation);
        var response = await viewer.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceB, recipeId, 1), new { expectedConcurrencyToken = token }, cancellation);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Refusals ----

    [Fact]
    public async Task A_restore_with_no_token_is_a_bad_request_naming_the_field()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedWithHistoryAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1), new { reason = "No token." }, cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeInvalidRequest, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    /// <summary>
    /// A stale token is a recoverable conflict, never a silent overwrite of whoever saved in between — and
    /// nothing of the creator's is lost, because their decision was "put it back to version 1".
    /// </summary>
    [Fact]
    public async Task A_token_that_was_never_this_recipes_is_a_conflict()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedWithHistoryAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1),
            new { expectedConcurrencyToken = "CAcGBQQDAgE=" },
            cancellation);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeConflict, (await BodyOf(response)).GetProperty("code").GetString());

        // And nothing happened: the recipe is still what the edit made it, with no third version.
        var reread = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), cancellation));
        Assert.Equal("Lemon olive oil cake", reread.GetProperty("title").GetString());
        Assert.Equal(2, reread.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    /// <summary>
    /// Its own 404 code, naming the route segment at fault — safe to disclose to a caller who has already
    /// been shown that this recipe exists and may read its history.
    /// </summary>
    [Fact]
    public async Task A_version_this_recipe_does_not_have_is_a_not_found_naming_the_segment()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedWithHistoryAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 9), new { expectedConcurrencyToken = token }, cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("versionNumber", out _));
    }

    /// <summary>
    /// A version number no version could carry never reaches the action: the route constraint answers 404, the
    /// same status a real unknown version gets, with the edge's generic code.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_version_number_below_one_is_refused_at_routing(int versionNumber)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedWithHistoryAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, versionNumber),
            new { expectedConcurrencyToken = token },
            cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The literal <c>compare</c> segment and a version number share a place in the URL, and the route
    /// constraint is what keeps routing able to tell them apart. Asserted because the day that constraint is
    /// dropped, one of the two routes silently stops being reachable.
    /// </summary>
    [Fact]
    public async Task The_comparison_route_still_answers_beside_the_restore_route()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, _, _) = await SeedWithHistoryAsync(client, _fixture.WorkspaceA);

        var response = await client.GetAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/versions/compare?from=1&to=2", cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Idempotency ----

    [Fact]
    public async Task A_replayed_restore_returns_the_original_response_and_writes_no_second_version()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedWithHistoryAsync(client, _fixture.WorkspaceA);
        var body = new { expectedConcurrencyToken = token, reason = "Put it back." };

        var first = await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1), body, "restore-key-1", cancellation);
        var second = await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1), body, "restore-key-1", cancellation);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(first.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal("true", second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());

        // Byte for byte the original answer, which also proves the response shape survives being stored as
        // JSON and read back.
        Assert.Equal((await BodyOf(first)).GetRawText(), (await BodyOf(second)).GetRawText());

        var history = await BodyOf(await client.GetAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/versions", cancellation));

        Assert.Equal(3, history.GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// One key cannot replay across versions: "put it back to 1" and "put it back to 2" are different
    /// requests, and answering the second with the first would be the failure a key exists to prevent.
    /// </summary>
    [Fact]
    public async Task The_same_key_for_a_different_version_is_refused()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedWithHistoryAsync(client, _fixture.WorkspaceA);
        var body = new { expectedConcurrencyToken = token };

        await client.PostAsJsonAsync(RestoreIn(_fixture.WorkspaceA, recipeId, 1), body, "restore-key-2", cancellation);
        var second = await client.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 2), body, "restore-key-2", cancellation);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, (await BodyOf(second)).GetProperty("code").GetString());
    }

    // ---- Isolation ----

    /// <summary>
    /// Workspace B's owner, naming A's recipe id exactly, is told the recipe does not exist — the same answer
    /// an id that was never created gets.
    /// </summary>
    [Fact]
    public async Task The_other_workspace_cannot_restore_this_recipe()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var inA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var (recipeId, token, _) = await SeedWithHistoryAsync(inA, _fixture.WorkspaceA);

        using var inB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);

        // Through B's own slug, which is the only route B may use at all.
        var throughB = await inB.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceB, recipeId, 1), new { expectedConcurrencyToken = token }, cancellation);

        Assert.Equal(HttpStatusCode.NotFound, throughB.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, (await BodyOf(throughB)).GetProperty("code").GetString());

        // And through A's slug, which B is not a member of: the workspace itself is hidden, so this is a 404
        // before any recipe is considered.
        var throughA = await inB.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1), new { expectedConcurrencyToken = token }, cancellation);

        Assert.Equal(HttpStatusCode.NotFound, throughA.StatusCode);

        // A's recipe is exactly as it was, with no version written by either attempt.
        var reread = await BodyOf(await inA.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), cancellation));
        Assert.Equal("Lemon olive oil cake", reread.GetProperty("title").GetString());
        Assert.Equal(2, reread.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    /// <summary>
    /// The mirror case, and the one a shared-vocabulary bug would show up in: each workspace restores its own
    /// recipe and neither sees the other's history change.
    /// </summary>
    [Fact]
    public async Task Each_workspace_restores_only_its_own_history()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var inA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        using var inB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);

        var (recipeA, tokenA, _) = await SeedWithHistoryAsync(inA, _fixture.WorkspaceA);
        var (recipeB, _, _) = await SeedWithHistoryAsync(inB, _fixture.WorkspaceB);

        await inA.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeA, 1), new { expectedConcurrencyToken = tokenA }, cancellation);

        var historyB = await BodyOf(await inB.GetAsync(
            $"{RecipeIn(_fixture.WorkspaceB, recipeB)}/versions", cancellation));

        Assert.Equal(2, historyB.GetProperty("items").GetArrayLength());
        Assert.DoesNotContain(
            historyB.GetProperty("items").EnumerateArray(),
            version => version.GetProperty("source").GetString() == "Restore");
    }

    /// <summary>
    /// The restored tag link points at A's own vocabulary row, not at B's identically named one. Checked with
    /// the filters ignored, because the point is what is in the table rather than what a scoped read returns.
    /// </summary>
    [Fact]
    public async Task A_restored_tag_links_to_this_workspaces_own_vocabulary_row()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var inA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        using var inB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);

        // B first, so a "weeknight" row exists in the other workspace before A's restore runs.
        await SeedWithHistoryAsync(inB, _fixture.WorkspaceB);
        var (recipeId, token, _) = await SeedWithHistoryAsync(inA, _fixture.WorkspaceA);

        var body = await BodyOf(await inA.PostAsJsonAsync(
            RestoreIn(_fixture.WorkspaceA, recipeId, 1), new { expectedConcurrencyToken = token }, cancellation));

        var linked = Assert.Single(body.GetProperty("tags").EnumerateArray()).GetProperty("workspaceTagId").GetGuid();

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var tag = await db.WorkspaceTags.IgnoreQueryFilters().SingleAsync(candidate => candidate.Id == linked, cancellation);

        Assert.Equal(_fixture.WorkspaceA.Id, tag.WorkspaceId);
        Assert.Equal("weeknight", tag.NormalizedName);
    }

    /// <inheritdoc cref="RecipeUpdateEndpointTests"/>
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
