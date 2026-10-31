using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Media;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Media;
using CreatorPantry.Tests.Storage;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Workflows;

#pragma warning disable MEAI001

/// <summary>
/// "Create food images" end to end through the real Gateway (13A.19), in the order the guided screens send it: a run
/// started from a recipe, the piece of work its first step files and keeps on the run, a plan passed through, a run
/// of pictures that comes back short, the one that arrived kept to the library on that work, and that picture put at
/// the top of the recipe — and a neighbouring workspace that reaches none of it.
/// </summary>
/// <remarks>
/// <para>
/// The screens are the Image Studio's own panels, hosted, so every request here is one the studio already sends:
/// the piece of work, the picture request naming it, the worker's pass, the keep that names the asset on the work, and
/// the recipe's own link command. The workflow is only ever asked whether a step is done.
/// </para>
/// <para>
/// Stubbed, as <see cref="CreativePipelineEndToEndTests"/> stubs them: the image provider, the object store and the
/// malware scanner. The worker is run by hand, once — the API host has no queue driver, by design.
/// </para>
/// </remarks>
public sealed partial class ImagesJourneyEndToEndTests : IAsyncLifetime
{
    private const string Prompt = "Overhead shot of a plum galette on a linen cloth, soft window light.";

    private readonly InMemoryPrivateObjectStore _store = new();
    private readonly FakeImageGenerator _generator = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IPrivateObjectStore>();
            services.AddSingleton<IPrivateObjectStore>(_store);
            services.RemoveAll<IImageGenerator>();
            services.AddSingleton<IImageGenerator>(_generator);
            services.RemoveAll<IMalwareScanGateway>();
            services.AddSingleton<IMalwareScanGateway>(new FakeMalwareScanGateway());

            // The queue driver the Worker host adds and the API host deliberately does not.
            services.AddGeneratedImageWorker();
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SeededWorkspace A => _fixture.WorkspaceA;

    private SeededWorkspace B => _fixture.WorkspaceB;

    private static string In(SeededWorkspace workspace, string path) => $"/api/v1/workspaces/{workspace.Slug}/{path}";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task A_picture_is_planned_made_kept_and_put_on_its_recipe_and_the_neighbour_reaches_none_of_it()
    {
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var recipe = await CreateRecipeAsync(owner, A);

        // ---- Started from the recipe, as "What next?" and the hub start it ----
        var started = await owner.PostAsJsonAsync(
            In(A, "workflow-runs"),
            new { definitionKey = "images.create", startingResource = new { kind = "Recipe", id = recipe.Id } },
            Guid.NewGuid().ToString(),
            Ct);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var runId = (await BodyOf(started)).GetProperty("id").GetGuid();
        Assert.Equal("about", NextStep(await ReadRunAsync(owner, A, runId)));

        // ---- 1. What the picture is for: nothing filed is nothing done ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, A, runId, "about")).StatusCode);

        // The first answer files the work, the recipe offered is linked by the creator's choice, and the step keeps
        // the work on the run straight away — which is what a reload or another device opens on.
        var contextId = await ContextAsync(owner, A, "Plum galette");
        await AddRecipeAsync(owner, A, contextId, recipe.Id);
        var kept = await owner.PutAsJsonAsync(
            In(A, $"workflow-runs/{runId}/steps/about/draft"),
            new { kind = "CreativeContext", id = contextId, expectedConcurrencyToken = await TokenAsync(owner, A, runId) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        var resumed = Step(await ReadRunAsync(owner, A, runId), "about");
        Assert.Equal("CreativeContext", resumed.GetProperty("draftKind").GetString());
        Assert.Equal(contextId, resumed.GetProperty("draftResourceId").GetGuid());

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "about", [new { kind = "CreativeContext", id = contextId }])).StatusCode);
        Assert.Equal("plan", NextStep(await ReadRunAsync(owner, A, runId)));

        // Save and exit on a later step names no draft — its actions keep none — and the run still holds the work.
        var exited = await owner.PostAsJsonAsync(
            In(A, $"workflow-runs/{runId}/steps/plan/save-and-exit"),
            new { draft = (object?)null, capabilityKey = (string?)null, expectedConcurrencyToken = await TokenAsync(owner, A, runId) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, exited.StatusCode);
        Assert.Contains((await ReadRunAsync(owner, A, runId)).GetProperty("links").EnumerateArray(),
            link => link.GetProperty("kind").GetString() == "CreativeContext" && link.GetProperty("resourceId").GetGuid() == contextId);

        // ---- 2. Planning is the creator's own word: a prompt kept on their device needs nothing on the server ----
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "plan")).StatusCode);
        Assert.Equal("make-and-keep", NextStep(await ReadRunAsync(owner, A, runId)));

        // ---- 3. Make and keep: nothing kept is nothing done ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, A, runId, "make-and-keep")).StatusCode);

        // Two asked for, one comes back: the second answer is not a picture.
        _generator.ReturnsAt(1, [0x01, 0x02, 0x03]);
        var asked = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 2, creativeContextId = contextId, recipeId = recipe.Id },
            "images-journey-make",
            Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        var operationId = (await BodyOf(asked)).GetProperty("id").GetGuid();

        // Asking again with the same key — a lost answer, a second press — is the same run, not a second one.
        var again = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 2, creativeContextId = contextId, recipeId = recipe.Id },
            "images-journey-make",
            Ct);
        Assert.Equal(operationId, (await BodyOf(again)).GetProperty("id").GetGuid());

        await RunWorkerAsync();
        Assert.Equal(2, _generator.Calls);
        Assert.Equal([Prompt, Prompt], _generator.Prompts);

        var made = await BodyOf(await owner.GetAsync(In(A, $"generated-images/operations/{operationId}"), Ct));
        Assert.Equal("PartiallySucceeded", made.GetProperty("status").GetString());
        var staged = Assert.Single(made.GetProperty("images").EnumerateArray()).GetProperty("id").GetGuid();

        // The one that arrived is saved to the library on this work, which then names it as a keeper.
        var saved = await owner.PostAsJsonAsync(
            In(A, "dam-assets/from-generated-image"),
            new { generatedImageId = staged, metadata = new { title = "Galette, first slice" }, creativeContextId = contextId },
            "images-journey-keep",
            Ct);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var assetId = (await BodyOf(saved)).GetProperty("id").GetGuid();
        Assert.Contains(
            (await BodyOf(await owner.GetAsync(In(A, $"creative-contexts/{contextId}"), Ct))).GetProperty("references").EnumerateArray(),
            reference => reference.GetProperty("kind").GetString() == "DamAsset"
                && reference.GetProperty("purpose").GetString() == "Keeper"
                && reference.GetProperty("mediaAssetId").GetGuid() == assetId);

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "make-and-keep", [new { kind = "MediaAsset", id = assetId }])).StatusCode);
        Assert.Equal("lead-picture", NextStep(await ReadRunAsync(owner, A, runId)));

        // ---- 4. The lead picture: done when the recipe has one, read from the recipe ----
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await CompleteAsync(owner, A, runId, "lead-picture")).StatusCode);

        var linked = await owner.PostAsJsonAsync(
            In(A, $"recipes/{recipe.Id}/asset-links"),
            new { mediaAssetId = assetId, role = "Hero", expectedConcurrencyToken = recipe.Token },
            "images-journey-lead",
            Ct);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "lead-picture")).StatusCode);
        var finished = await owner.PostAsJsonAsync(
            In(A, $"workflow-runs/{runId}/completion"),
            new { expectedConcurrencyToken = await TokenAsync(owner, A, runId) },
            Ct);
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal("Completed", (await BodyOf(finished)).GetProperty("status").GetString());

        // ---- The neighbour: none of it is theirs to read, and none of it can finish their own run ----
        using var neighbour = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"workflow-runs/{runId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"creative-contexts/{contextId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"generated-images/operations/{operationId}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"dam-assets/{assetId}"), Ct)).StatusCode);

        // Our picture cannot become the lead picture of a recipe of theirs, and our recipe is no recipe of theirs.
        var theirRecipe = await CreateRecipeAsync(neighbour, B);
        var theirLead = await neighbour.PostAsJsonAsync(
            In(B, $"recipes/{theirRecipe.Id}/asset-links"),
            new { mediaAssetId = assetId, role = "Hero", expectedConcurrencyToken = theirRecipe.Token },
            Guid.NewGuid().ToString("N"),
            Ct);
        Assert.NotEqual(HttpStatusCode.OK, theirLead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.GetAsync(In(B, $"recipes/{recipe.Id}"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await neighbour.PostAsJsonAsync(
            In(B, $"recipes/{recipe.Id}/asset-links"),
            new { mediaAssetId = assetId, role = "Gallery", expectedConcurrencyToken = recipe.Token },
            Guid.NewGuid().ToString("N"),
            Ct)).StatusCode);

        var theirs = await neighbour.PostAsJsonAsync(In(B, "workflow-runs"), new { definitionKey = "images.create" }, Guid.NewGuid().ToString(), Ct);
        var theirRunId = (await BodyOf(theirs)).GetProperty("id").GetGuid();
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await CompleteAsync(neighbour, B, theirRunId, "about", [new { kind = "CreativeContext", id = contextId }])).StatusCode);

        var theirDraft = await neighbour.PutAsJsonAsync(
            In(B, $"workflow-runs/{theirRunId}/steps/about/draft"),
            new { kind = "CreativeContext", id = contextId, expectedConcurrencyToken = await TokenAsync(neighbour, B, theirRunId) },
            Ct);
        Assert.NotEqual(HttpStatusCode.OK, theirDraft.StatusCode);
    }

    [Fact]
    public async Task A_picture_saved_from_another_workspace_cannot_finish_making_and_keeping()
    {
        using var neighbour = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);
        var theirContext = await ContextAsync(neighbour, B, "Their broth");
        var theirAsset = await KeptPictureAsync(neighbour, B, theirContext);

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var runId = (await BodyOf(await owner.PostAsJsonAsync(
            In(A, "workflow-runs"), new { definitionKey = "images.create" }, Guid.NewGuid().ToString(), Ct))).GetProperty("id").GetGuid();
        var ours = await ContextAsync(owner, A, "Plum galette");
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "about", [new { kind = "CreativeContext", id = ours }])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(owner, A, runId, "plan")).StatusCode);

        var crossing = await CompleteAsync(owner, A, runId, "make-and-keep", [new { kind = "MediaAsset", id = theirAsset }]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossing.StatusCode);
        Assert.Equal("make-and-keep", NextStep(await ReadRunAsync(owner, A, runId)));
    }

    // ---- Helpers ----

    private sealed record SeededRecipe(Guid Id, string Token);

    private static string? NextStep(JsonElement run) =>
        run.GetProperty("nextAction").ValueKind == JsonValueKind.Null
            ? null
            : run.GetProperty("nextAction").GetProperty("stepKey").GetString();

    private static JsonElement Step(JsonElement run, string key) =>
        run.GetProperty("steps").EnumerateArray().Single(step => step.GetProperty("key").GetString() == key);

    private static async Task<JsonElement> ReadRunAsync(GatewayClient client, SeededWorkspace workspace, Guid runId) =>
        await BodyOf(await client.GetAsync(In(workspace, $"workflow-runs/{runId}"), Ct));

    private static async Task<string> TokenAsync(GatewayClient client, SeededWorkspace workspace, Guid runId) =>
        (await ReadRunAsync(client, workspace, runId)).GetProperty("concurrencyToken").GetString()!;

    private static async Task<HttpResponseMessage> CompleteAsync(
        GatewayClient client, SeededWorkspace workspace, Guid runId, string stepKey, object[]? results = null) =>
        await client.PostAsJsonAsync(
            In(workspace, $"workflow-runs/{runId}/steps/{stepKey}/completion"),
            new { results, expectedConcurrencyToken = await TokenAsync(client, workspace, runId) },
            Ct);

    private static async Task<SeededRecipe> CreateRecipeAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var created = await client.PostAsJsonAsync(In(workspace, "recipes"), new { title = "Plum galette" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var detail = await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), Ct));

        return new SeededRecipe(detail.GetProperty("id").GetGuid(), detail.GetProperty("concurrencyToken").GetString()!);
    }

    private static async Task<Guid> ContextAsync(GatewayClient client, SeededWorkspace workspace, string workingTitle)
    {
        var response = await client.PostAsJsonAsync(In(workspace, "creative-contexts"), new { workingTitle }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private static async Task AddRecipeAsync(GatewayClient client, SeededWorkspace workspace, Guid contextId, Guid recipeId)
    {
        var token = (await BodyOf(await client.GetAsync(In(workspace, $"creative-contexts/{contextId}"), Ct)))
            .GetProperty("concurrencyToken").GetString();
        var added = await client.PostAsJsonAsync(
            In(workspace, $"creative-contexts/{contextId}/references"),
            new { expectedConcurrencyToken = token, reference = new { kind = "Recipe", recipeId } },
            Ct);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
    }

    /// <summary>One picture made for a piece of work and saved to that workspace's library on it.</summary>
    private async Task<Guid> KeptPictureAsync(GatewayClient client, SeededWorkspace workspace, Guid contextId)
    {
        var asked = await client.PostAsJsonAsync(
            In(workspace, "generated-images"),
            new { promptText = "A bowl of broth.", variantCount = 1, creativeContextId = contextId },
            Guid.NewGuid().ToString("N"),
            Ct);
        var operationId = (await BodyOf(asked)).GetProperty("id").GetGuid();
        await RunWorkerAsync();

        var made = await BodyOf(await client.GetAsync(In(workspace, $"generated-images/operations/{operationId}"), Ct));
        var staged = Assert.Single(made.GetProperty("images").EnumerateArray()).GetProperty("id").GetGuid();
        var saved = await client.PostAsJsonAsync(
            In(workspace, "dam-assets/from-generated-image"),
            new { generatedImageId = staged, metadata = new { title = "Broth" }, creativeContextId = contextId },
            Guid.NewGuid().ToString("N"),
            Ct);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        return (await BodyOf(saved)).GetProperty("id").GetGuid();
    }

    private async Task<GeneratedImagePassSummary> RunWorkerAsync()
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IGeneratedImageWorker>().RunPendingAsync(Ct);
    }
}
