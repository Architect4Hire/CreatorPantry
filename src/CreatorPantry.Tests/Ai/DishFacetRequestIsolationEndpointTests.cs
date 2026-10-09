using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The dish-facet request route through the real Gateway, across two workspaces.
/// </summary>
/// <remarks>
/// <para>
/// tenancy.md asks for the boundary to be proven per feature and through the route, because a business test
/// cannot show that the policy, the route template and the workspace middleware are wired the way the
/// controller claims.
/// </para>
/// <para>
/// This capability names no recipe at all, so there is no pin to confuse across the boundary — which makes the
/// read the interesting half: a reading is a stored proposal about a creator's own working title, and a
/// neighbour must not be able to poll one. A dish name is modest as secrets go, but it is the creator's
/// unpublished editorial plan, and "what is Workspace B working on" is exactly the question a leaked poll
/// would answer.
/// </para>
/// <para>
/// Nothing here reaches a provider: the test host runs no worker, so an accepted request stays queued, which
/// is all these assertions need.
/// </para>
/// </remarks>
public sealed class DishFacetRequestIsolationEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_naming_a_dish_is_accepted_and_points_at_its_own_status_resource()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await AskAsync(client, _fixture.WorkspaceA, "Fatoosh Salad with Grilled Chicken");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await BodyOf(response);
        Assert.Equal(
            $"{Route(_fixture.WorkspaceA)}/{body.GetProperty("aiProposalRequestId").GetGuid()}",
            response.Headers.Location!.ToString());
    }

    /// <summary>A reading the neighbour asked for cannot be read, and reads as absent rather than refused.</summary>
    [Fact]
    public async Task A_request_in_another_workspace_cannot_be_read()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);
        using var theirs = await OwnerOf(_fixture.WorkspaceB);

        var accepted = await AskAsync(theirs, _fixture.WorkspaceB, "Their unannounced autumn galette");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var requestId = (await BodyOf(accepted)).GetProperty("aiProposalRequestId").GetGuid();

        var crossing = await mine.GetAsync($"{Route(_fixture.WorkspaceA)}/{requestId}", Ct);
        var missing = await mine.GetAsync($"{Route(_fixture.WorkspaceA)}/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
        Assert.Equal(missing.StatusCode, crossing.StatusCode);
        Assert.Equal(WithoutTraceId(await BodyOf(missing)), WithoutTraceId(await BodyOf(crossing)));
    }

    /// <summary>
    /// An operation of another task type reads as absent on this route, in the same words.
    /// </summary>
    /// <remarks>
    /// Proven through the route rather than only in Business, because the status route must not become a way
    /// to enumerate which capabilities a workspace has been using: a reading that came back "not a dish-facet
    /// request" would answer that for every id a caller could guess at.
    /// </remarks>
    [Fact]
    public async Task A_request_of_another_task_type_reads_as_absent_in_the_same_words()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var concepts = await mine.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipe-concept-requests",
            new { dishName = "Fattoush" },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, concepts.StatusCode);
        var otherTaskId = (await BodyOf(concepts)).GetProperty("aiProposalRequestId").GetGuid();

        var crossing = await mine.GetAsync($"{Route(_fixture.WorkspaceA)}/{otherTaskId}", Ct);
        var missing = await mine.GetAsync($"{Route(_fixture.WorkspaceA)}/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
        Assert.Equal(WithoutTraceId(await BodyOf(missing)), WithoutTraceId(await BodyOf(crossing)));
    }

    /// <summary>
    /// A non-member is refused the route itself, and an inaccessible workspace answers as an unknown one does.
    /// </summary>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_ask_on_the_others_route()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var refused = await AskAsync(mine, _fixture.WorkspaceB, "Fattoush");
        var unknown = await mine.PostAsJsonAsync(
            "/api/v1/workspaces/no-such-workspace/dish-facet-requests",
            new { dishName = "Fattoush" },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(unknown.StatusCode, refused.StatusCode);

        // The whole body, not just the status: two refusals matching on status but differing in their message
        // would disclose exactly what the matching status hides — whether that slug is a workspace at all.
        // `traceId` is per-request by design and is the only thing allowed to differ.
        Assert.Equal(WithoutTraceId(await BodyOf(unknown)), WithoutTraceId(await BodyOf(refused)));
    }

    /// <summary>
    /// Reading a name spends the workspace's allowance, so a Viewer may not start one.
    /// </summary>
    /// <remarks>
    /// Contributor rather than Viewer matters more here than on the routes a creator presses a button for:
    /// this one is asked automatically as a name is filled in, so a Viewer idling on the setup step would
    /// otherwise spend someone else's budget by typing.
    /// </remarks>
    [Fact]
    public async Task A_viewer_cannot_ask_for_a_reading_but_may_poll()
    {
        using var owner = await OwnerOf(_fixture.WorkspaceB);
        var accepted = await AskAsync(owner, _fixture.WorkspaceB, "Fattoush");
        var requestId = (await BodyOf(accepted)).GetProperty("aiProposalRequestId").GetGuid();

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: Ct);

        var asking = await AskAsync(viewer, _fixture.WorkspaceB, "Fattoush");
        var polling = await viewer.GetAsync($"{Route(_fixture.WorkspaceB)}/{requestId}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, asking.StatusCode);
        Assert.Equal(HttpStatusCode.OK, polling.StatusCode);
    }

    /// <summary>
    /// Without an idempotency key a retry would buy a second reading of the same name.
    /// </summary>
    /// <remarks>
    /// The normal case for this capability rather than the exceptional one: the surface asks as a creator
    /// types, so the same name arriving twice is what a flaky connection produces, not what a double-click
    /// does.
    /// </remarks>
    [Fact]
    public async Task A_request_without_an_idempotency_key_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            Route(_fixture.WorkspaceA), new { dishName = "Fattoush" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A blank name is not a question with an answer, and is refused before anything is queued.</summary>
    [Fact]
    public async Task A_request_with_no_dish_name_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var empty = await AskAsync(client, _fixture.WorkspaceA, "   ");
        var absent = await client.PostAsJsonAsync(
            Route(_fixture.WorkspaceA), new { }, Guid.NewGuid().ToString("N"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, absent.StatusCode);
    }

    /// <summary>
    /// The same key and the same name returns the first reading rather than buying a second.
    /// </summary>
    [Fact]
    public async Task The_same_key_replays_the_first_reading()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);
        var key = Guid.NewGuid().ToString("N");

        var first = await client.PostAsJsonAsync(
            Route(_fixture.WorkspaceA), new { dishName = "Fattoush" }, key, Ct);
        var second = await client.PostAsJsonAsync(
            Route(_fixture.WorkspaceA), new { dishName = "Fattoush" }, key, Ct);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));

        Assert.Equal(
            (await BodyOf(first)).GetProperty("aiProposalRequestId").GetGuid(),
            (await BodyOf(second)).GetProperty("aiProposalRequestId").GetGuid());
    }

    private static string Route(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/dish-facet-requests";

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    private static Task<HttpResponseMessage> AskAsync(
        GatewayClient client, SeededWorkspace workspace, string dishName) =>
        client.PostAsJsonAsync(Route(workspace), new { dishName }, Guid.NewGuid().ToString("N"), Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

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
