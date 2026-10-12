using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// <c>GET</c> and <c>PUT .../ai-features</c> through the real Gateway: the contract, the Owner bar, the
/// stable error codes, and the boundary between two workspaces.
/// </summary>
public sealed class AiFeaturesEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string FeaturesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/ai-features";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string[] EnabledIn(JsonElement body) =>
    [
        .. body.GetProperty("features").EnumerateArray()
            .Where(feature => feature.GetProperty("enabled").GetBoolean())
            .Select(feature => feature.GetProperty("task").GetString()!),
    ];

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    private static Task<HttpResponseMessage> PutAsync(
        GatewayClient client, SeededWorkspace workspace, params string[] enabled) =>
        client.SendAsync(HttpMethod.Put, FeaturesIn(workspace), new { enabled }, headers: null, Ct);

    [Fact]
    public async Task Any_member_reads_every_switchable_feature()
    {
        // Workspace B's member is a Viewer, the lowest role there is.
        using var client = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var response = await client.GetAsync(FeaturesIn(_fixture.WorkspaceB), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());

        var features = (await BodyOf(response)).GetProperty("features").EnumerateArray().ToArray();
        Assert.Equal(AiFeaturePolicy.SwitchableTasks, features.Select(feature => feature.GetProperty("task").GetString()));
        Assert.All(features, feature => Assert.False(feature.GetProperty("setByWorkspace").GetBoolean()));
    }

    [Fact]
    public async Task An_owner_replaces_the_set_and_a_read_returns_it()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await PutAsync(client, _fixture.WorkspaceA, AiTaskCatalog.RecipeRevision, AiTaskCatalog.RecipeReview);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([AiTaskCatalog.RecipeRevision, AiTaskCatalog.RecipeReview], EnabledIn(await BodyOf(response)));

        var read = await BodyOf(await client.GetAsync(FeaturesIn(_fixture.WorkspaceA), Ct));
        Assert.Equal([AiTaskCatalog.RecipeRevision, AiTaskCatalog.RecipeReview], EnabledIn(read));
        Assert.All(
            read.GetProperty("features").EnumerateArray(),
            feature => Assert.True(feature.GetProperty("setByWorkspace").GetBoolean()));
    }

    [Fact]
    public async Task An_editor_is_refused_and_nothing_changes()
    {
        using var editor = await SignInAsync(_fixture.WorkspaceA.MemberEmail);

        var response = await PutAsync(editor, _fixture.WorkspaceA, AiTaskCatalog.RecipeRevision);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var read = await BodyOf(await editor.GetAsync(FeaturesIn(_fixture.WorkspaceA), Ct));
        Assert.All(
            read.GetProperty("features").EnumerateArray(),
            feature => Assert.False(feature.GetProperty("setByWorkspace").GetBoolean()));
    }

    [Fact]
    public async Task An_unknown_feature_answers_400_with_its_stable_code()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await PutAsync(client, _fixture.WorkspaceA, "diagnostic");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(AiFeatureErrors.Invalid, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("enabled[0]", out _));
    }

    [Fact]
    public async Task One_workspaces_choice_is_invisible_to_and_unwritable_from_another()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);

        await PutAsync(ownerA, _fixture.WorkspaceA, AiTaskCatalog.RecipeRevision);

        // B's own read is untouched by A's write.
        var inB = await BodyOf(await ownerB.GetAsync(FeaturesIn(_fixture.WorkspaceB), Ct));
        Assert.All(
            inB.GetProperty("features").EnumerateArray(),
            feature => Assert.False(feature.GetProperty("setByWorkspace").GetBoolean()));

        // And B's Owner can neither read nor write A's: 404 for both, so A's existence is not disclosed.
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.GetAsync(FeaturesIn(_fixture.WorkspaceA), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(ownerB, _fixture.WorkspaceA)).StatusCode);

        var stillA = await BodyOf(await ownerA.GetAsync(FeaturesIn(_fixture.WorkspaceA), Ct));
        Assert.Equal([AiTaskCatalog.RecipeRevision], EnabledIn(stillA));
    }
}
