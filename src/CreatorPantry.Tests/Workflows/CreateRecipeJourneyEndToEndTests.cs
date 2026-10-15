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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// "Create a recipe" end to end through the real Gateway (13A.14), in the order the guided screens send it: start,
/// keep each step's draft as the work appears, read the run back as a reload does, complete each step once the recipe
/// says it is done, go Back and come forward again, finish, and start what it suggests next from the recipe it made.
/// </summary>
/// <remarks>
/// The screens add nothing the server does not decide — each request here is one a screen makes, so this is the
/// journey the creator takes with the browser left out.
/// </remarks>
public sealed class CreateRecipeJourneyEndToEndTests : IAsyncLifetime
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
    public async Task A_creator_writes_a_recipe_step_by_step_comes_back_to_it_and_carries_on_to_testing_it()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        // ---- Start ----
        var started = await client.PostAsJsonAsync(RunsIn, new { definitionKey = "recipe.create" }, Guid.NewGuid().ToString(), Ct);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var runId = (await BodyOf(started)).GetProperty("id").GetGuid();
        Assert.Equal("start", NextStep(await ReadAsync(client, runId)));

        // ---- "Help me draft it", then a change of mind: each request is kept as it is made ----
        var ideas = await SeedConceptRequestAsync();
        await KeepDraftAsync(client, runId, "start", "AiRequest", ideas);

        // A reload: the step opens on what it was waiting on.
        var reloaded = await ReadAsync(client, runId);
        Assert.Equal("start", NextStep(reloaded));
        Assert.Equal("AiRequest", Step(reloaded, "start").GetProperty("draftKind").GetString());
        Assert.Equal(ideas, Step(reloaded, "start").GetProperty("draftResourceId").GetGuid());

        // ---- "Write it yourself": the editor's first save makes the recipe, and the run is pointed at it ----
        var recipeId = await CreateRecipeAsync(client, "Soda bread");
        await KeepDraftAsync(client, runId, "start", "Recipe", recipeId);
        Assert.Equal(recipeId, Step(await ReadAsync(client, runId), "start").GetProperty("draftResourceId").GetGuid());

        var afterStart = await CompleteAsync(client, runId, "start", [new { kind = "Recipe", id = recipeId }]);
        Assert.Equal(HttpStatusCode.OK, afterStart.StatusCode);
        var run = await BodyOf(afterStart);
        Assert.Equal("ingredients", NextStep(run));
        Assert.Contains(run.GetProperty("links").EnumerateArray(), link =>
            link.GetProperty("kind").GetString() == "Recipe" && link.GetProperty("resourceId").GetGuid() == recipeId);

        // ---- Ingredients: not done until the recipe has some ----
        var early = await CompleteAsync(client, runId, "ingredients");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        Assert.Equal(WorkflowErrorCodes.StepIncomplete, (await BodyOf(early)).GetProperty("code").GetString());

        await EditRecipeAsync(client, recipeId, new
        {
            ingredientGroups = new[] { new { ingredients = new[] { new { displayText = "450 g plain flour" }, new { displayText = "1 tsp bicarbonate of soda" } } } },
        });
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, runId, "ingredients")).StatusCode);

        // ---- Back to the start, and forward again on the recipe it already made: never a second recipe ----
        var back = await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/method/back", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal("ingredients", NextStep(await BodyOf(back)));
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, runId, "ingredients")).StatusCode);

        // ---- Method, then yield and time ----
        await EditRecipeAsync(client, recipeId, new
        {
            instructions = new[] { new { steps = new[] { new { text = "Mix the dry ingredients." }, new { text = "Bake for 40 minutes." } } } },
        });
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(client, runId, "method")).StatusCode);

        var tooSoon = await CompleteAsync(client, runId, "yield-and-time");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooSoon.StatusCode);

        await EditRecipeAsync(client, recipeId, new { yieldText = "One loaf", servingCount = 8, prepTimeMinutes = 10, cookTimeMinutes = 40, totalTimeMinutes = 50 });
        var afterTime = await BodyOf(await CompleteAsync(client, runId, "yield-and-time"));
        Assert.Equal("try-sizes", NextStep(afterTime));

        // ---- Trying another size is optional and changes nothing: done on the creator's word ----
        var afterSizes = await BodyOf(await CompleteAsync(client, runId, "try-sizes"));
        Assert.Equal(JsonValueKind.Null, afterSizes.GetProperty("nextAction").ValueKind);
        Assert.True(afterSizes.GetProperty("readyToComplete").GetBoolean());

        // ---- Finish, and what comes next ----
        var finished = await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/completion", new { expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct);
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        var done = await BodyOf(finished);
        Assert.Equal("Completed", done.GetProperty("status").GetString());

        var next = done.GetProperty("nextSuggestions")[0];
        Assert.Equal(WorkflowCatalogue.RecipeTestAndApproveKey, next.GetProperty("key").GetString());
        Assert.Equal(recipeId, next.GetProperty("startFromId").GetGuid());

        var testing = await client.PostAsJsonAsync(
            RunsIn,
            new { definitionKey = next.GetProperty("key").GetString(), startingResource = new { kind = next.GetProperty("startFromKind").GetString(), id = recipeId } },
            Guid.NewGuid().ToString(),
            Ct);
        Assert.Equal(HttpStatusCode.Created, testing.StatusCode);
        var testRun = await BodyOf(testing);
        Assert.Equal("Completed", Step(testRun, "choose").GetProperty("status").GetString());

        // One recipe, made once, with the creator's words exactly as they wrote them.
        var recipe = await BodyOf(await client.GetAsync($"{RecipesIn}/{recipeId}", Ct));
        Assert.Equal("450 g plain flour", recipe.GetProperty("ingredientGroups")[0].GetProperty("ingredients")[0].GetProperty("displayText").GetString());
        Assert.Equal(1, await InWorkspaceAsync(db => db.Recipes.CountAsync(Ct)));
    }

    [Fact]
    public async Task Another_workspace_cannot_read_or_move_the_journey_or_point_it_at_its_own_recipe()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var started = await owner.PostAsJsonAsync(RunsIn, new { definitionKey = "recipe.create" }, Guid.NewGuid().ToString(), Ct);
        var runId = (await BodyOf(started)).GetProperty("id").GetGuid();
        var token = await TokenAsync(owner, runId);

        using var neighbour = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var theirRunsIn = $"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/workflow-runs";

        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync($"{theirRunsIn}/{runId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync($"{RunsIn}/{runId}", Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await neighbour.PutAsJsonAsync($"{theirRunsIn}/{runId}/steps/start/draft", new { kind = "Recipe", id = Guid.NewGuid(), expectedConcurrencyToken = token }, Ct)).StatusCode);

        // The neighbour's recipe, named on this run by its own owner, is answered as an unknown one.
        var theirs = await neighbour.PostAsJsonAsync($"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/recipes", new { title = "Their loaf" }, Ct);
        var theirRecipe = (await BodyOf(theirs)).GetProperty("recipeId").GetGuid();
        var pointed = await owner.PutAsJsonAsync(
            $"{RunsIn}/{runId}/steps/start/draft", new { kind = "Recipe", id = theirRecipe, expectedConcurrencyToken = token }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, pointed.StatusCode);
        Assert.Equal(JsonValueKind.Null, Step(await ReadAsync(owner, runId), "start").GetProperty("draftKind").ValueKind);
    }

    // ---- Helpers ----

    private static string? NextStep(JsonElement run) =>
        run.GetProperty("nextAction").ValueKind == JsonValueKind.Null
            ? null
            : run.GetProperty("nextAction").GetProperty("stepKey").GetString();

    private static JsonElement Step(JsonElement run, string key) =>
        run.GetProperty("steps").EnumerateArray().Single(step => step.GetProperty("key").GetString() == key);

    private async Task<JsonElement> ReadAsync(GatewayClient client, Guid runId) =>
        await BodyOf(await client.GetAsync($"{RunsIn}/{runId}", Ct));

    private async Task<string> TokenAsync(GatewayClient client, Guid runId) =>
        (await ReadAsync(client, runId)).GetProperty("concurrencyToken").GetString()!;

    private async Task KeepDraftAsync(GatewayClient client, Guid runId, string stepKey, string kind, Guid id)
    {
        var response = await client.PutAsJsonAsync(
            $"{RunsIn}/{runId}/steps/{stepKey}/draft", new { kind, id, expectedConcurrencyToken = await TokenAsync(client, runId) }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> CompleteAsync(GatewayClient client, Guid runId, string stepKey, object[]? results = null) =>
        await client.PostAsJsonAsync(
            $"{RunsIn}/{runId}/steps/{stepKey}/completion",
            new { results, expectedConcurrencyToken = await TokenAsync(client, runId) },
            Ct);

    private async Task<Guid> CreateRecipeAsync(GatewayClient client, string title)
    {
        var response = await client.PostAsJsonAsync(RecipesIn, new { title }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    /// <summary>Saves the recipe the way the hosted editor does: its own route, quoting the token it read.</summary>
    private async Task EditRecipeAsync(GatewayClient client, Guid recipeId, object fields)
    {
        var path = $"{RecipesIn}/{recipeId}";
        var token = (await BodyOf(await client.GetAsync(path, Ct))).GetProperty("concurrencyToken").GetString();
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
        body["expectedConcurrencyToken"] = JsonSerializer.SerializeToElement(token);

        var response = await client.PatchAsJsonAsync(path, body, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A request for recipe ideas, as "Help me draft it" makes one, without paying a model for it.</summary>
    private Task<Guid> SeedConceptRequestAsync() =>
        InWorkspaceAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var operation = new AiOperation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = _fixture.WorkspaceA.Id,
                TaskType = AiTaskType.RecipeConcepts,
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                TaskInputsJson = "{}",
                RequestedByMembershipId = Guid.NewGuid(),
                RequestedAt = now,
                StatusChangedAt = now,
                AvailableAt = now,
            };
            db.AiOperations.Add(operation);
            await db.SaveChangesAsync(Ct);

            return operation.Id;
        });

    private async Task<T> InWorkspaceAsync<T>(Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            _fixture.WorkspaceA.Id, _fixture.WorkspaceA.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");

        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }
}
