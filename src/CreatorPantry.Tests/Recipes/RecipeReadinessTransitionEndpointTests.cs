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
/// <c>POST /api/v1/workspaces/{slug}/recipes/{recipeId}/readiness-transitions</c> through the real Gateway —
/// real cookie session, real gateway-signed internal token, real API — for the moves themselves, the roles
/// each needs, the approval's gate, the required idempotency key, and what one workspace can do to the other's
/// recipes (TESTRUN-005).
/// </summary>
/// <remarks>
/// The machine's shape is <see cref="RecipeStatusTransitionsTests"/>, what Business decides is
/// <see cref="RecipeTransitionBusinessTests"/>, and that the write is atomic is
/// <c>RecipeDataLayerTests</c>. What this file is for is the route.
/// </remarks>
public sealed class RecipeReadinessTransitionEndpointTests : IAsyncLifetime
{
    /// <summary>
    /// A Contributor may advance a recipe and may not approve or reopen one, which is the distinction the
    /// Editor bar exists to draw. The shared fixture seeds Owner plus Editor in A and Viewer in B, so this
    /// role has to be added.
    /// </summary>
    private const string ContributorEmail = "transition-contributor-a@example.com";

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

    private static string TransitionsIn(SeededWorkspace workspace, Guid recipeId) =>
        $"{RecipeIn(workspace, recipeId)}/readiness-transitions";

    // ---- The moves ----

    /// <summary>
    /// Forward, one step at a time, each answering with the recipe in its new state. A Contributor's moves:
    /// advancing your own work is the work.
    /// </summary>
    [Fact]
    public async Task A_draft_advances_one_step_at_a_time()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedAsync(client, _fixture.WorkspaceA);

        foreach (var target in new[] { "InDevelopment", "Testing", "ReadyForReview" })
        {
            var body = await MoveAsync(client, _fixture.WorkspaceA, recipeId, target);

            Assert.Equal(target, body.GetProperty("status").GetString());
        }
    }

    /// <summary>
    /// An invalid jump is refused, and the answer names the states the recipe could have gone to instead — so
    /// a client can say what to do rather than only that this failed.
    /// </summary>
    [Fact]
    public async Task An_invalid_jump_is_refused_and_says_where_the_recipe_could_go()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await PostAsync(client, _fixture.WorkspaceA, recipeId, "Approved");
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.TransitionInvalidRequest, problem.GetProperty("code").GetString());

        var detail = string.Join(" ", problem.GetProperty("errors").GetProperty("transition")
            .EnumerateArray().Select(entry => entry.GetString()));

        Assert.Contains(nameof(RecipeStatus.InDevelopment), detail, StringComparison.Ordinal);
        Assert.Contains(nameof(RecipeStatus.Archived), detail, StringComparison.Ordinal);

        // And the recipe did not move.
        Assert.Equal(nameof(RecipeStatus.Draft), await StatusAsync(client, _fixture.WorkspaceA, recipeId));
    }

    /// <summary>
    /// A reopen withdraws work somebody else advanced, so it is the one move that has to be explained. Without
    /// a reason it is refused; the field requirement is data-dependent, which is why the answer comes from
    /// Business rather than from the validator.
    /// </summary>
    [Fact]
    public async Task A_reopen_without_a_reason_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedAsync(client, _fixture.WorkspaceA);

        await MoveAsync(client, _fixture.WorkspaceA, recipeId, "InDevelopment");
        await MoveAsync(client, _fixture.WorkspaceA, recipeId, "Testing");

        var response = await PostAsync(client, _fixture.WorkspaceA, recipeId, "InDevelopment");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.TransitionInvalidRequest,
            (await BodyOf(response)).GetProperty("code").GetString());

        Assert.Equal(nameof(RecipeStatus.Testing), await StatusAsync(client, _fixture.WorkspaceA, recipeId));
    }

    /// <inheritdoc cref="A_reopen_without_a_reason_is_refused"/>
    [Fact]
    public async Task A_reopen_with_a_reason_returns_the_recipe_in_development()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedAsync(client, _fixture.WorkspaceA);

        await MoveAsync(client, _fixture.WorkspaceA, recipeId, "InDevelopment");
        await MoveAsync(client, _fixture.WorkspaceA, recipeId, "Testing");

        var body = await MoveAsync(
            client, _fixture.WorkspaceA, recipeId, "InDevelopment", reason: "The crumb was wrong.");

        Assert.Equal(nameof(RecipeStatus.InDevelopment), body.GetProperty("status").GetString());
    }

    /// <summary>
    /// Asking for the state the recipe is already in succeeds and changes nothing — a repeat rather than a
    /// jump. The timestamp is the assertion doing the work: a transition that had happened would have stamped
    /// it.
    /// </summary>
    [Fact]
    public async Task Asking_for_the_state_it_is_already_in_changes_nothing()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedAsync(client, _fixture.WorkspaceA);

        var before = await ReadAsync(client, _fixture.WorkspaceA, recipeId);

        var body = await MoveAsync(client, _fixture.WorkspaceA, recipeId, "Draft");

        Assert.Equal(nameof(RecipeStatus.Draft), body.GetProperty("status").GetString());
        Assert.Equal(
            before.GetProperty("updatedAt").GetString(),
            body.GetProperty("updatedAt").GetString());
    }

    /// <summary>
    /// Archiving and restoring are moves of this machine too, not commands beside it — so a recipe's editorial
    /// life reads as one sequence. The dedicated <c>/archive</c> and <c>/unarchive</c> routes still exist and
    /// reach the same rules.
    /// </summary>
    [Fact]
    public async Task Archiving_and_restoring_run_through_this_route_too()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedAsync(client, _fixture.WorkspaceA);

        var archived = await MoveAsync(client, _fixture.WorkspaceA, recipeId, "Archived");
        Assert.Equal(nameof(RecipeStatus.Archived), archived.GetProperty("status").GetString());

        var restored = await MoveAsync(client, _fixture.WorkspaceA, recipeId, "Draft");
        Assert.Equal(nameof(RecipeStatus.Draft), restored.GetProperty("status").GetString());
    }

    // ---- Roles ----

    /// <summary>
    /// A Contributor advances and may do nothing else. Both halves in one test, because the distinction is the
    /// point: the same caller, the same recipe, one move allowed and the next not.
    /// </summary>
    [Fact]
    public async Task A_contributor_may_advance_but_may_not_reopen()
    {
        using var contributor = await _fixture.SignInAsync(
            ContributorEmail, Password, TestContext.Current.CancellationToken);

        var recipeId = await SeedAsync(contributor, _fixture.WorkspaceA);

        // Allowed: Draft -> InDevelopment -> Testing are a Contributor's moves.
        await MoveAsync(contributor, _fixture.WorkspaceA, recipeId, "InDevelopment");
        await MoveAsync(contributor, _fixture.WorkspaceA, recipeId, "Testing");

        // Refused: reopening withdraws work, which needs an Editor.
        var response = await PostAsync(
            contributor, _fixture.WorkspaceA, recipeId, "InDevelopment", reason: "Actually, no.");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.TransitionForbidden,
            (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <inheritdoc cref="A_contributor_may_advance_but_may_not_reopen"/>
    [Fact]
    public async Task A_contributor_may_not_archive()
    {
        using var contributor = await _fixture.SignInAsync(
            ContributorEmail, Password, TestContext.Current.CancellationToken);

        var recipeId = await SeedAsync(contributor, _fixture.WorkspaceA);

        var response = await PostAsync(contributor, _fixture.WorkspaceA, recipeId, "Archived");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A Viewer has no move at all, so the route bar excludes them — refused by the policy before the body or
    /// the recipe is looked at.
    /// </summary>
    [Fact]
    public async Task A_viewer_may_not_transition_at_all()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB);
        var recipeId = await SeedAsync(owner, _fixture.WorkspaceB);

        using var viewer = await SignInAsync(_fixture.WorkspaceB, asOwner: false);
        var response = await PostAsync(viewer, _fixture.WorkspaceB, recipeId, "InDevelopment");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- The approval's gate ----

    /// <summary>
    /// An approval over outstanding blockers is a conflict, and the answer names the rules so a client can say
    /// which rather than sending the approver back to the readiness screen to guess.
    /// </summary>
    [Fact]
    public async Task An_approval_over_blockers_is_refused_and_names_them()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedAsync(client, _fixture.WorkspaceA);

        await AdvanceToReviewAsync(client, _fixture.WorkspaceA, recipeId);

        var response = await PostAsync(client, _fixture.WorkspaceA, recipeId, "Approved");
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.TransitionBlockedConflict, problem.GetProperty("code").GetString());

        var blocking = problem.GetProperty("errors").GetProperty("blockingRules")
            .EnumerateArray().Select(entry => entry.GetString()).ToList();

        Assert.Contains(RecipeReadinessCatalogue.IngredientsPresent, blocking);
        Assert.Contains(RecipeReadinessCatalogue.TestingCurrentVersionUntested, blocking);

        // Not among them: a hero image is advice, so a recipe can be approved before it is photographed —
        // which is also what makes the approval reachable at all, since nothing can link one yet.
        Assert.DoesNotContain(RecipeReadinessCatalogue.MediaHeroMissing, blocking);

        // A refused approval moves nothing and writes no transition.
        Assert.Equal(
            nameof(RecipeStatus.ReadyForReview), await StatusAsync(client, _fixture.WorkspaceA, recipeId));
        Assert.Equal(0, await TransitionCountAsync(recipeId, RecipeStatus.Approved));
    }

    /// <summary>
    /// The whole gate, end to end: a complete recipe, cooked and recorded, advanced through every state and
    /// approved — and the approval writes the version it names.
    /// </summary>
    /// <remarks>
    /// This is the test that proves the approval gate works at all through the API. It could not exist while
    /// <c>recipe.media.heroMissing</c> was a blocker, because nothing can link a hero image; every other
    /// blocker is clearable by editing the recipe or recording a test.
    /// </remarks>
    [Fact]
    public async Task A_complete_and_tested_recipe_can_be_approved()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CompleteRecipeAsync(client, _fixture.WorkspaceA);

        await AdvanceToReviewAsync(client, _fixture.WorkspaceA, recipeId);

        var body = await MoveAsync(client, _fixture.WorkspaceA, recipeId, "Approved", reason: "Third bake.");

        Assert.Equal(nameof(RecipeStatus.Approved), body.GetProperty("status").GetString());

        // The approval names immutable content: a version of its own, marked ready, and it is the recipe's
        // current one.
        var version = body.GetProperty("currentVersion");
        Assert.Equal(nameof(RecipeVersionSource.ReadinessApproval), version.GetProperty("source").GetString());
        Assert.Equal(nameof(RecipeVersionReadiness.Ready), version.GetProperty("readiness").GetString());

        Assert.Equal(1, await TransitionCountAsync(recipeId, RecipeStatus.Approved));
    }

    /// <summary>
    /// A recipe can be approved, reopened and approved again without being re-cooked. The approval writes a
    /// snapshot of words nobody changed, so the test that cleared the first approval still describes the
    /// second — which is what <c>RecipeReadinessRepository</c>'s content-equivalence is for.
    /// </summary>
    /// <remarks>
    /// Before that, this was a hard block: the second approval's gate read its own first snapshot as untested
    /// and demanded a test of content the creator had already tested.
    /// </remarks>
    [Fact]
    public async Task A_reopened_recipe_can_be_approved_again_without_being_cooked_again()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CompleteRecipeAsync(client, _fixture.WorkspaceA);

        await AdvanceToReviewAsync(client, _fixture.WorkspaceA, recipeId);
        await MoveAsync(client, _fixture.WorkspaceA, recipeId, "Approved");

        // Back out, and up again — none of which writes a version or records a test.
        await MoveAsync(client, _fixture.WorkspaceA, recipeId, "InDevelopment", reason: "One more look.");
        await MoveAsync(client, _fixture.WorkspaceA, recipeId, "Testing");
        await MoveAsync(client, _fixture.WorkspaceA, recipeId, "ReadyForReview");

        var body = await MoveAsync(client, _fixture.WorkspaceA, recipeId, "Approved", reason: "Still good.");

        Assert.Equal(nameof(RecipeStatus.Approved), body.GetProperty("status").GetString());
        Assert.Equal(2, await TransitionCountAsync(recipeId, RecipeStatus.Approved));
    }

    /// <summary>
    /// The equivalence stops at content: an edit after an approval is new words, and approving them again
    /// needs a new test. The blocker naming <c>currentVersionUntested</c> is the gate working, not failing.
    /// </summary>
    [Fact]
    public async Task An_edit_after_an_approval_needs_a_fresh_test_before_the_next_approval()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CompleteRecipeAsync(client, _fixture.WorkspaceA);

        await AdvanceToReviewAsync(client, _fixture.WorkspaceA, recipeId);
        await MoveAsync(client, _fixture.WorkspaceA, recipeId, "Approved");

        // An edit reopens the recipe on its own (RecipeStatusTransitions.EditReopens) and writes a version.
        await EditAsync(client, _fixture.WorkspaceA, recipeId, new { title = "Lemon olive oil cake" });
        Assert.Equal(
            nameof(RecipeStatus.InDevelopment), await StatusAsync(client, _fixture.WorkspaceA, recipeId));

        await MoveAsync(client, _fixture.WorkspaceA, recipeId, "Testing");
        await MoveAsync(client, _fixture.WorkspaceA, recipeId, "ReadyForReview");

        var response = await PostAsync(client, _fixture.WorkspaceA, recipeId, "Approved");
        var blocking = (await BodyOf(response)).GetProperty("errors").GetProperty("blockingRules")
            .EnumerateArray().Select(entry => entry.GetString()).ToList();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(RecipeReadinessCatalogue.TestingCurrentVersionUntested, blocking);
    }

    // ---- The required key ----

    /// <summary>
    /// The key is required here and accepted everywhere else in this module. A caller who loses the response
    /// to an approval cannot tell whether the recipe was approved, and the blind retry that follows is the one
    /// request that must not be able to write twice.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_key_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            TransitionsIn(_fixture.WorkspaceA, recipeId),
            new
            {
                targetStatus = "InDevelopment",
                expectedConcurrencyToken = await TokenAsync(client, _fixture.WorkspaceA, recipeId),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            IdempotencyPolicy.KeyRequiredCode, (await BodyOf(response)).GetProperty("code").GetString());

        Assert.Equal(nameof(RecipeStatus.Draft), await StatusAsync(client, _fixture.WorkspaceA, recipeId));
    }

    /// <summary>
    /// The same key and the same request replay the first answer rather than moving the recipe again — and the
    /// response says so, so a client can tell a replay from a fresh success.
    /// </summary>
    [Fact]
    public async Task The_same_key_replays_the_first_answer()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedAsync(client, _fixture.WorkspaceA);
        var token = await TokenAsync(client, _fixture.WorkspaceA, recipeId);

        var first = await PostAsync(
            client, _fixture.WorkspaceA, recipeId, "InDevelopment", token: token, key: "key-replay");
        var second = await PostAsync(
            client, _fixture.WorkspaceA, recipeId, "InDevelopment", token: token, key: "key-replay");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.False(first.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal("true", second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());

        // One move, one row: the replay wrote nothing.
        Assert.Equal(1, await TransitionCountAsync(recipeId, RecipeStatus.InDevelopment));
    }

    /// <summary>The same key with a different request is a reused key, not a replay.</summary>
    [Fact]
    public async Task The_same_key_with_a_different_target_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedAsync(client, _fixture.WorkspaceA);
        var token = await TokenAsync(client, _fixture.WorkspaceA, recipeId);

        await PostAsync(client, _fixture.WorkspaceA, recipeId, "InDevelopment", token: token, key: "key-reused");
        var second = await PostAsync(
            client, _fixture.WorkspaceA, recipeId, "Archived", token: token, key: "key-reused");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
    }

    // ---- Conflict ----

    /// <summary>
    /// A token that is not the recipe's is a conflict rather than a move applied to work the caller has not
    /// seen — and nothing moves.
    /// </summary>
    /// <remarks>
    /// A well-formed token the recipe never had, which is how <c>RecipeArchiveEndpointTests</c> makes this
    /// case too. It cannot be produced by moving the recipe under the caller in this host: these tests run on
    /// SQLite, whose customizer never bumps a row version, so an edit leaves the token exactly as it was.
    /// <c>RecipeTransitionBusinessTests.A_stale_token_conflicts_even_when_the_recipe_is_already_there</c> is
    /// where the genuinely-moved-on case is asserted. What this shows is the route's half: the check runs, the
    /// refusal is a 409 with a stable code, and no transition is written.
    /// </remarks>
    [Fact]
    public async Task A_token_that_is_not_the_recipes_is_a_conflict()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await PostAsync(
            client, _fixture.WorkspaceA, recipeId, "InDevelopment", token: "CAcGBQQDAgE=");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.RecipeConflict, (await BodyOf(response)).GetProperty("code").GetString());

        Assert.Equal(nameof(RecipeStatus.Draft), await StatusAsync(client, _fixture.WorkspaceA, recipeId));
        Assert.Equal(0, await TransitionCountAsync(recipeId, RecipeStatus.InDevelopment));
    }

    [Fact]
    public async Task A_malformed_token_is_a_bad_request_naming_the_field()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await PostAsync(
            client, _fixture.WorkspaceA, recipeId, "InDevelopment", token: "not a token");
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    // ---- Not found, and isolation ----

    [Fact]
    public async Task An_unknown_recipe_is_not_found()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            TransitionsIn(_fixture.WorkspaceA, Guid.NewGuid()),
            new { targetStatus = "InDevelopment", expectedConcurrencyToken = "AQIDBAUGBwg=" },
            "key-missing",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.RecipeNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// The other workspace's recipe is answered exactly as an unknown one is, so the route cannot be used to
    /// learn that a neighbour's recipe id exists (tenancy.md) — and nothing is moved.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_recipe_cannot_be_moved()
    {
        using var inB = await SignInAsync(_fixture.WorkspaceB);
        var theirs = await SeedAsync(inB, _fixture.WorkspaceB);
        var theirToken = await TokenAsync(inB, _fixture.WorkspaceB, theirs);

        using var inA = await SignInAsync(_fixture.WorkspaceA);

        var throughA = await PostAsync(
            inA, _fixture.WorkspaceA, theirs, "InDevelopment", token: theirToken, key: "key-cross");

        Assert.Equal(HttpStatusCode.NotFound, throughA.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.RecipeNotFound, (await BodyOf(throughA)).GetProperty("code").GetString());

        // Untouched, and no transition written for it.
        Assert.Equal(nameof(RecipeStatus.Draft), await StatusAsync(inB, _fixture.WorkspaceB, theirs));
        Assert.Equal(0, await TransitionCountAsync(theirs, RecipeStatus.InDevelopment));
    }

    /// <summary>
    /// Two recipes, one moved and one not, and the workspaces do not see each other's history. A
    /// single-workspace test would show neither.
    /// </summary>
    [Fact]
    public async Task A_transition_in_one_workspace_leaves_the_other_alone()
    {
        using var inA = await SignInAsync(_fixture.WorkspaceA);
        using var inB = await SignInAsync(_fixture.WorkspaceB);

        var mine = await SeedAsync(inA, _fixture.WorkspaceA);
        var theirs = await SeedAsync(inB, _fixture.WorkspaceB);

        await MoveAsync(inA, _fixture.WorkspaceA, mine, "InDevelopment");

        Assert.Equal(nameof(RecipeStatus.InDevelopment), await StatusAsync(inA, _fixture.WorkspaceA, mine));
        Assert.Equal(nameof(RecipeStatus.Draft), await StatusAsync(inB, _fixture.WorkspaceB, theirs));

        Assert.Equal(1, await TransitionCountAsync(mine, RecipeStatus.InDevelopment));
        Assert.Equal(0, await TransitionCountAsync(theirs, RecipeStatus.InDevelopment));

        // The rows are the workspace's own, which is the claim a shared table has to be able to make.
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var row = await db.RecipeStatusTransitions
            .IgnoreQueryFilters()
            .SingleAsync(entry => entry.RecipeId == mine, TestContext.Current.CancellationToken);

        Assert.Equal(_fixture.WorkspaceA.Id, row.WorkspaceId);
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail,
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<Guid> SeedAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title = "Olive oil cake" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    /// <summary>
    /// A recipe with everything the blocking rules ask for, cooked once and recorded — so the only thing
    /// standing between it and an approval is the machine.
    /// </summary>
    /// <remarks>
    /// The blockers it clears, in order: ingredients and instructions exist, the yield and a time are stated,
    /// no line is ambiguous (nothing resolves them in this host, and an unresolved line is only a
    /// recommendation), there is no AI work outstanding, and a successful test names the current version. A
    /// hero image is deliberately absent, which is the point — it is advice.
    /// </remarks>
    private static async Task<Guid> CompleteRecipeAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var recipeId = await SeedAsync(client, workspace);

        await EditAsync(client, workspace, recipeId, new
        {
            yieldText = "makes 12 muffins",
            prepTimeMinutes = 20,
            cookTimeMinutes = 35,
            ingredientGroups = new[]
            {
                new { ingredients = new[] { new { displayText = "2 cups flour" } } },
            },
            instructions = new[]
            {
                new { steps = new[] { new { text = "Whisk the eggs and the sugar." } } },
            },
        });

        // Against the version the edit just wrote, which is what makes it evidence about these words.
        var versionNumber = (await ReadAsync(client, workspace, recipeId))
            .GetProperty("currentVersion").GetProperty("versionNumber").GetInt32();

        var recorded = await client.PostAsJsonAsync(
            $"{RecipeIn(workspace, recipeId)}/test-runs",
            new
            {
                sourceVersionNumber = versionNumber,
                testedAt = "2026-09-20T18:00:00Z",
                outcome = "Succeeded",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);

        return recipeId;
    }

    /// <summary>Walks a draft up to ReadyForReview, which is where the approval is asked for.</summary>
    private static async Task AdvanceToReviewAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId)
    {
        await MoveAsync(client, workspace, recipeId, "InDevelopment");
        await MoveAsync(client, workspace, recipeId, "Testing");
        await MoveAsync(client, workspace, recipeId, "ReadyForReview");
    }

    private static async Task EditAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId, object fields)
    {
        var token = await TokenAsync(client, workspace, recipeId);

        // Merged rather than spread, because the patch's token has to travel with whatever the caller sent.
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(fields))!;
        body["expectedConcurrencyToken"] = JsonSerializer.SerializeToElement(token);

        var response = await client.PatchAsJsonAsync(
            RecipeIn(workspace, recipeId), body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Posts one transition, with a fresh token and a fresh key unless the caller names them.</summary>
    private static async Task<HttpResponseMessage> PostAsync(
        GatewayClient client,
        SeededWorkspace workspace,
        Guid recipeId,
        string target,
        string? reason = null,
        string? token = null,
        string? key = null)
    {
        // Read for the token unless one was supplied. A transition invalidates nothing a caller can see on
        // SQLite, but the read is what a real client would do and it keeps the helper honest on SQL Server.
        token ??= await TokenAsync(client, workspace, recipeId);

        return await client.PostAsJsonAsync(
            TransitionsIn(workspace, recipeId),
            new { targetStatus = target, reason, expectedConcurrencyToken = token },
            key ?? Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);
    }

    /// <summary>Posts one transition and asserts it was applied, returning the recipe.</summary>
    private static async Task<JsonElement> MoveAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId, string target, string? reason = null)
    {
        var response = await PostAsync(client, workspace, recipeId, target, reason);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyOf(response);
    }

    private static async Task<JsonElement> ReadAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId) =>
        await BodyOf(await client.GetAsync(
            RecipeIn(workspace, recipeId), TestContext.Current.CancellationToken));

    private static async Task<string> TokenAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId) =>
        (await ReadAsync(client, workspace, recipeId)).GetProperty("concurrencyToken").GetString()!;

    private static async Task<string?> StatusAsync(
        GatewayClient client, SeededWorkspace workspace, Guid recipeId) =>
        (await ReadAsync(client, workspace, recipeId)).GetProperty("status").GetString();

    /// <summary>
    /// How many transitions to one state this recipe has. Read past the query filter, because the point of
    /// several of these assertions is that the row is <em>not</em> there for a workspace that should not have
    /// one.
    /// </summary>
    private async Task<int> TransitionCountAsync(Guid recipeId, RecipeStatus target)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.RecipeStatusTransitions
            .IgnoreQueryFilters()
            .CountAsync(
                row => row.RecipeId == recipeId && row.ToStatus == target,
                TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

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

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
