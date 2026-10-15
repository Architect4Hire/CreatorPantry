using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// "Test and approve" end to end through the real Gateway (13A.16), in the order the guided screens send it: fill in
/// the basics, record a failed test with a blocking issue, fix the recipe and close the issue against the version
/// that fixed it, cook it again, walk it to review one move at a time, have a Contributor refused the approval and an
/// Editor give it, and only then finish.
/// </summary>
/// <remarks>
/// Every move here is a request the step's own screen makes — the editor's save, the Test kitchen's record and
/// resolution, the readiness panel's transitions. The workflow is asked only whether each step is done, and never
/// moves the recipe itself.
/// </remarks>
public sealed class TestAndApproveJourneyEndToEndTests : IAsyncLifetime
{
    private const string ContributorEmail = "test-approve-contributor-a@example.com";

    private const string EditorEmail = "test-approve-editor-a@example.com";

    private const string Password = "correct horse battery";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        foreach (var (email, role) in new[] { (ContributorEmail, WorkspaceRole.Contributor), (EditorEmail, WorkspaceRole.Editor) })
        {
            db.WorkspaceMemberships.Add(new WorkspaceMembership
            {
                Id = Guid.NewGuid(),
                WorkspaceId = _fixture.WorkspaceA.Id,
                UserId = await _fixture.Api.CreateUserAsync(email, Password),
                Role = role,
                Status = WorkspaceMembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string RunsIn => $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/workflow-runs";

    private string RecipesIn => $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task A_recipe_that_failed_its_first_test_is_fixed_tested_again_reviewed_and_approved_by_an_editor()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateRecipeAsync(owner);

        var started = await owner.PostAsJsonAsync(
            RunsIn, new { definitionKey = "recipe.test-and-approve", startingResource = new { kind = "Recipe", id = recipeId } }, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var runId = (await BodyOf(started)).GetProperty("id").GetGuid();
        Assert.Equal("basics", NextStep(await ReadRunAsync(owner, runId)));

        // ---- Basics: refused while anything a test needs is missing ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, runId, "basics")).StatusCode);
        await EditRecipeAsync(owner, recipeId, new
        {
            yieldText = "One loaf",
            prepTimeMinutes = 10,
            cookTimeMinutes = 40,
            ingredientGroups = new[] { new { ingredients = new[] { new { displayText = "450 g plain flour" } } } },
            instructions = new[] { new { steps = new[] { new { text = "Bake for 40 minutes." } } } },
        });
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, runId, "basics")).StatusCode);

        // ---- Cook: not done until the current version has a test ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, runId, "cook")).StatusCode);
        var firstTest = await RecordTestAsync(owner, recipeId, "Failed", blockingIssue: "Far too dense");
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, runId, "cook")).StatusCode);

        // ---- Fix: a failed test and an open blocking issue each keep it from being done ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, runId, "fix")).StatusCode);

        // The correction is a new version; the issue is closed against it, and the observation is never rewritten.
        await EditRecipeAsync(owner, recipeId, new { notes = "Less soda, more buttermilk." });
        var fixedIn = await VersionNumberAsync(owner, recipeId);
        var resolved = await owner.PostAsJsonAsync(
            $"{RecipesIn}/{recipeId}/test-runs/{firstTest.RunId}/issues/{firstTest.IssueId}/resolution",
            new { kind = "Fixed", resolutionVersionNumber = fixedIn, notes = "Rebalanced the leavening." },
            Ct);
        Assert.Equal(HttpStatusCode.Created, resolved.StatusCode);

        // Fixed and closed: what failed was the version before, so this step is done. The fixed version is untested,
        // which the recipe's readiness says — and which approval will not get past until it is cooked again.
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, runId, "fix")).StatusCode);
        var readiness = await BodyOf(await owner.GetAsync($"{RecipesIn}/{recipeId}/readiness", Ct));
        Assert.Contains(
            readiness.GetProperty("findings").EnumerateArray(),
            finding => finding.GetProperty("ruleId").GetString() == "recipe.testing.currentVersionUntested"
                && finding.GetProperty("status").GetString() == "Blocker");

        // ---- Send for review: one move at a time, and the step is done only on the server's status ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, runId, "send-for-review")).StatusCode);
        foreach (var target in new[] { "InDevelopment", "Testing" })
        {
            Assert.Equal(HttpStatusCode.OK, (await MoveAsync(owner, recipeId, target)).StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, runId, "send-for-review")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(owner, recipeId, "ReadyForReview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, runId, "send-for-review")).StatusCode);

        // ---- Approve: a Contributor is refused by the recipe, and the step stays open ----
        using var contributor = await _fixture.SignInAsync(ContributorEmail, Password, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await MoveAsync(contributor, recipeId, "Approved", "Looks right to me.")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, runId, "approve")).StatusCode);
        Assert.Equal("ReadyForReview", (await RecipeAsync(owner, recipeId)).GetProperty("status").GetString());

        // An Editor's approval re-checks readiness: refused while the fixed version is untested, so no progress waives it.
        using var editor = await _fixture.SignInAsync(EditorEmail, Password, Ct);
        var blocked = await MoveAsync(editor, recipeId, "Approved", "Looks right.");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(RecipeErrorCodes.TransitionBlockedConflict, (await BodyOf(blocked)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, runId, "approve")).StatusCode);

        // Cooked again and right: the approval is the domain's move, and only after it lands is the step done.
        await RecordTestAsync(owner, recipeId, "Succeeded");
        Assert.Equal(HttpStatusCode.OK, (await MoveAsync(editor, recipeId, "Approved", "Cooked twice, right the second time.")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, runId, "approve")).StatusCode);

        var finished = await owner.PostAsJsonAsync(
            $"{RunsIn}/{runId}/completion", new { expectedConcurrencyToken = await TokenAsync(owner, runId) }, Ct);
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal("Completed", (await BodyOf(finished)).GetProperty("status").GetString());
        Assert.Equal("Approved", (await RecipeAsync(owner, recipeId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Another_workspace_can_neither_read_the_run_nor_move_its_recipe()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateRecipeAsync(owner);
        var started = await owner.PostAsJsonAsync(
            RunsIn, new { definitionKey = "recipe.test-and-approve", startingResource = new { kind = "Recipe", id = recipeId } }, Guid.NewGuid().ToString(), Ct);
        var runId = (await BodyOf(started)).GetProperty("id").GetGuid();

        using var neighbour = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var theirs = $"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}";

        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync($"{theirs}/workflow-runs/{runId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync($"{theirs}/recipes/{recipeId}/readiness", Ct)).StatusCode);
        var moved = await neighbour.PostAsJsonAsync(
            $"{theirs}/recipes/{recipeId}/readiness-transitions",
            new { targetStatus = "InDevelopment", reason = (string?)null, expectedConcurrencyToken = "AAAAAAAAB9E=" },
            Guid.NewGuid().ToString("N"),
            Ct);
        Assert.Equal(HttpStatusCode.NotFound, moved.StatusCode);
        Assert.Equal("Draft", (await RecipeAsync(owner, recipeId)).GetProperty("status").GetString());
    }

    // ---- Helpers ----

    private sealed record RecordedTest(Guid RunId, Guid IssueId);

    private static string? NextStep(JsonElement run) =>
        run.GetProperty("nextAction").ValueKind == JsonValueKind.Null
            ? null
            : run.GetProperty("nextAction").GetProperty("stepKey").GetString();

    private async Task<JsonElement> ReadRunAsync(GatewayClient client, Guid runId) =>
        await BodyOf(await client.GetAsync($"{RunsIn}/{runId}", Ct));

    private async Task<string> TokenAsync(GatewayClient client, Guid runId) =>
        (await ReadRunAsync(client, runId)).GetProperty("concurrencyToken").GetString()!;

    private async Task<HttpResponseMessage> CompleteAsync(GatewayClient client, Guid runId, string stepKey) =>
        await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/{stepKey}/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct);

    private async Task<JsonElement> RecipeAsync(GatewayClient client, Guid recipeId) =>
        await BodyOf(await client.GetAsync($"{RecipesIn}/{recipeId}", Ct));

    private async Task<int> VersionNumberAsync(GatewayClient client, Guid recipeId) =>
        (await RecipeAsync(client, recipeId)).GetProperty("currentVersion").GetProperty("versionNumber").GetInt32();

    private async Task<Guid> CreateRecipeAsync(GatewayClient client)
    {
        var response = await client.PostAsJsonAsync(RecipesIn, new { title = "Soda bread" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    private async Task EditRecipeAsync(GatewayClient client, Guid recipeId, object fields)
    {
        var token = (await RecipeAsync(client, recipeId)).GetProperty("concurrencyToken").GetString();
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
        body["expectedConcurrencyToken"] = JsonSerializer.SerializeToElement(token);

        var response = await client.PatchAsJsonAsync($"{RecipesIn}/{recipeId}", body, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Records a test of the current version, as the Test kitchen's form does.</summary>
    private async Task<RecordedTest> RecordTestAsync(GatewayClient client, Guid recipeId, string outcome, string? blockingIssue = null)
    {
        var response = await client.PostAsJsonAsync(
            $"{RecipesIn}/{recipeId}/test-runs",
            new
            {
                sourceVersionNumber = await VersionNumberAsync(client, recipeId),
                testedAt = DateTimeOffset.UtcNow.ToString("O"),
                outcome,
                observations = blockingIssue is null ? Array.Empty<object>() : new object[] { new { kind = "Texture", text = "Heavy and close." } },
                issues = blockingIssue is null
                    ? Array.Empty<object>()
                    : new object[] { new { severity = "Blocking", title = blockingIssue, observationIndex = 0 } },
            },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await BodyOf(response);
        var issues = body.GetProperty("issueIds").EnumerateArray().ToList();
        return new RecordedTest(body.GetProperty("testRunId").GetGuid(), issues.Count > 0 ? issues[0].GetGuid() : Guid.Empty);
    }

    /// <summary>A readiness transition, as the readiness panel sends it: the version read, a reason, an idempotency key.</summary>
    private async Task<HttpResponseMessage> MoveAsync(GatewayClient client, Guid recipeId, string target, string? reason = null)
    {
        var token = (await RecipeAsync(client, recipeId)).GetProperty("concurrencyToken").GetString();
        return await client.PostAsJsonAsync(
            $"{RecipesIn}/{recipeId}/readiness-transitions",
            new { targetStatus = target, reason, expectedConcurrencyToken = token },
            Guid.NewGuid().ToString("N"),
            Ct);
    }
}
