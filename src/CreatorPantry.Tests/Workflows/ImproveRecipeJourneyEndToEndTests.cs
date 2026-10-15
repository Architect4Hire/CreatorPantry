using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// "Improve a recipe" end to end through the real Gateway (13A.15), in the order the guided screens send it: start
/// from a recipe, find the suggestions waiting on it as a reload does, refuse a stale one, reject one, accept one,
/// finish when every suggestion is answered, and carry on to testing.
/// </summary>
/// <remarks>
/// Suggestions are seeded rather than generated — producing one needs a worker and a model, neither of which this is
/// about — and decided through the recipe page's own disposition route, which is what the step's panel calls.
/// </remarks>
public sealed class ImproveRecipeJourneyEndToEndTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string RunsIn => $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/workflow-runs";

    private string RecipesIn => $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task A_creator_answers_every_suggestion_keeps_the_one_they_want_and_carries_on_to_testing()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var (recipeId, firstVersion) = await CreateRecipeAsync(client);

        // ---- Started from the recipe: choosing it is already done ----
        var started = await client.PostAsJsonAsync(
            RunsIn, new { definitionKey = "recipe.improve", startingResource = new { kind = "Recipe", id = recipeId } }, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var runId = (await BodyOf(started)).GetProperty("id").GetGuid();
        Assert.Equal("change", NextStep(await ReadAsync(client, runId)));

        // ---- A suggestion asked for earlier, and one asked for now: both waiting, both found after a reload ----
        var stale = await SeedSuggestionAsync(recipeId, firstVersion, "A sharper headnote.", minutesAgo: 30);
        var open = await OpenAsync(client, recipeId);
        Assert.Equal([stale.RequestId], open.Select(item => item.GetProperty("requestId").GetGuid()));
        Assert.False(open[0].GetProperty("isStale").GetBoolean());

        // Nothing saved during the run yet, so the step is not done.
        var early = await CompleteAsync(client, runId, "change");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        Assert.Equal(WorkflowErrorCodes.StepIncomplete, (await BodyOf(early)).GetProperty("code").GetString());

        // ---- The creator's own save moves the recipe on: the earlier suggestion is now stale ----
        var secondVersion = await EditRecipeAsync(client, recipeId, new { notes = "Better the next day." });
        open = await OpenAsync(client, recipeId);
        Assert.True(Assert.Single(open).GetProperty("isStale").GetBoolean());

        var refused = await DecideAsync(client, recipeId, stale, accept: true);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // ---- Asked again against the recipe as it is, and kept ----
        var fresh = await SeedSuggestionAsync(recipeId, secondVersion, "A warmer headnote.", minutesAgo: 1);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(client, recipeId, fresh, accept: true)).StatusCode);
        Assert.Equal("A warmer headnote.", (await RecipeAsync(client, recipeId)).GetProperty("headnote").GetString());

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, runId, "change")).StatusCode);
        Assert.Equal("decide", NextStep(await ReadAsync(client, runId)));

        // ---- Decide: the stale one is still waiting, so the step is not done until it is answered ----
        Assert.Equal([stale.RequestId], (await OpenAsync(client, recipeId)).Select(item => item.GetProperty("requestId").GetGuid()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(client, runId, "decide")).StatusCode);

        // A stale suggestion can still be said no to.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(client, recipeId, stale, accept: false)).StatusCode);
        Assert.Empty(await OpenAsync(client, recipeId));
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, runId, "decide")).StatusCode);

        // ---- Compare and try sizes are optional: done on the creator's word ----
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, runId, "compare")).StatusCode);
        var afterSizes = await BodyOf(await CompleteAsync(client, runId, "try-sizes"));
        Assert.True(afterSizes.GetProperty("readyToComplete").GetBoolean());

        // ---- Finish, and carry on to testing the improved recipe ----
        var finished = await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct);
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        var next = (await BodyOf(finished)).GetProperty("nextSuggestions")[0];
        Assert.Equal(WorkflowCatalogue.RecipeTestAndApproveKey, next.GetProperty("key").GetString());
        Assert.Equal(recipeId, next.GetProperty("startFromId").GetGuid());

        // Every version is kept: the first, the creator's save, and the accepted suggestion.
        var history = await BodyOf(await client.GetAsync($"{RecipesIn}/{recipeId}/versions", Ct));
        Assert.Equal(3, history.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Another_workspace_sees_none_of_the_suggestions_waiting_on_this_recipe()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var (recipeId, version) = await CreateRecipeAsync(owner);
        await SeedSuggestionAsync(recipeId, version, "A sharper headnote.", minutesAgo: 1);

        using var neighbour = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var theirs = await neighbour.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/recipes/{recipeId}/ai-proposals/open", Ct);
        var unknown = await neighbour.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/recipes/{Guid.NewGuid()}/ai-proposals/open", Ct);
        var through = await neighbour.GetAsync($"{RecipesIn}/{recipeId}/ai-proposals/open", Ct);

        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, through.StatusCode);

        // Indistinguishable from a recipe that does not exist: the same code and the same words.
        var theirBody = await BodyOf(theirs);
        var unknownBody = await BodyOf(unknown);
        Assert.Equal(unknownBody.GetProperty("code").GetString(), theirBody.GetProperty("code").GetString());
        Assert.Equal(unknownBody.GetProperty("title").GetString(), theirBody.GetProperty("title").GetString());
    }

    // ---- Helpers ----

    private sealed record Suggestion(Guid RequestId, Guid ChangeId);

    private static string? NextStep(JsonElement run) =>
        run.GetProperty("nextAction").ValueKind == JsonValueKind.Null
            ? null
            : run.GetProperty("nextAction").GetProperty("stepKey").GetString();

    private async Task<JsonElement> ReadAsync(GatewayClient client, Guid runId) =>
        await BodyOf(await client.GetAsync($"{RunsIn}/{runId}", Ct));

    private async Task<string> TokenAsync(GatewayClient client, Guid runId) =>
        (await ReadAsync(client, runId)).GetProperty("concurrencyToken").GetString()!;

    private async Task<HttpResponseMessage> CompleteAsync(GatewayClient client, Guid runId, string stepKey) =>
        await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/{stepKey}/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct);

    private async Task<List<JsonElement>> OpenAsync(GatewayClient client, Guid recipeId)
    {
        var response = await client.GetAsync($"{RecipesIn}/{recipeId}/ai-proposals/open", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await BodyOf(response)).EnumerateArray()];
    }

    private Task<HttpResponseMessage> DecideAsync(GatewayClient client, Guid recipeId, Suggestion suggestion, bool accept) =>
        client.PostAsJsonAsync(
            $"{RecipesIn}/{recipeId}/ai-proposals/{suggestion.RequestId}/disposition",
            accept
                ? new { decision = nameof(AiDispositionDecision.AcceptAll), acceptedChangeIds = new[] { suggestion.ChangeId } }
                : (object)new { decision = nameof(AiDispositionDecision.Reject) },
            Ct);

    private async Task<JsonElement> RecipeAsync(GatewayClient client, Guid recipeId) =>
        await BodyOf(await client.GetAsync($"{RecipesIn}/{recipeId}", Ct));

    private async Task<(Guid RecipeId, Guid VersionId)> CreateRecipeAsync(GatewayClient client)
    {
        var created = await client.PostAsJsonAsync(
            RecipesIn,
            new { title = "Olive oil cake", headnote = "The one my grandmother made." },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), Ct));

        return (detail.GetProperty("id").GetGuid(), detail.GetProperty("currentVersion").GetProperty("id").GetGuid());
    }

    /// <summary>Saves the recipe the way the hosted editor does, and answers the version it now stands at.</summary>
    private async Task<Guid> EditRecipeAsync(GatewayClient client, Guid recipeId, object fields)
    {
        var path = $"{RecipesIn}/{recipeId}";
        var token = (await BodyOf(await client.GetAsync(path, Ct))).GetProperty("concurrencyToken").GetString();
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
        body["expectedConcurrencyToken"] = JsonSerializer.SerializeToElement(token);

        var response = await client.PatchAsJsonAsync(path, body, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await BodyOf(response)).GetProperty("currentVersion").GetProperty("id").GetGuid();
    }

    /// <summary>A proposed headnote change against one version, as a scoped revision would leave it.</summary>
    private async Task<Suggestion> SeedSuggestionAsync(Guid recipeId, Guid versionId, string headnote, int minutesAgo)
    {
        var requestId = Guid.NewGuid();
        var changeId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            _fixture.WorkspaceA.Id, _fixture.WorkspaceA.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AiOperations.Add(new AiOperation
        {
            Id = requestId,
            WorkspaceId = _fixture.WorkspaceA.Id,
            TaskType = AiTaskType.Diagnostic,
            Scope = AiOperationScope.WholeRecipe,
            Status = AiOperationStatus.Proposed,
            RecipeId = recipeId,
            RecipeVersionId = versionId,
            IdempotencyKey = $"seed-{requestId:N}",
            RequestedByMembershipId = Guid.NewGuid(),
            RequestedAt = at,
            StatusChangedAt = at,
            AvailableAt = at,
            StartedAt = at,
        });

        var proposal = new AiProposal
        {
            Id = Guid.NewGuid(),
            WorkspaceId = _fixture.WorkspaceA.Id,
            AiOperationId = requestId,
            SourceRecipeVersionId = versionId,
            OutputSchemaVersion = "fixture.diagnostic.v1",
            PromptTemplateId = "fixture.diagnostic",
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:seed",
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = at,
        };
        proposal.Changes.Add(new AiStructuredChange
        {
            Id = changeId,
            WorkspaceId = _fixture.WorkspaceA.Id,
            ChangeKind = AiChangeKind.Set,
            TargetKind = AiChangeTargetKind.Recipe,
            FieldName = "headnote",
            BeforeValue = "The one my grandmother made.",
            AfterValue = headnote,
            SortOrder = 0,
            Disposition = AiChangeDisposition.Pending,
        });
        db.AiProposals.Add(proposal);
        await db.SaveChangesAsync(Ct);

        return new Suggestion(requestId, changeId);
    }
}
