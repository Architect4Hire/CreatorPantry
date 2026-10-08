using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// The creative-production path walked end to end across its real seams (12.10l): a creator asks for
/// pictures, the worker makes them, one is kept into the library with the prompt that made it, the asset is
/// linked to a recipe, and then unlinked.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Every step below has thorough tests of its own, and before this there
/// was none that ran two of them in a row over HTTP. Keeping a generated image into the library was not
/// exercised through its route at all. A key between two of these tables, or an id one step hands the next in
/// the wrong shape, would have passed every per-feature test.
/// </para>
/// <para>
/// <strong>What is real and what is not.</strong> Real: the Gateway, the API, the database, every facade and
/// the worker's own pass. Stubbed: the image provider (no model runs in a test, and a generation costs money),
/// the object store (in memory) and the malware scanner. The worker is run by hand, once, rather than on a
/// timer — the API host has no queue driver, by design, so a request can never generate inside itself.
/// </para>
/// </remarks>
public sealed class CreativePipelineEndToEndTests : IAsyncLifetime
{
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

    private static string In(SeededWorkspace workspace, string path) => $"/api/v1/workspaces/{workspace.Slug}/{path}";

    [Fact]
    public async Task A_picture_goes_from_a_request_to_a_recipe_and_back_off_it_without_leaving_the_library()
    {
        var workspace = _fixture.WorkspaceA;
        using var client = await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

        // 1. Ask for pictures. Accepted and queued: nothing is generated inside the request.
        var requested = await client.PostAsJsonAsync(
            In(workspace, "generated-images"),
            new { promptText = "Overhead shot of soda bread on linen, soft window light.", variantCount = 2 },
            "pipeline-generate",
            Ct);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var operationId = (await BodyOf(requested)).GetProperty("id").GetGuid();
        Assert.Equal(0, _generator.Calls);

        // 2. The worker's pass makes them, one provider call per picture.
        var pass = await RunWorkerAsync();
        Assert.Equal(1, pass.Claimed);
        Assert.Equal(1, pass.Generated);
        Assert.Equal(2, _generator.Calls);

        // 3. The run now reports two staged pictures, each readable through its own route.
        var run = await BodyOf(await client.GetAsync(In(workspace, $"generated-images/operations/{operationId}"), Ct));
        Assert.Equal(2, run.GetProperty("stagedCount").GetInt32());
        var staged = run.GetProperty("images").EnumerateArray().Select(image => image.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, staged.Count);

        var preview = await client.GetAsync(In(workspace, $"generated-images/{staged[0]}/preview"), Ct);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/png", preview.Content.Headers.ContentType?.MediaType);

        // 4. A recipe for the picture to belong to.
        var recipe = await CreateRecipeAsync(client, workspace);

        // 5. Keep the first picture into the library, with the prompt that made it; decline the second.
        var kept = await client.PostAsJsonAsync(
            In(workspace, "dam-assets/from-generated-image"),
            new
            {
                generatedImageId = staged[0],
                metadata = new { title = "Soda bread hero", altText = "A round loaf on a linen cloth." },
                prompt = new
                {
                    channelKey = "instagram",
                    imageKind = "Hero",
                    text = "Overhead shot of soda bread on linen, soft window light.",
                    source = "Manual",
                },
            },
            "pipeline-keep",
            Ct);
        Assert.Equal(HttpStatusCode.Created, kept.StatusCode);
        var asset = await BodyOf(kept);
        var assetId = asset.GetProperty("id").GetGuid();
        Assert.Equal("AiGenerated", asset.GetProperty("kind").GetString());
        Assert.Equal(staged[0], asset.GetProperty("sourceGeneratedImageId").GetGuid());
        var promptId = asset.GetProperty("promptRecordId").GetGuid();

        var declined = await client.SendAsync(HttpMethod.Delete, In(workspace, $"generated-images/{staged[1]}"), body: null, headers: null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, declined.StatusCode);

        // 6. The library has it, as bytes of its own, and its lineage names the prompt.
        var detail = await BodyOf(await client.GetAsync(In(workspace, $"dam-assets/{assetId}"), Ct));
        Assert.Equal("Soda bread hero", detail.GetProperty("title").GetString());
        Assert.Contains(detail.GetProperty("prompts").EnumerateArray(), prompt => prompt.GetProperty("id").GetGuid() == promptId);

        var content = await client.GetAsync(In(workspace, $"dam-assets/{assetId}/content"), Ct);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.NotEmpty(await content.Content.ReadAsByteArrayAsync(Ct));

        // The prompt is in the Prompt Library in its own right, word for word.
        var prompt = await BodyOf(await client.GetAsync(In(workspace, $"prompts/{promptId}"), Ct));
        Assert.Equal(promptId, prompt.GetProperty("promptRecordId").GetGuid());
        Assert.Equal("Overhead shot of soda bread on linen, soft window light.", prompt.GetProperty("text").GetString());

        // The declined picture is no longer served; the kept one's staging copy still is, and is the same picture.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(In(workspace, $"generated-images/{staged[1]}/preview"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(In(workspace, $"generated-images/{staged[0]}/preview"), Ct)).StatusCode);

        // 7. Link the asset to the recipe as its lead picture, held at the version it has now.
        var linked = await client.PostAsJsonAsync(
            In(workspace, $"recipes/{recipe.Id}/asset-links"),
            new { mediaAssetId = assetId, role = "Hero", versionNumber = 1, caption = "Fresh from the oven.", expectedConcurrencyToken = recipe.Token },
            "pipeline-link",
            Ct);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var linkedRecipe = await BodyOf(linked);
        var link = Assert.Single(linkedRecipe.GetProperty("assetLinks").EnumerateArray());
        Assert.Equal(assetId, link.GetProperty("mediaAssetId").GetGuid());
        Assert.Equal(2, linkedRecipe.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());

        // The library's own page says where the picture is used.
        var used = await BodyOf(await client.GetAsync(In(workspace, $"dam-assets/{assetId}"), Ct));
        var recipeLink = Assert.Single(used.GetProperty("recipeLinks").EnumerateArray());
        Assert.Equal(recipe.Id, recipeLink.GetProperty("recipeId").GetGuid());
        Assert.Equal("Hero", recipeLink.GetProperty("role").GetString());

        // 8. Unlink it. The recipe lets go; the library keeps the picture, its version and its prompt.
        var unlinked = await client.SendAsync(
            HttpMethod.Delete,
            In(workspace, $"recipes/{recipe.Id}/asset-links/{link.GetProperty("id").GetGuid()}"),
            new { expectedConcurrencyToken = linkedRecipe.GetProperty("concurrencyToken").GetString() },
            headers: null,
            Ct);
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.Empty((await BodyOf(unlinked)).GetProperty("assetLinks").EnumerateArray());

        var after = await BodyOf(await client.GetAsync(In(workspace, $"dam-assets/{assetId}"), Ct));
        Assert.Empty(after.GetProperty("recipeLinks").EnumerateArray());
        Assert.Single(after.GetProperty("versions").EnumerateArray());
        Assert.Contains(after.GetProperty("prompts").EnumerateArray(), each => each.GetProperty("id").GetGuid() == promptId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(In(workspace, $"dam-assets/{assetId}/content"), Ct)).StatusCode);

        // 9. And none of it is anyone else's: the other workspace can reach no step of the chain.
        using var neighbour = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        foreach (var path in (string[])
        [
            $"generated-images/operations/{operationId}",
            $"generated-images/{staged[0]}/preview",
            $"dam-assets/{assetId}",
            $"dam-assets/{assetId}/content",
            $"prompts/{promptId}",
            $"recipes/{recipe.Id}",
        ])
        {
            var response = await neighbour.GetAsync(In(_fixture.WorkspaceB, path), Ct);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path} answered {(int)response.StatusCode} to another workspace");
        }
    }

    /// <summary>
    /// The same walk with every write sent twice under its key: a creator on a bad connection ends up with one
    /// run, one asset and one link — and one charge.
    /// </summary>
    [Fact]
    public async Task Every_step_repeated_under_its_key_happens_once()
    {
        var workspace = _fixture.WorkspaceA;
        using var client = await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);
        var generate = new { promptText = "A bowl of soup on a wooden table.", variantCount = 1 };

        var first = await client.PostAsJsonAsync(In(workspace, "generated-images"), generate, "twice-generate", Ct);
        var again = await client.PostAsJsonAsync(In(workspace, "generated-images"), generate, "twice-generate", Ct);
        var operationId = (await BodyOf(first)).GetProperty("id").GetGuid();
        Assert.Equal(operationId, (await BodyOf(again)).GetProperty("id").GetGuid());

        await RunWorkerAsync();
        await RunWorkerAsync();
        Assert.Equal(1, _generator.Calls);

        var run = await BodyOf(await client.GetAsync(In(workspace, $"generated-images/operations/{operationId}"), Ct));
        var imageId = Assert.Single(run.GetProperty("images").EnumerateArray()).GetProperty("id").GetGuid();

        var keep = new { generatedImageId = imageId, metadata = new { title = "Soup" } };
        var kept = await client.PostAsJsonAsync(In(workspace, "dam-assets/from-generated-image"), keep, "twice-keep", Ct);
        var keptAgain = await client.PostAsJsonAsync(In(workspace, "dam-assets/from-generated-image"), keep, "twice-keep", Ct);
        Assert.Equal(HttpStatusCode.Created, kept.StatusCode);
        var assetId = (await BodyOf(kept)).GetProperty("id").GetGuid();
        Assert.Equal(assetId, (await BodyOf(keptAgain)).GetProperty("id").GetGuid());

        var recipe = await CreateRecipeAsync(client, workspace);
        var linkBody = new { mediaAssetId = assetId, role = "Gallery", expectedConcurrencyToken = recipe.Token };
        var linked = await client.PostAsJsonAsync(In(workspace, $"recipes/{recipe.Id}/asset-links"), linkBody, "twice-link", Ct);
        var linkedAgain = await client.PostAsJsonAsync(In(workspace, $"recipes/{recipe.Id}/asset-links"), linkBody, "twice-link", Ct);
        Assert.Equal(HttpStatusCode.OK, linkedAgain.StatusCode);
        Assert.Equal(await linked.Content.ReadAsStringAsync(Ct), await linkedAgain.Content.ReadAsStringAsync(Ct));

        var library = await BodyOf(await client.GetAsync(In(workspace, "dam-assets"), Ct));
        Assert.Single(library.GetProperty("items").EnumerateArray());

        var read = await BodyOf(await client.GetAsync(In(workspace, $"recipes/{recipe.Id}"), Ct));
        Assert.Single(read.GetProperty("assetLinks").EnumerateArray());
        Assert.Equal(2, read.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    private sealed record SeededRecipe(Guid Id, string Token);

    private static async Task<SeededRecipe> CreateRecipeAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var created = await client.PostAsJsonAsync(In(workspace, "recipes"), new { title = "Soda bread" }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var detail = await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), Ct));

        return new SeededRecipe(detail.GetProperty("id").GetGuid(), detail.GetProperty("concurrencyToken").GetString()!);
    }

    private async Task<GeneratedImagePassSummary> RunWorkerAsync()
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IGeneratedImageWorker>().RunPendingAsync(Ct);
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}
