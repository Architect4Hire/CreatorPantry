using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The piece of work a concept request may name (AF.7.4), through the real Gateway and across two workspaces.
/// </summary>
/// <remarks>
/// A concept request used to carry nothing a client could point at another workspace with: twelve fields of
/// the creator's own text. It can now name a creative context by id, so the route owes the proof tenancy.md
/// asks of every workspace feature — a neighbour's id is answered as one that does not exist, in the same
/// words, before anything is queued. Nothing here reaches a provider: the test host runs no worker.
/// </remarks>
public sealed class RecipeConceptContextIsolationEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_piece_of_work_in_another_workspace_is_answered_as_one_that_does_not_exist()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);
        using var theirs = await OwnerOf(_fixture.WorkspaceB);

        var neighbours = await CreateContextAsync(theirs, _fixture.WorkspaceB);

        var crossing = await AskAsync(mine, _fixture.WorkspaceA, new { course = "main", creativeContextId = neighbours });
        var missing = await AskAsync(mine, _fixture.WorkspaceA, new { course = "main", creativeContextId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
        Assert.Equal(missing.StatusCode, crossing.StatusCode);

        // Each body is read once: the content stream is consumed, so a second read would fail rather than
        // compare.
        var crossingBody = await BodyOf(crossing);
        var missingBody = await BodyOf(missing);

        Assert.Equal(AiConceptRequestErrors.ContextNotFound, crossingBody.GetProperty("code").GetString());

        // The whole body, not just the code: two refusals that matched on status and code but differed in
        // their message would disclose exactly what the matching status hides.
        Assert.Equal(WithoutTraceId(missingBody), WithoutTraceId(crossingBody));
    }

    [Fact]
    public async Task The_callers_own_piece_of_work_is_accepted_with_or_without_its_days_notes()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);
        var own = await CreateContextAsync(mine, _fixture.WorkspaceA);

        var followed = await AskAsync(mine, _fixture.WorkspaceA, new { course = "main", creativeContextId = own });
        var switchedOff = await AskAsync(
            mine, _fixture.WorkspaceA, new { course = "main", creativeContextId = own, applyDayNotes = false });

        Assert.Equal(HttpStatusCode.Accepted, followed.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, switchedOff.StatusCode);
    }

    private static string Route(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipe-concept-requests";

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    private static Task<HttpResponseMessage> AskAsync(GatewayClient client, SeededWorkspace workspace, object body) =>
        client.PostAsJsonAsync(Route(workspace), body, Guid.NewGuid().ToString("N"), Ct);

    private static async Task<Guid> CreateContextAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/creative-contexts",
            new { workingTitle = "Monday supper" },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string WithoutTraceId(JsonElement body)
    {
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node.Remove("traceId");

        return node.ToJsonString();
    }
}
