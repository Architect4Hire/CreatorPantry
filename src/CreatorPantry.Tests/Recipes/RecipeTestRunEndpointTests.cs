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
/// <c>POST /api/v1/workspaces/{slug}/recipes/{recipeId}/test-runs</c> through the real Gateway — real cookie
/// session, real gateway-signed internal token, real API — for what a recorded test is, who may record one,
/// what it refuses, and what it deliberately cannot do.
/// </summary>
/// <remarks>
/// Bodies are anonymous objects on purpose, so what is on the wire is what the test wrote — including the
/// attachment field the contract does not have.
/// </remarks>
public sealed class RecipeTestRunEndpointTests : IAsyncLifetime
{
    /// <summary>
    /// The role that proves recording a test is not gated like archiving: the fixture seeds a Viewer, and a
    /// Contributor has to be added to show where the bar actually sits.
    /// </summary>
    private const string ContributorEmail = "testrun-contributor-a@example.com";

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

    private static string TestRunsIn(SeededWorkspace workspace, Guid recipeId) =>
        $"{RecipeIn(workspace, recipeId)}/test-runs";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static object ValidBody(object? extra = null) => extra ?? new
    {
        sourceVersionNumber = 1,
        testedAt = "2026-09-20T18:00:00Z",
        outcome = "SucceededWithIssues",
        rating = 4,
        environmentNotes = "Fan oven, ran hot.",
        actualYieldText = "got 10, not 12",
        actualCookTimeMinutes = 32,
    };

    /// <summary>A recipe whose version 1 exists, which is all a test needs to point at.</summary>
    private async Task<Guid> SeedRecipeAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var cancellation = TestContext.Current.CancellationToken;

        var created = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new
            {
                title = "Olive oil cake",
                prepTimeMinutes = 20,
                yieldText = "makes 12 muffins",
            },
            cancellation);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (await BodyOf(created)).GetProperty("recipeId").GetGuid();
    }

    // ---- What a recorded test is ----

    [Fact]
    public async Task A_test_is_recorded_against_the_version_it_names()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(TestRunsIn(_fixture.WorkspaceA, recipeId), ValidBody(), cancellation);
        var created = await BodyOf(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(recipeId, created.GetProperty("recipeId").GetGuid());
        Assert.Equal(1, created.GetProperty("sourceVersionNumber").GetInt32());
        Assert.Equal("SucceededWithIssues", created.GetProperty("outcome").GetString());

        // The exact version, resolved from the number the caller cited, so a later reader can say what was
        // cooked without trusting the number to still mean the same thing.
        Assert.NotEqual(Guid.Empty, created.GetProperty("recipeVersionId").GetGuid());

        // The location names the run's own resource. No GET serves it yet — the read seam arrives with the
        // history — but a created resource has an address regardless.
        var testRunId = created.GetProperty("testRunId").GetGuid();
        Assert.EndsWith($"/test-runs/{testRunId:D}", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recording_a_test_changes_nothing_about_the_recipe()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var before = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), cancellation));

        await client.PostAsJsonAsync(TestRunsIn(_fixture.WorkspaceA, recipeId), ValidBody(), cancellation);

        var after = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), cancellation));

        // The whole restriction, on the surface a client can see. An unchanged concurrency token is the
        // strongest available statement that no recipe row was written: had anything touched it, the token
        // would have moved and every open editor's next save would have been refused.
        Assert.Equal(
            before.GetProperty("concurrencyToken").GetString(),
            after.GetProperty("concurrencyToken").GetString());
        Assert.Equal(before.GetProperty("updatedAt").GetString(), after.GetProperty("updatedAt").GetString());

        // The actual figures the test reported did not become the recipe's claims.
        Assert.Equal("makes 12 muffins", after.GetProperty("yieldText").GetString());
        Assert.Equal(before.GetRawText(), after.GetRawText());
    }

    [Fact]
    public async Task Issues_are_attached_to_the_observations_the_request_pointed_at()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId),
            new
            {
                sourceVersionNumber = 1,
                testedAt = "2026-09-20T18:00:00Z",
                observations = new object[]
                {
                    new { kind = "Appearance", text = "Pale on top." },
                    new { kind = "Texture", text = "The crumb was close." },
                },
                issues = new object[]
                {
                    new { severity = "Major", title = "Crumb too dense", observationIndex = 1 },
                    new { severity = "Minor", title = "Filed on its own" },
                },
            },
            cancellation);

        var created = await BodyOf(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Both id lists come back in submitted order, which is what lets a client resolve an issue later
        // without listing the test back.
        var observationIds = created.GetProperty("observationIds").EnumerateArray().Select(id => id.GetGuid()).ToList();
        var issueIds = created.GetProperty("issueIds").EnumerateArray().Select(id => id.GetGuid()).ToList();
        Assert.Equal(2, observationIds.Count);
        Assert.Equal(2, issueIds.Count);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var issues = await db.TestIssues.IgnoreQueryFilters()
            .Where(issue => issueIds.Contains(issue.Id))
            .OrderBy(issue => issue.SortOrder)
            .ToListAsync(cancellation);

        // The index the request used resolved to the id the write gave that note.
        Assert.Equal(observationIds[1], issues[0].TestObservationId);

        // An issue filed on its own points at nothing, which is a legitimate state and not a missing link.
        Assert.Null(issues[1].TestObservationId);
    }

    // ---- What it deliberately cannot do ----

    [Fact]
    public async Task The_route_cannot_be_used_to_link_media()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var someoneElsesAsset = Guid.NewGuid();

        // A body that tries every plausible spelling of an attachment. The contract has no such field, so the
        // binder drops all of it — which is the point: linking test media needs something that can authorize
        // an asset against this workspace, and the media aggregate does not exist yet. Accepting the field and
        // validating it against nothing is the failure this shape prevents.
        var response = await client.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId),
            new
            {
                sourceVersionNumber = 1,
                testedAt = "2026-09-20T18:00:00Z",
                attachments = new object[] { new { mediaAssetId = someoneElsesAsset, caption = "The collapsed loaf." } },
                mediaAssetId = someoneElsesAsset,
            },
            cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // No attachment row exists at all, for this asset or any other. If a future change adds the field
        // without adding the authorization to go with it, this is what fails.
        Assert.False(await db.TestAttachmentLinks.IgnoreQueryFilters().AnyAsync(cancellation));
    }

    // ---- Refusals ----

    [Fact]
    public async Task A_test_that_names_no_version_is_a_bad_request_naming_the_field()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId),
            new { testedAt = "2026-09-20T18:00:00Z" },
            cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.TestRunInvalidRequest, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("sourceVersionNumber", out _));
    }

    [Fact]
    public async Task A_version_the_recipe_does_not_have_is_not_found_and_names_the_field()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 9, testedAt = "2026-09-20T18:00:00Z" },
            cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Its own code, and it names the field — that naming is the only reason this 404 is worth telling
        // apart from the one that hides a recipe.
        Assert.Equal(RecipeErrorCodes.VersionNotFound, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("sourceVersionNumber", out _));
    }

    [Fact]
    public async Task An_unknown_recipe_is_not_found()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);

        var response = await client.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, Guid.NewGuid()), ValidBody(), cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_archived_recipe_refuses_a_test_with_its_own_conflict()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        // The archive command requires the recipe's token, so it has to be read first.
        var detail = await BodyOf(await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), cancellation));
        var archived = await client.PostAsJsonAsync(
            $"{RecipeIn(_fixture.WorkspaceA, recipeId)}/archive",
            new { expectedConcurrencyToken = detail.GetProperty("concurrencyToken").GetString() },
            cancellation);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        var response = await client.PostAsJsonAsync(TestRunsIn(_fixture.WorkspaceA, recipeId), ValidBody(), cancellation);
        var problem = await BodyOf(response);

        // 409 rather than 403: the role is not the problem, and an Owner gets this too. Its own code rather
        // than a stale-token conflict, because the remedies are opposites — this one says "bring the recipe
        // back", and retrying will fail forever.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeArchivedConflict, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_test_dated_in_the_future_is_refused()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, testedAt = DateTimeOffset.UtcNow.AddDays(1) },
            cancellation);
        var problem = await BodyOf(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.TestRunInvalidRequest, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("testedAt", out _));
    }

    // ---- Who may record one ----

    [Fact]
    public async Task A_contributor_may_record_a_test()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var recipeId = await SeedRecipeAsync(owner, _fixture.WorkspaceA);

        using var contributor = await _fixture.SignInAsync(ContributorEmail, cancellationToken: cancellation);
        var response = await contributor.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId), ValidBody(), cancellation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_not_record_a_test()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);
        var recipeId = await SeedRecipeAsync(owner, _fixture.WorkspaceB);

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: cancellation);
        var response = await viewer.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceB, recipeId), ValidBody(), cancellation);

        // Forbidden rather than not-found: being refused for role is safe to disclose to somebody already known
        // to belong here.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Isolation ----

    [Fact]
    public async Task Another_workspaces_recipe_cannot_be_tested_and_is_not_found()
    {
        var cancellation = TestContext.Current.CancellationToken;

        using var bOwner = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);
        var foreignRecipeId = await SeedRecipeAsync(bOwner, _fixture.WorkspaceB);

        using var aOwner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);

        // Workspace A's own route, naming B's recipe. Nothing distinguishes this from an id that never
        // existed, which is what stops a caller learning that B's recipe is real.
        var response = await aOwner.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, foreignRecipeId), ValidBody(), cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, (await BodyOf(response)).GetProperty("code").GetString());

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.False(await db.RecipeTestRuns.IgnoreQueryFilters()
            .AnyAsync(run => run.RecipeId == foreignRecipeId, cancellation));
    }

    [Fact]
    public async Task A_recorded_test_belongs_to_the_workspace_that_recorded_it()
    {
        var cancellation = TestContext.Current.CancellationToken;

        using var aOwner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var aRecipeId = await SeedRecipeAsync(aOwner, _fixture.WorkspaceA);
        var aRun = await BodyOf(await aOwner.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, aRecipeId), ValidBody(), cancellation));

        using var bOwner = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);
        var bRecipeId = await SeedRecipeAsync(bOwner, _fixture.WorkspaceB);
        await bOwner.PostAsJsonAsync(TestRunsIn(_fixture.WorkspaceB, bRecipeId), ValidBody(), cancellation);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var runId = aRun.GetProperty("testRunId").GetGuid();
        var stored = await db.RecipeTestRuns.IgnoreQueryFilters()
            .SingleAsync(run => run.Id == runId, cancellation);

        // Server-derived from the resolved route, never from the body — which has no field for it.
        Assert.Equal(_fixture.WorkspaceA.Id, stored.WorkspaceId);

        // And each workspace has exactly its own, so neither write leaked into the other's.
        Assert.Equal(
            1,
            await db.RecipeTestRuns.IgnoreQueryFilters()
                .CountAsync(run => run.WorkspaceId == _fixture.WorkspaceA.Id, cancellation));
        Assert.Equal(
            1,
            await db.RecipeTestRuns.IgnoreQueryFilters()
                .CountAsync(run => run.WorkspaceId == _fixture.WorkspaceB.Id, cancellation));
    }

    // ---- Idempotency ----

    [Fact]
    public async Task A_replayed_test_returns_the_original_response_and_records_one_run()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);
        var body = ValidBody();

        var first = await client.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId), body, "test-key-1", cancellation);
        var second = await client.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId), body, "test-key-1", cancellation);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.False(first.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal("true", second.Headers.GetValues(IdempotencyPolicy.ReplayedHeader).Single());
        Assert.Equal((await BodyOf(first)).GetRawText(), (await BodyOf(second)).GetRawText());

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // One record of one cook. Without the key a retry after a lost response would leave the creator with
        // two tests of the same bake and no way to tell which the retry made.
        Assert.Equal(
            1,
            await db.RecipeTestRuns.IgnoreQueryFilters().CountAsync(run => run.RecipeId == recipeId, cancellation));
    }

    [Fact]
    public async Task The_same_key_for_a_different_test_is_refused()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        var recipeId = await SeedRecipeAsync(client, _fixture.WorkspaceA);

        await client.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, testedAt = "2026-09-20T18:00:00Z", rating = 4 },
            "test-key-2",
            cancellation);

        var second = await client.PostAsJsonAsync(
            TestRunsIn(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, testedAt = "2026-09-20T18:00:00Z", rating = 2 },
            "test-key-2",
            cancellation);

        // Reported as key reuse rather than silently returning the first test's result — a creator who scored
        // the same bake differently the second time meant something by it.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, (await BodyOf(second)).GetProperty("code").GetString());
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
}
