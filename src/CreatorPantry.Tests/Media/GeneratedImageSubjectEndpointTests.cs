using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Media;

#pragma warning disable MEAI001

/// <summary>
/// What a run of pictures was asked for — a recipe, a version of it, a piece of work — through the real
/// Gateway and across two workspaces: recorded when it is this workspace's, refused before anything is queued
/// when it is not.
/// </summary>
/// <remarks>
/// The ids ground nothing; the worker sends the prompt as written. What is under test is that a run can be
/// traced to what asked for it, and that naming a neighbour's recipe or work costs nothing and says nothing.
/// </remarks>
public sealed class GeneratedImageSubjectEndpointTests : IAsyncLifetime
{
    private readonly FakeImageGenerator _generator = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(new InMemoryPrivateObjectStore());
            services.RemoveAll<IImageGenerator>();
            services.AddSingleton<IImageGenerator>(_generator);
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(new FakeMalwareScanGateway());
            services.AddGeneratedImageWorker();
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SeededWorkspace A => _fixture.WorkspaceA;

    private SeededWorkspace B => _fixture.WorkspaceB;

    private static string In(SeededWorkspace workspace, string path) => $"/api/v1/workspaces/{workspace.Slug}/{path}";

    [Fact]
    public async Task A_run_records_the_recipe_version_and_piece_of_work_it_was_asked_for()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var recipe = await RecipeAsync(client, A);
        var contextId = await ContextAsync(client, A);

        var response = await AskAsync(
            client, A, new { recipeId = recipe.Id, recipeVersionId = recipe.VersionId, creativeContextId = contextId });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await BodyOf(response);
        AssertSubject(accepted, recipe.Id, recipe.VersionId, contextId);

        var read = await BodyOf(await client.GetAsync(In(A, $"generated-images/operations/{accepted.GetProperty("id").GetGuid()}"), Ct));
        AssertSubject(read, recipe.Id, recipe.VersionId, contextId);
    }

    [Fact]
    public async Task A_run_asked_for_nothing_in_particular_records_nothing()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var response = await AskAsync(client, A, new { });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        AssertSubject(await BodyOf(response), null, null, null);
    }

    [Fact]
    public async Task A_recipe_alone_and_a_piece_of_work_alone_are_each_enough()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var recipe = await RecipeAsync(client, A);
        var contextId = await ContextAsync(client, A);

        AssertSubject(await BodyOf(await AskAsync(client, A, new { recipeId = recipe.Id })), recipe.Id, null, null);
        AssertSubject(await BodyOf(await AskAsync(client, A, new { creativeContextId = contextId })), null, null, contextId);
    }

    /// <summary>
    /// The neighbour's recipe is answered exactly as one that does not exist, and nothing is queued — so the
    /// worker has nothing to claim and nothing is charged.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_recipe_is_refused_as_an_unknown_one_and_queues_nothing()
    {
        using var ours = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var theirs = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);
        var theirRecipe = await RecipeAsync(theirs, B);

        var crossing = await AskAsync(ours, A, new { recipeId = theirRecipe.Id });
        var crossingVersion = await AskAsync(ours, A, new { recipeId = theirRecipe.Id, recipeVersionId = theirRecipe.VersionId });
        var missing = await AskAsync(ours, A, new { recipeId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossing.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossingVersion.StatusCode);

        var crossingBody = await BodyOf(crossing);
        Assert.Equal(MediaErrorCodes.GenerationRecipeUnprocessable, crossingBody.GetProperty("code").GetString());
        Assert.Equal(WithoutTraceId(await BodyOf(missing)), WithoutTraceId(crossingBody));
        Assert.Equal(MediaErrorCodes.GenerationRecipeUnprocessable, (await BodyOf(crossingVersion)).GetProperty("code").GetString());

        Assert.Equal(0, (await RunWorkerAsync()).Claimed);
        Assert.Equal(0, _generator.Calls);
    }

    /// <summary>
    /// Work about a recipe that has since been archived can still have pictures made for it: archiving puts a
    /// recipe away, it does not take it out of the workspace, and a run that named it must not be refused.
    /// </summary>
    [Fact]
    public async Task An_archived_recipe_can_still_be_what_a_run_is_for()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var recipe = await RecipeAsync(client, A);
        var token = (await BodyOf(await client.GetAsync(In(A, $"recipes/{recipe.Id}"), Ct))).GetProperty("concurrencyToken").GetString();
        var archived = await client.PostAsJsonAsync(In(A, $"recipes/{recipe.Id}/archive"), new { expectedConcurrencyToken = token }, Ct);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        var response = await AskAsync(client, A, new { recipeId = recipe.Id, recipeVersionId = recipe.VersionId });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    /// <summary>A version is only ever a version of the recipe it is named with.</summary>
    [Fact]
    public async Task A_version_of_some_other_recipe_is_refused()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var first = await RecipeAsync(client, A);
        var second = await RecipeAsync(client, A);

        var response = await AskAsync(client, A, new { recipeId = first.Id, recipeVersionId = second.VersionId });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(MediaErrorCodes.GenerationRecipeUnprocessable, (await BodyOf(response)).GetProperty("code").GetString());
        Assert.Equal(0, (await RunWorkerAsync()).Claimed);
    }

    [Fact]
    public async Task Another_workspaces_piece_of_work_is_refused_as_an_unknown_one_and_queues_nothing()
    {
        using var ours = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var theirs = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);
        var theirContext = await ContextAsync(theirs, B);

        var crossing = await AskAsync(ours, A, new { creativeContextId = theirContext });
        var missing = await AskAsync(ours, A, new { creativeContextId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossing.StatusCode);

        var crossingBody = await BodyOf(crossing);
        Assert.Equal(MediaErrorCodes.GenerationContextUnprocessable, crossingBody.GetProperty("code").GetString());
        Assert.Equal(WithoutTraceId(await BodyOf(missing)), WithoutTraceId(crossingBody));

        Assert.Equal(0, (await RunWorkerAsync()).Claimed);
        Assert.Equal(0, _generator.Calls);
    }

    [Fact]
    public async Task A_version_named_without_its_recipe_is_a_request_error()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var recipe = await RecipeAsync(client, A);

        var response = await AskAsync(client, A, new { recipeVersionId = recipe.VersionId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(MediaErrorCodes.GenerationInvalidRequest, (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>A key names one request, and what the request was for is part of what it was.</summary>
    [Fact]
    public async Task The_same_key_naming_a_different_piece_of_work_is_refused_and_queues_no_second_run()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var first = await ContextAsync(client, A);
        var second = await ContextAsync(client, A);

        var asked = await AskAsync(client, A, new { creativeContextId = first }, "run-1");
        var again = await AskAsync(client, A, new { creativeContextId = first }, "run-1");
        var different = await AskAsync(client, A, new { creativeContextId = second }, "run-1");

        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal((await BodyOf(asked)).GetProperty("id").GetGuid(), (await BodyOf(again)).GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);
        Assert.Equal(IdempotencyPolicy.KeyReusedCode, (await BodyOf(different)).GetProperty("code").GetString());
        Assert.Equal(1, (await RunWorkerAsync()).Claimed);
    }

    /// <summary>What a run was for is the workspace's to read and nobody else's.</summary>
    [Fact]
    public async Task What_a_run_was_for_is_not_readable_from_another_workspace()
    {
        using var ours = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var theirs = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);
        var recipe = await RecipeAsync(ours, A);
        var operationId = (await BodyOf(await AskAsync(ours, A, new { recipeId = recipe.Id }))).GetProperty("id").GetGuid();

        var response = await theirs.GetAsync(In(B, $"generated-images/operations/{operationId}"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static void AssertSubject(JsonElement run, Guid? recipeId, Guid? recipeVersionId, Guid? contextId)
    {
        Assert.Equal(recipeId, IdOf(run, "recipeId"));
        Assert.Equal(recipeVersionId, IdOf(run, "recipeVersionId"));
        Assert.Equal(contextId, IdOf(run, "creativeContextId"));
    }

    private static Guid? IdOf(JsonElement run, string name) =>
        run.GetProperty(name).ValueKind is JsonValueKind.Null ? null : run.GetProperty(name).GetGuid();

    /// <summary>Asks for one picture, with whatever the run is said to be for merged into the request.</summary>
    private static Task<HttpResponseMessage> AskAsync(
        GatewayClient client, SeededWorkspace workspace, object subject, string? idempotencyKey = null)
    {
        var body = JsonSerializer.SerializeToNode(subject)!.AsObject();
        body["promptText"] = "A bowl of soup on a wooden table.";
        body["variantCount"] = 1;

        return client.PostAsJsonAsync(In(workspace, "generated-images"), body, idempotencyKey ?? Guid.NewGuid().ToString("N"), Ct);
    }

    private sealed record SeededRecipe(Guid Id, Guid VersionId);

    private static async Task<SeededRecipe> RecipeAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var created = await client.PostAsJsonAsync(In(workspace, "recipes"), new { title = "Soda bread" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), Ct));

        return new SeededRecipe(detail.GetProperty("id").GetGuid(), detail.GetProperty("currentVersion").GetProperty("id").GetGuid());
    }

    private static async Task<Guid> ContextAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.PostAsJsonAsync(In(workspace, "creative-contexts"), new { workingTitle = "Soup, Sunday" }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private async Task<GeneratedImagePassSummary> RunWorkerAsync()
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IGeneratedImageWorker>().RunPendingAsync(Ct);
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <inheritdoc cref="Ai.PhotographyConceptIsolationEndpointTests"/>
    private static string WithoutTraceId(JsonElement body)
    {
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node.Remove("traceId");

        return node.ToJsonString();
    }
}
