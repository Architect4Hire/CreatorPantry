using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// IMG-001's request route through the real Gateway, across two workspaces.
/// </summary>
/// <remarks>
/// <para>
/// tenancy.md asks for the boundary to be proven per feature and through the route, because a business test
/// cannot show that the policy, the route template and the workspace middleware are wired the way the
/// controller claims. The pin is the interesting part here: this is the first AI capability whose recipe is
/// optional, so "another workspace's recipe" and "no recipe at all" are two very different requests that must
/// not be confusable.
/// </para>
/// <para>
/// Separate from <c>PhotographyConceptEndpointTests</c>, which is contract and validator work and needs no
/// database. Nothing here reaches a provider: the test host runs no worker, so an accepted request stays
/// queued, which is all these assertions need.
/// </para>
/// </remarks>
public sealed class PhotographyConceptIsolationEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A shoot with nothing pinned is a complete request, and the commonest one.</summary>
    [Fact]
    public async Task A_request_that_pins_no_recipe_is_accepted()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await AskAsync(client, _fixture.WorkspaceA, new { channelKey = "instagram" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(
            $"{Route(_fixture.WorkspaceA)}/"
                + body.GetProperty("aiProposalRequestId").GetGuid(),
            response.Headers.Location!.ToString());
    }

    /// <summary>
    /// A recipe belonging to the neighbour is answered exactly as one that never existed.
    /// </summary>
    /// <remarks>
    /// A 404 rather than a 422, by this codebase's suffix rule and by tenancy.md: an unknown recipe and an
    /// inaccessible one must be one answer. It is the same 404, with the same code, for a recipe that does not
    /// exist anywhere — so a shoot request cannot be used to ask what a neighbour owns.
    /// </remarks>
    [Fact]
    public async Task A_recipe_in_another_workspace_is_answered_as_one_that_does_not_exist()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);
        using var theirs = await OwnerOf(_fixture.WorkspaceB);

        var neighbours = await CreateRecipeAsync(theirs, _fixture.WorkspaceB);

        var crossing = await AskAsync(mine, _fixture.WorkspaceA, new { recipeId = neighbours });
        var missing = await AskAsync(mine, _fixture.WorkspaceA, new { recipeId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
        Assert.Equal(missing.StatusCode, crossing.StatusCode);

        // Each body is read once: the content stream is consumed, so a second read would fail rather than
        // compare.
        var crossingBody = await BodyOf(crossing);
        var missingBody = await BodyOf(missing);

        Assert.Equal(
            AiPhotographyConceptRequestErrors.RecipeNotFound,
            crossingBody.GetProperty("code").GetString());

        // The whole body, not just the code: two refusals that matched on status and code but differed in
        // their message would disclose exactly what the matching status hides. `traceId` is per-request by
        // design and is the only thing allowed to differ.
        //
        // The shared message does say "this workspace has no recipe with that id", which is deliberate and
        // discloses nothing — it is a statement about the caller's own workspace, and it is the same sentence
        // whether the id belongs to a neighbour or to nobody.
        Assert.Equal(WithoutTraceId(missingBody), WithoutTraceId(crossingBody));
    }

    /// <summary>A creator's own recipe is accepted, so the refusal above is the boundary and not the pin.</summary>
    [Fact]
    public async Task A_recipe_in_the_callers_own_workspace_is_accepted()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var own = await CreateRecipeAsync(mine, _fixture.WorkspaceA);
        var response = await AskAsync(mine, _fixture.WorkspaceA, new { recipeId = own });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    /// <summary>A request the neighbour made cannot be read, and reads as absent rather than refused.</summary>
    [Fact]
    public async Task A_request_in_another_workspace_cannot_be_read()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);
        using var theirs = await OwnerOf(_fixture.WorkspaceB);

        var accepted = await AskAsync(theirs, _fixture.WorkspaceB, new { channelKey = "instagram" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var requestId = (await BodyOf(accepted)).GetProperty("aiProposalRequestId").GetGuid();

        var crossing = await mine.GetAsync($"{Route(_fixture.WorkspaceA)}/{requestId}", Ct);
        var missing = await mine.GetAsync($"{Route(_fixture.WorkspaceA)}/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
        Assert.Equal(missing.StatusCode, crossing.StatusCode);
        Assert.Equal(await CodeOf(missing), await CodeOf(crossing));
    }

    /// <summary>
    /// A non-member is refused the route itself, and an inaccessible workspace answers as an unknown one does.
    /// </summary>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_ask_on_the_others_route()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var refused = await AskAsync(mine, _fixture.WorkspaceB, new { channelKey = "instagram" });
        var unknown = await mine.PostAsJsonAsync(
            "/api/v1/workspaces/no-such-workspace/photography-concept-requests",
            new { channelKey = "instagram" },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(unknown.StatusCode, refused.StatusCode);
    }

    /// <summary>Generation spends the workspace's allowance, so a Viewer may not start one.</summary>
    [Fact]
    public async Task A_viewer_cannot_ask_for_concepts_but_may_poll()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceB);
        var accepted = await AskAsync(owner, _fixture.WorkspaceB, new { channelKey = "instagram" });
        var requestId = (await BodyOf(accepted)).GetProperty("aiProposalRequestId").GetGuid();

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: Ct);

        var asking = await AskAsync(viewer, _fixture.WorkspaceB, new { channelKey = "instagram" });
        var polling = await viewer.GetAsync($"{Route(_fixture.WorkspaceB)}/{requestId}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, asking.StatusCode);
        Assert.Equal(HttpStatusCode.OK, polling.StatusCode);
    }

    /// <summary>Without an idempotency key a retry would buy a second set of concepts.</summary>
    [Fact]
    public async Task A_request_without_an_idempotency_key_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            Route(_fixture.WorkspaceA), new { channelKey = "instagram" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string Route(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/photography-concept-requests";

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    private static Task<HttpResponseMessage> AskAsync(
        GatewayClient client, SeededWorkspace workspace, object body) =>
        client.PostAsJsonAsync(Route(workspace), body, Guid.NewGuid().ToString("N"), Ct);

    private static async Task<Guid> CreateRecipeAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title = "Buttermilk Soda Bread" },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("recipeId").GetGuid();
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await BodyOf(response)).GetProperty("code").GetString();

    /// <summary>
    /// A problem body with its per-request field dropped, so two refusals can be compared as the answers they
    /// are. <c>traceId</c> differs by design and is the only thing allowed to.
    /// </summary>
    private static string WithoutTraceId(JsonElement body)
    {
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node.Remove("traceId");

        return node.ToJsonString();
    }
}
