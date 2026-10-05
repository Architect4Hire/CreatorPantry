using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// IMG-002's request route through the real Gateway, across two workspaces.
/// </summary>
/// <remarks>
/// <para>
/// This capability resolves three ids from three places — a concept from another operation's proposal, a
/// recipe through the recipe module, a brief through the brand module — so it has three boundaries to hold
/// rather than one, and each is refused at the request rather than at the worker.
/// </para>
/// <para>
/// Nothing here reaches a provider: the test host runs no worker, so an accepted request stays queued, which
/// is all these assertions need. A concept is not seeded through IMG-001's own route either — that would
/// require a worker to have run — so the accepted-path assertions here stop at "the concept was refused
/// because it is not this workspace's", and the composable path is covered by the handler tests.
/// </para>
/// </remarks>
public sealed class ImagePromptIsolationEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A concept request id from another workspace is answered as one that never existed.
    /// </summary>
    /// <remarks>
    /// The neighbour's request is real and has run a photography-concept task, so this is the case A's own ids
    /// cannot imitate — and it answers with the same code as an id that exists nowhere.
    /// </remarks>
    [Fact]
    public async Task A_concept_request_in_another_workspace_is_answered_as_one_that_does_not_exist()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);
        using var theirs = await OwnerOf(_fixture.WorkspaceB);

        var neighbours = await ConceptRequestAsync(theirs, _fixture.WorkspaceB);

        var crossing = await AskAsync(mine, _fixture.WorkspaceA, Body(neighbours));
        var missing = await AskAsync(mine, _fixture.WorkspaceA, Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
        Assert.Equal(missing.StatusCode, crossing.StatusCode);

        var crossingBody = await BodyOf(crossing);
        var missingBody = await BodyOf(missing);

        Assert.Equal(
            AiImagePromptRequestErrors.ConceptNotFound, crossingBody.GetProperty("code").GetString());
        Assert.Equal(WithoutTraceId(missingBody), WithoutTraceId(crossingBody));
    }

    /// <summary>
    /// A concept request of the caller's own that ran a different task is refused the same way.
    /// </summary>
    /// <remarks>
    /// Without the task-type check, any proposal the workspace held would stand in for a photography concept —
    /// and a prompt would be composed from a recipe draft or a brand sample.
    /// </remarks>
    [Fact]
    public async Task A_request_of_this_workspace_that_ran_a_different_task_is_refused()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var concepts = await mine.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipe-concept-requests",
            new { audience = "weeknight cooks" },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, concepts.StatusCode);
        var otherTask = (await BodyOf(concepts)).GetProperty("aiProposalRequestId").GetGuid();

        var response = await AskAsync(mine, _fixture.WorkspaceA, Body(otherTask));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            AiImagePromptRequestErrors.ConceptNotFound,
            (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>A request the neighbour made cannot be read, and reads as absent rather than refused.</summary>
    [Fact]
    public async Task A_request_in_another_workspace_cannot_be_read()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var crossing = await mine.GetAsync($"{Route(_fixture.WorkspaceA)}/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
        Assert.Equal(
            AiImagePromptRequestErrors.RequestNotFound,
            (await BodyOf(crossing)).GetProperty("code").GetString());
    }

    /// <summary>A non-member is refused the route itself, before any id is looked at.</summary>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_ask_on_the_others_route()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var refused = await AskAsync(mine, _fixture.WorkspaceB, Body(Guid.NewGuid()));
        var unknown = await mine.PostAsJsonAsync(
            "/api/v1/workspaces/no-such-workspace/image-prompt-requests",
            Body(Guid.NewGuid()),
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(unknown.StatusCode, refused.StatusCode);
    }

    /// <summary>Composition spends the workspace's allowance, so a Viewer may not start one.</summary>
    [Fact]
    public async Task A_viewer_cannot_ask_for_a_prompt()
    {
        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: Ct);

        var asking = await AskAsync(viewer, _fixture.WorkspaceB, Body(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, asking.StatusCode);
    }

    /// <summary>Without an idempotency key a retry would buy a second prompt.</summary>
    [Fact]
    public async Task A_request_without_an_idempotency_key_is_refused()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            Route(_fixture.WorkspaceA), Body(Guid.NewGuid()), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A malformed request is refused before any id is resolved, and names the field.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_shot_is_refused_as_invalid()
    {
        using var client = await OwnerOf(_fixture.WorkspaceA);

        var response = await AskAsync(
            client,
            _fixture.WorkspaceA,
            new { conceptRequestId = Guid.NewGuid(), conceptId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            AiImagePromptRequestErrors.RequestInvalid,
            (await BodyOf(response)).GetProperty("code").GetString());
    }

    private static string Route(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/image-prompt-requests";

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    private static object Body(Guid conceptRequestId) => new
    {
        conceptRequestId,
        conceptId = Guid.NewGuid(),
        shotKind = "Hero",
    };

    private static Task<HttpResponseMessage> AskAsync(
        GatewayClient client, SeededWorkspace workspace, object body) =>
        client.PostAsJsonAsync(Route(workspace), body, Guid.NewGuid().ToString("N"), Ct);

    /// <summary>A queued photography-concept request of that workspace, so its id is real.</summary>
    private static async Task<Guid> ConceptRequestAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/photography-concept-requests",
            new { channelKey = "instagram" },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        return (await BodyOf(response)).GetProperty("aiProposalRequestId").GetGuid();
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <inheritdoc cref="PhotographyConceptIsolationEndpointTests"/>
    private static string WithoutTraceId(JsonElement body)
    {
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node.Remove("traceId");

        return node.ToJsonString();
    }
}
