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
/// <c>PATCH .../test-runs/{id}</c> and <c>POST .../test-runs/{id}/issues/{issueId}/resolution</c> through the
/// real Gateway, for the two commands TESTRUN-002 asks for as separate atomic operations.
/// </summary>
/// <remarks>
/// Bodies are anonymous objects on purpose, so what is on the wire is what the test wrote — including the
/// absent fields a merge patch depends on.
/// </remarks>
public sealed class RecipeTestRunUpdateEndpointTests : IAsyncLifetime
{
    /// <summary>
    /// The role that proves resolving is gated a step above recording: a Contributor may write a test up and
    /// may not close its issues.
    /// </summary>
    private const string ContributorEmail = "resolve-contributor-a@example.com";

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

    private static string RunIn(SeededWorkspace workspace, Guid recipeId, Guid runId) =>
        $"{RecipeIn(workspace, recipeId)}/test-runs/{runId}";

    private static string ResolutionIn(SeededWorkspace workspace, Guid recipeId, Guid runId, Guid issueId) =>
        $"{RunIn(workspace, recipeId, runId)}/issues/{issueId}/resolution";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>A recipe, a test of its version 1 with one note and one issue raised from it.</summary>
    private async Task<SeededTest> SeedAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var cancellation = TestContext.Current.CancellationToken;

        var recipe = await BodyOf(await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title = "Olive oil cake", yieldText = "makes 12 muffins" },
            cancellation));
        var recipeId = recipe.GetProperty("recipeId").GetGuid();

        var run = await BodyOf(await client.PostAsJsonAsync(
            $"{RecipeIn(workspace, recipeId)}/test-runs",
            new
            {
                sourceVersionNumber = 1,
                testedAt = "2026-09-20T18:00:00Z",
                outcome = "SucceededWithIssues",
                rating = 3,
                summaryNotes = "Went well enough.",
                observations = new object[] { new { kind = "Texture", text = "The crumb was close." } },
                issues = new object[]
                {
                    new { severity = "Major", title = "Crumb too dense", observationIndex = 0 },
                },
            },
            cancellation));

        var runId = run.GetProperty("testRunId").GetGuid();

        return new SeededTest(
            recipeId,
            runId,
            run.GetProperty("observationIds").EnumerateArray().Single().GetGuid(),
            run.GetProperty("issueIds").EnumerateArray().Single().GetGuid(),

            // Straight from the create, which is the whole reason it publishes one: a client that has just
            // recorded a test would otherwise have no way to edit it.
            run.GetProperty("concurrencyToken").GetString()!);
    }

    /// <summary>A well-formed token that was never issued, for the paths that must fail before comparing it.</summary>
    private const string Seed = "AAAAAAAAAAA=";

    // ---- Update ----

    [Fact]
    public async Task An_edit_changes_what_it_names_and_leaves_the_rest()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);
        var token = seeded.Token;

        var response = await client.PatchAsJsonAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId),
            new { expectedConcurrencyToken = token, rating = 5, outcome = "Succeeded" },
            cancellation);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, body.GetProperty("rating").GetInt32());
        Assert.Equal("Succeeded", body.GetProperty("outcome").GetString());

        // Untouched by a body that did not mention them.
        Assert.Equal("Went well enough.", body.GetProperty("summaryNotes").GetString());
        Assert.Single(body.GetProperty("observations").EnumerateArray());

        // The response carries a token at all, which is the whole reason it is the run rather than an empty 204.
        //
        // That it is a *new* token cannot be asserted here: this host is SQLite, where the row version is filled
        // by a default expression on insert and never bumped on update — SqliteModelCustomizer says so, and adds
        // that concurrency behaviour belongs to tests against a real database.
        // RecipeTestRunDataLayerTests covers it there.
        Assert.False(string.IsNullOrEmpty(body.GetProperty("concurrencyToken").GetString()));
    }

    [Fact]
    public async Task A_field_sent_as_null_is_cleared()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        var body = await BodyOf(await client.PatchAsJsonAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId),
            new { expectedConcurrencyToken = seeded.Token, summaryNotes = (string?)null },
            cancellation));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("summaryNotes").ValueKind);
        Assert.Equal(3, body.GetProperty("rating").GetInt32());
    }

    [Fact]
    public async Task A_token_that_was_never_issued_is_refused_as_a_conflict_of_its_own()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        // A well-formed token naming a state this run was never in. That is as close to a stale token as this
        // host can get — SQLite never bumps the row version, so a genuinely superseded token cannot be produced
        // here; RecipeTestRunDataLayerTests exercises the real race against SQL Server.
        var response = await client.PatchAsJsonAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId),
            new { expectedConcurrencyToken = Seed, rating = 1 },
            cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Its own code, not the recipe's: a client showing "this test changed" must not be able to confuse it
        // with the recipe having changed.
        Assert.Equal(RecipeErrorCodes.TestRunConflict, problem.GetProperty("code").GetString());

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // And nothing was written — the refused edit left the test exactly as it was.
        var stored = await db.RecipeTestRuns.IgnoreQueryFilters()
            .SingleAsync(run => run.Id == seeded.RunId, cancellation);
        Assert.Equal(3, stored.Rating);
    }

    [Fact]
    public async Task An_edit_with_no_token_is_a_bad_request_naming_the_field()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId), new { rating = 2 }, cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.TestRunInvalidRequest, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("expectedConcurrencyToken", out _));
    }

    [Fact]
    public async Task An_unknown_test_and_one_of_another_recipe_are_the_same_answer()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);
        var other = await SeedAsync(client, _fixture.WorkspaceA);
        var body = new { expectedConcurrencyToken = Seed, rating = 2 };

        var unknown = await client.PatchAsJsonAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, Guid.NewGuid()), body, cancellation);

        // A real run, under the wrong recipe. Indistinguishable from an id that never existed, which is what
        // keeps the route from confirming that somebody else's test is real.
        var wrongRecipe = await client.PatchAsJsonAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, other.RunId), body, cancellation);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongRecipe.StatusCode);
        Assert.Equal(RecipeErrorCodes.TestRunNotFound, (await BodyOf(unknown)).GetProperty("code").GetString());
        Assert.Equal(RecipeErrorCodes.TestRunNotFound, (await BodyOf(wrongRecipe)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_viewer_may_not_edit_a_test()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(owner, _fixture.WorkspaceB);

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: cancellation);
        var response = await viewer.PatchAsJsonAsync(
            RunIn(_fixture.WorkspaceB, seeded.RecipeId, seeded.RunId),
            new { expectedConcurrencyToken = Seed, rating = 1 },
            cancellation);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_submitted_observation_list_replaces_the_set()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PatchAsJsonAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId),
            new
            {
                expectedConcurrencyToken = seeded.Token,
                observations = new object[]
                {
                    new { id = seeded.ObservationId, kind = "Texture", text = "The crumb was dense." },
                    new { kind = "Appearance", text = "Pale on top." },
                },
                issues = new object[]
                {
                    new { id = seeded.IssueId, severity = "Blocking", title = "Crumb far too dense", observationIndex = 0 },
                },
            },
            cancellation);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var observations = body.GetProperty("observations").EnumerateArray().ToList();
        Assert.Equal(2, observations.Count);

        // Updated in place, so the id survives and the issue's link to it is still meaningful.
        Assert.Equal(seeded.ObservationId, observations[0].GetProperty("id").GetGuid());
        Assert.Equal("The crumb was dense.", observations[0].GetProperty("text").GetString());

        var issue = Assert.Single(body.GetProperty("issues").EnumerateArray().ToList());
        Assert.Equal("Blocking", issue.GetProperty("severity").GetString());
        Assert.Equal(seeded.ObservationId, issue.GetProperty("testObservationId").GetGuid());
    }

    [Fact]
    public async Task A_replayed_edit_returns_the_original_response()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);
        var body = new { expectedConcurrencyToken = seeded.Token, rating = 5 };
        var path = RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId);

        var first = await client.PatchAsJsonAsync(path, body, "edit-key-1", cancellation);
        var second = await client.PatchAsJsonAsync(path, body, "edit-key-1", cancellation);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Without the key the replay would be a conflict — the first edit moved the token the second quotes.
        Assert.Equal("true", second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());
        Assert.Equal((await BodyOf(first)).GetRawText(), (await BodyOf(second)).GetRawText());
    }

    // ---- Resolution ----

    [Fact]
    public async Task Resolving_an_issue_records_the_decision_and_rewrites_nothing()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ResolutionIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId, seeded.IssueId),
            new { kind = "WontFix", notes = "Dense is what we wanted." },
            cancellation);
        var body = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(seeded.IssueId, body.GetProperty("testIssueId").GetGuid());
        Assert.Equal("WontFix", body.GetProperty("resolution").GetProperty("kind").GetString());
        Assert.EndsWith("/resolution", response.Headers.Location!.ToString(), StringComparison.Ordinal);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // The restriction, checked where it can actually be checked. The tester's words are exactly as they
        // were, and so is the issue's own title — a resolution says what was done, never what happened.
        var observation = await db.TestObservations.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == seeded.ObservationId, cancellation);
        Assert.Equal("The crumb was close.", observation.Text);

        var issue = await db.TestIssues.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == seeded.IssueId, cancellation);
        Assert.Equal("Crumb too dense", issue.Title);
        Assert.Equal(seeded.ObservationId, issue.TestObservationId);
    }

    [Fact]
    public async Task An_issue_cannot_be_resolved_twice()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);
        var path = ResolutionIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId, seeded.IssueId);

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(path, new { kind = "Fixed" }, cancellation)).StatusCode);

        // A different decision, so no idempotency record can answer it: this is a genuinely second resolution.
        var second = await client.PostAsJsonAsync(path, new { kind = "NotReproduced" }, cancellation);
        var problem = await BodyOf(second);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(RecipeErrorCodes.TestIssueResolvedConflict, problem.GetProperty("code").GetString());

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // One decision stands, and it is the first. The unique index is what guarantees that rather than the
        // order two requests happened to arrive in.
        var resolution = await db.TestIssueResolutions.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.TestIssueId == seeded.IssueId, cancellation);
        Assert.Equal(TestIssueResolutionKind.Fixed, resolution.Kind);
    }

    [Fact]
    public async Task A_replayed_resolution_returns_the_original_response()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);
        var path = ResolutionIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId, seeded.IssueId);
        var body = new { kind = "Fixed", notes = "Raised the hydration." };

        var first = await client.PostAsJsonAsync(path, body, "resolve-key-1", cancellation);
        var second = await client.PostAsJsonAsync(path, body, "resolve-key-1", cancellation);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // The reason a key matters more here than on most routes: without it, a retry after a lost response is
        // told the issue is already resolved by its own earlier attempt, which reads as somebody else deciding.
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());
        Assert.Equal((await BodyOf(first)).GetRawText(), (await BodyOf(second)).GetRawText());
    }

    [Fact]
    public async Task A_correction_version_this_recipe_does_not_have_is_not_found()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ResolutionIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId, seeded.IssueId),
            new { kind = "Fixed", resolutionVersionNumber = 9 },
            cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("resolutionVersionNumber", out _));
    }

    [Fact]
    public async Task A_later_version_may_be_named_as_the_correction()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        // An edit to the recipe writes version 2, which is a version later than the one the test was run
        // against and so a candidate for having fixed it.
        await EditRecipeAsync(client, _fixture.WorkspaceA, seeded.RecipeId);

        var response = await client.PostAsJsonAsync(
            ResolutionIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId, seeded.IssueId),
            new { kind = "Fixed", resolutionVersionNumber = 2, notes = "Raised the hydration." },
            cancellation);
        var resolution = (await BodyOf(response)).GetProperty("resolution");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotEqual(Guid.Empty, resolution.GetProperty("resolutionRecipeVersionId").GetGuid());

        // No override happened, so nothing records one.
        Assert.Equal(
            JsonValueKind.Null, resolution.GetProperty("predatingVersionOverrideReason").ValueKind);
    }

    [Fact]
    public async Task The_tested_version_itself_is_refused_as_the_correction()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        // Version 1 is what was tested, so it cannot contain the fix for what that test found.
        var response = await client.PostAsJsonAsync(
            ResolutionIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId, seeded.IssueId),
            new { kind = "Fixed", resolutionVersionNumber = 1 },
            cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.TestIssueInvalidRequest, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("resolutionVersionNumber", out _));
    }

    [Fact]
    public async Task An_earlier_version_is_accepted_when_the_override_says_why()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ResolutionIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId, seeded.IssueId),
            new
            {
                kind = "Fixed",
                resolutionVersionNumber = 1,
                predatingVersionOverrideReason = "Version 1 had it right; the change that broke it came later.",
            },
            cancellation);
        var resolution = (await BodyOf(response)).GetProperty("resolution");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Stored, because this is the case where somebody overrode a check and the next reader needs to know it
        // was a decision rather than a mis-click.
        Assert.Equal(
            "Version 1 had it right; the change that broke it came later.",
            resolution.GetProperty("predatingVersionOverrideReason").GetString());
    }

    [Fact]
    public async Task Only_a_fix_may_name_a_correction_version()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);
        await EditRecipeAsync(client, _fixture.WorkspaceA, seeded.RecipeId);

        var response = await client.PostAsJsonAsync(
            ResolutionIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId, seeded.IssueId),
            new { kind = "WontFix", resolutionVersionNumber = 2 },
            cancellation);

        // Otherwise a history can report an issue as declined while pointing at the version that fixed it —
        // which is also what CK_TestIssueResolutions_Version_Kind refuses at the database.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.TestIssueInvalidRequest, (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_contributor_may_edit_a_test_but_not_resolve_its_issues()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(owner, _fixture.WorkspaceA);

        using var contributor = await _fixture.SignInAsync(ContributorEmail, cancellationToken: cancellation);

        var edit = await contributor.PatchAsJsonAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId),
            new { expectedConcurrencyToken = seeded.Token, rating = 4 },
            cancellation);

        var resolve = await contributor.PostAsJsonAsync(
            ResolutionIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId, seeded.IssueId),
            new { kind = "Fixed" },
            cancellation);

        // Reporting and deciding are different acts: finishing a write-up is Contributor work, closing an issue
        // is Editor work.
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, resolve.StatusCode);
    }

    [Fact]
    public async Task A_resolved_issue_cannot_be_dropped_by_an_edit()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var seeded = await SeedAsync(client, _fixture.WorkspaceA);

        await client.PostAsJsonAsync(
            ResolutionIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId, seeded.IssueId),
            new { kind = "Fixed" },
            cancellation);

        var response = await client.PatchAsJsonAsync(
            RunIn(_fixture.WorkspaceA, seeded.RecipeId, seeded.RunId),
            new { expectedConcurrencyToken = seeded.Token, issues = Array.Empty<object>() },
            cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.TestIssueResolvedRemovalConflict, problem.GetProperty("code").GetString());

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // Both survive: the decision is still there, and so is the issue it was about.
        Assert.True(await db.TestIssues.IgnoreQueryFilters()
            .AnyAsync(issue => issue.Id == seeded.IssueId, cancellation));
        Assert.True(await db.TestIssueResolutions.IgnoreQueryFilters()
            .AnyAsync(resolution => resolution.TestIssueId == seeded.IssueId, cancellation));
    }

    // ---- Isolation ----

    [Fact]
    public async Task Neither_command_reaches_another_workspaces_test()
    {
        var cancellation = TestContext.Current.CancellationToken;

        using var bOwner = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);
        var foreign = await SeedAsync(bOwner, _fixture.WorkspaceB);

        using var aOwner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var own = await SeedAsync(aOwner, _fixture.WorkspaceA);

        // Workspace A's own route, naming B's recipe and B's run.
        var edit = await aOwner.PatchAsJsonAsync(
            RunIn(_fixture.WorkspaceA, foreign.RecipeId, foreign.RunId),
            new { expectedConcurrencyToken = Seed, rating = 1 },
            cancellation);

        var resolve = await aOwner.PostAsJsonAsync(
            ResolutionIn(_fixture.WorkspaceA, foreign.RecipeId, foreign.RunId, foreign.IssueId),
            new { kind = "Fixed" },
            cancellation);

        // And A's own recipe with B's run id, which is the subtler attempt: the recipe is readable, so only the
        // run's own scoping stops this.
        var mixed = await aOwner.PatchAsJsonAsync(
            RunIn(_fixture.WorkspaceA, own.RecipeId, foreign.RunId),
            new { expectedConcurrencyToken = Seed, rating = 1 },
            cancellation);

        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, resolve.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, mixed.StatusCode);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // Nothing was written to B's test, and nothing was resolved.
        var stored = await db.RecipeTestRuns.IgnoreQueryFilters()
            .SingleAsync(run => run.Id == foreign.RunId, cancellation);
        Assert.Equal(3, stored.Rating);
        Assert.Equal(_fixture.WorkspaceB.Id, stored.WorkspaceId);
        Assert.False(await db.TestIssueResolutions.IgnoreQueryFilters().AnyAsync(cancellation));
    }

    /// <summary>An edit to the recipe, which writes a second version for a resolution to be able to name.</summary>
    private static async Task EditRecipeAsync(GatewayClient client, SeededWorkspace workspace, Guid recipeId)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var detail = await BodyOf(await client.GetAsync(RecipeIn(workspace, recipeId), cancellation));

        var response = await client.PatchAsJsonAsync(
            RecipeIn(workspace, recipeId),
            new
            {
                expectedConcurrencyToken = detail.GetProperty("concurrencyToken").GetString(),
                notes = "Raised the hydration.",
            },
            cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <inheritdoc cref="RecipeDuplicateEndpointTests"/>
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

    private sealed record SeededTest(
        Guid RecipeId, Guid RunId, Guid ObservationId, Guid IssueId, string Token);
}
