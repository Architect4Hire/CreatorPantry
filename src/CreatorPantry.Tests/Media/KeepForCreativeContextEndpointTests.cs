using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Managers.MalwareScanning;
using CreatorPantry.Domain.Managers.Storage;
using CreatorPantry.Domain.Modules.Content.Managers;
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
/// Keeping a generated picture for a piece of work, through the real Gateway and across two workspaces: the
/// picture reaches the library and the work names it in one request, or neither happens.
/// </summary>
/// <remarks>
/// Before this the two were separate writes a client made in order, and the second could fail after the
/// first had committed — a picture in the library that the work it was made for did not point at, and that a
/// themed day's history therefore never showed. What is under test is that there is no such state any more,
/// from either side of the workspace boundary.
/// </remarks>
public sealed class KeepForCreativeContextEndpointTests : IAsyncLifetime
{
    private const string ThemeKey = "soup-sunday";

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

    [Fact]
    public async Task A_picture_kept_for_a_piece_of_work_is_named_on_it_and_remembered_by_its_day()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        await ThemeAsync(client, A);
        var contextId = await ContextAsync(client, A, ThemeKey);
        var imageId = await StageAsync(client, A);

        // Read first, so the history is cached with nothing in it: the keep has to retire that answer.
        Assert.Empty(await HistoryAsync(client, A));

        var kept = await KeepAsync(client, A, imageId, contextId);
        Assert.Equal(HttpStatusCode.Created, kept.StatusCode);
        var assetId = (await BodyOf(kept)).GetProperty("id").GetGuid();

        var reference = Assert.Single(await ReferencesAsync(client, A, contextId));
        Assert.Equal("DamAsset", reference.GetProperty("kind").GetString());
        Assert.Equal("Keeper", reference.GetProperty("purpose").GetString());
        Assert.Equal(assetId, reference.GetProperty("mediaAssetId").GetGuid());
        Assert.Equal(1, reference.GetProperty("mediaAssetVersionNumber").GetInt32());

        // No second request was made, and the day already knows.
        var remembered = Assert.Single(await HistoryAsync(client, A));
        Assert.Equal(contextId, remembered.GetProperty("contextId").GetGuid());
        var outcome = Assert.Single(remembered.GetProperty("outcomes").EnumerateArray());
        Assert.Equal("KeptPicture", outcome.GetProperty("kind").GetString());
        Assert.Equal(assetId, outcome.GetProperty("mediaAssetId").GetGuid());
    }

    [Fact]
    public async Task A_picture_kept_for_no_piece_of_work_is_kept_as_it_always_was()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var contextId = await ContextAsync(client, A);
        var imageId = await StageAsync(client, A);

        var kept = await KeepAsync(client, A, imageId, contextId: null);

        Assert.Equal(HttpStatusCode.Created, kept.StatusCode);
        Assert.Empty(await ReferencesAsync(client, A, contextId));
    }

    /// <summary>
    /// The neighbour's piece of work is answered exactly as one that does not exist, and nothing is stored:
    /// not the asset, not a reference on their work, and the staged picture is still ours to keep.
    /// </summary>
    [Fact]
    public async Task Keeping_for_another_workspaces_piece_of_work_is_refused_as_an_unknown_one_and_stores_nothing()
    {
        using var ours = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var theirs = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);
        var theirContext = await ContextAsync(theirs, B);
        var imageId = await StageAsync(ours, A);

        var crossing = await KeepAsync(ours, A, imageId, theirContext);
        var missing = await KeepAsync(ours, A, imageId, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, crossing.StatusCode);
        Assert.Equal(missing.StatusCode, crossing.StatusCode);

        var crossingBody = await BodyOf(crossing);
        Assert.Equal(MediaErrorCodes.AssetInvalidRequest, crossingBody.GetProperty("code").GetString());
        Assert.True(NamesTheWork(crossingBody));
        Assert.Equal(WithoutTraceId(await BodyOf(missing)), WithoutTraceId(crossingBody));

        Assert.Empty(await LibraryAsync(ours, A));
        Assert.Empty(await ReferencesAsync(theirs, B, theirContext));

        // Still staged, so the refusal cost the creator nothing: the same picture is kept on a second try.
        Assert.Equal(HttpStatusCode.Created, (await KeepAsync(ours, A, imageId, contextId: null)).StatusCode);
    }

    [Fact]
    public async Task An_id_that_could_not_be_a_piece_of_works_is_refused_by_field()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var imageId = await StageAsync(client, A);

        var response = await KeepAsync(client, A, imageId, Guid.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(NamesTheWork(await BodyOf(response)));
        Assert.Empty(await LibraryAsync(client, A));
    }

    /// <summary>
    /// A piece of work that already names every source it can refuses the picture, and the keep with it: the
    /// creator is told before anything is stored, rather than left with a picture their work cannot point at.
    /// </summary>
    [Fact]
    public async Task A_piece_of_work_with_no_room_left_refuses_the_keep_and_stores_nothing()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var contextId = await ContextAsync(client, A);
        var imageId = await StageAsync(client, A);

        for (var index = 0; index < CreativeContextPolicy.MaxReferences; index++)
        {
            var recipe = await client.PostAsJsonAsync(In(A, "recipes"), new { title = $"Recipe {index}" }, Ct);
            var recipeId = (await BodyOf(recipe)).GetProperty("recipeId").GetGuid();
            var token = (await BodyOf(await client.GetAsync(In(A, $"creative-contexts/{contextId}"), Ct)))
                .GetProperty("concurrencyToken").GetString();

            var added = await client.PostAsJsonAsync(
                In(A, $"creative-contexts/{contextId}/references"),
                new { expectedConcurrencyToken = token, reference = new { kind = "Recipe", recipeId } },
                Ct);
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        }

        var response = await KeepAsync(client, A, imageId, contextId);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ContentErrorCodes.CreativeContextReferenceLimit, (await BodyOf(response)).GetProperty("code").GetString());
        Assert.Empty(await LibraryAsync(client, A));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(In(A, $"generated-images/{imageId}/preview"), Ct)).StatusCode);
        Assert.Equal(CreativeContextPolicy.MaxReferences, (await ReferencesAsync(client, A, contextId)).Count);
    }

    /// <summary>
    /// A picture kept before the work was named is finished by keeping it again with the work: the same asset,
    /// now pointed at — and a third time changes nothing.
    /// </summary>
    [Fact]
    public async Task Keeping_again_names_the_picture_on_the_work_once_and_makes_no_second_asset()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var contextId = await ContextAsync(client, A);
        var imageId = await StageAsync(client, A);

        var first = await KeepAsync(client, A, imageId, contextId: null);
        var assetId = (await BodyOf(first)).GetProperty("id").GetGuid();
        Assert.Empty(await ReferencesAsync(client, A, contextId));

        foreach (var _ in Enumerable.Range(0, 2))
        {
            var again = await KeepAsync(client, A, imageId, contextId);

            Assert.Equal(HttpStatusCode.Created, again.StatusCode);
            Assert.Equal(assetId, (await BodyOf(again)).GetProperty("id").GetGuid());
        }

        Assert.Equal(assetId, Assert.Single(await ReferencesAsync(client, A, contextId)).GetProperty("mediaAssetId").GetGuid());
        Assert.Single(await LibraryAsync(client, A));
    }

    /// <summary>A repeat cannot be used to point a neighbour's work at a picture either.</summary>
    [Fact]
    public async Task Keeping_again_for_another_workspaces_piece_of_work_is_refused_and_names_nothing()
    {
        using var ours = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var theirs = await _fixture.SignInAsync(B.OwnerEmail, cancellationToken: Ct);
        var theirContext = await ContextAsync(theirs, B);
        var imageId = await StageAsync(ours, A);
        await KeepAsync(ours, A, imageId, contextId: null);

        var again = await KeepAsync(ours, A, imageId, theirContext);

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Empty(await ReferencesAsync(theirs, B, theirContext));
    }

    [Fact]
    public async Task The_same_key_naming_a_different_piece_of_work_is_refused()
    {
        using var client = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        var first = await ContextAsync(client, A);
        var second = await ContextAsync(client, A);
        var imageId = await StageAsync(client, A);

        await KeepAsync(client, A, imageId, first, "keep-1");
        var different = await KeepAsync(client, A, imageId, second, "keep-1");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);
        Assert.Empty(await ReferencesAsync(client, A, second));
    }

    private static Task<HttpResponseMessage> KeepAsync(
        GatewayClient client, SeededWorkspace workspace, Guid imageId, Guid? contextId, string? idempotencyKey = null)
    {
        var body = new { generatedImageId = imageId, metadata = new { title = "Soup, overhead" }, creativeContextId = contextId };
        var path = In(workspace, "dam-assets/from-generated-image");

        return idempotencyKey is null
            ? client.PostAsJsonAsync(path, body, Ct)
            : client.PostAsJsonAsync(path, body, idempotencyKey, Ct);
    }

    /// <summary>One staged picture of this workspace's, made by the worker's own pass.</summary>
    private async Task<Guid> StageAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var requested = await client.PostAsJsonAsync(
            In(workspace, "generated-images"),
            new { promptText = "A bowl of soup on a wooden table.", variantCount = 1 },
            Guid.NewGuid().ToString("N"),
            Ct);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var operationId = (await BodyOf(requested)).GetProperty("id").GetGuid();

        await using (var scope = _fixture.Api.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IGeneratedImageWorker>().RunPendingAsync(Ct);
        }

        var run = await BodyOf(await client.GetAsync(In(workspace, $"generated-images/operations/{operationId}"), Ct));

        return Assert.Single(run.GetProperty("images").EnumerateArray()).GetProperty("id").GetGuid();
    }

    private static async Task ThemeAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.SendAsync(
            HttpMethod.Put,
            In(workspace, "weekly-themes"),
            new { themes = new[] { new { day = "Sunday", key = ThemeKey, displayName = "Soup Sunday" } } },
            headers: null,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<Guid> ContextAsync(GatewayClient client, SeededWorkspace workspace, string? weeklyThemeKey = null)
    {
        var response = await client.PostAsJsonAsync(
            In(workspace, "creative-contexts"), new { workingTitle = "Soup, Sunday", weeklyThemeKey }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> ReferencesAsync(GatewayClient client, SeededWorkspace workspace, Guid contextId) =>
        [.. (await BodyOf(await client.GetAsync(In(workspace, $"creative-contexts/{contextId}"), Ct)))
            .GetProperty("references").EnumerateArray()];

    private static async Task<List<JsonElement>> LibraryAsync(GatewayClient client, SeededWorkspace workspace) =>
        [.. (await BodyOf(await client.GetAsync(In(workspace, "dam-assets"), Ct))).GetProperty("items").EnumerateArray()];

    private static async Task<List<JsonElement>> HistoryAsync(GatewayClient client, SeededWorkspace workspace) =>
        [.. (await BodyOf(await client.GetAsync(In(workspace, $"weekly-themes/{ThemeKey}/history"), Ct)))
            .GetProperty("items").EnumerateArray()];

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <summary>Whether a refusal's field errors name the piece of work, however the field is cased.</summary>
    private static bool NamesTheWork(JsonElement problem) =>
        problem.GetProperty("errors").EnumerateObject()
            .Any(field => string.Equals(field.Name, "creativeContextId", StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc cref="Ai.PhotographyConceptIsolationEndpointTests"/>
    private static string WithoutTraceId(JsonElement body)
    {
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node.Remove("traceId");

        return node.ToJsonString();
    }
}
