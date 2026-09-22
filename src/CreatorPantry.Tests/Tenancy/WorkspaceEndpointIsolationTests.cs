using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Tenancy;

namespace CreatorPantry.Tests.Tenancy;

/// <summary>
/// Cross-workspace isolation for the 2.8 workspace management endpoints, entirely through HTTP against the
/// real Gateway and API pipeline (real cookie session, real gateway-signed internal token, real membership
/// policies) — a repository-level test would not exercise any of that, and is not isolation coverage
/// (tenancy.md). Uses the shared <see cref="TwoWorkspaceGatewayFixture"/> so this file only adds the
/// assertions specific to these routes.
/// </summary>
/// <remarks>
/// Covers tenancy.md's "detail" and "mutation" isolation legs directly (<c>GET</c>/<c>PATCH
/// /workspaces/{slug}</c>). <c>GET /me</c> proves a user only ever sees their own memberships, which is
/// necessary but is a cross-workspace membership query, not the strict per-entity
/// <c>WorkspaceOwnershipConvention</c> filter — that filter still needs its own list-isolation test against
/// the first real workspace-scoped resource (recipes, media, ...) once one exists. Search/cache/background
/// processing/AI retrieval don't apply to these endpoints and are correctly out of scope here.
/// </remarks>
public sealed class WorkspaceEndpointIsolationTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Me_lists_only_the_callers_own_workspace_with_the_correct_role()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellationToken);
        using var editorA = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: cancellationToken);
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellationToken);

        var ownerAMemberships = await GetMembershipsAsync(ownerA, cancellationToken);
        var editorAMemberships = await GetMembershipsAsync(editorA, cancellationToken);
        var ownerBMemberships = await GetMembershipsAsync(ownerB, cancellationToken);

        AssertSingleMembership(ownerAMemberships, _fixture.WorkspaceA.Id, WorkspaceRole.Owner);
        AssertSingleMembership(editorAMemberships, _fixture.WorkspaceA.Id, WorkspaceRole.Editor);
        AssertSingleMembership(ownerBMemberships, _fixture.WorkspaceB.Id, WorkspaceRole.Owner);
    }

    [Fact]
    public async Task A_member_can_read_their_own_workspace_but_not_the_other_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellationToken);

        var own = await ownerA.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}", cancellationToken);
        var other = await ownerA.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var body = await own.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(_fixture.WorkspaceA.Id, body.GetProperty("workspaceId").GetGuid());

        // Not 403: a nonmember must not learn the other workspace exists at all.
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task Reading_the_other_similarly_named_workspace_never_returns_the_wrong_workspaces_data()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // Both workspaces share a display name (TwoWorkspaceGatewayFixture): only WorkspaceId-correct code
        // can tell them apart, so a name/slug-pattern isolation bug would show up here.
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellationToken);
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellationToken);

        var asOwnerA = await (await ownerA.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}", cancellationToken))
            .Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var asOwnerB = await (await ownerB.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}", cancellationToken))
            .Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        Assert.Equal(_fixture.WorkspaceA.Name, asOwnerA.GetProperty("name").GetString());
        Assert.Equal(_fixture.WorkspaceB.Name, asOwnerB.GetProperty("name").GetString());
        Assert.NotEqual(_fixture.WorkspaceA.Id, asOwnerB.GetProperty("workspaceId").GetGuid());
        Assert.NotEqual(_fixture.WorkspaceB.Id, asOwnerA.GetProperty("workspaceId").GetGuid());
    }

    [Fact]
    public async Task Only_the_owner_can_rename_their_workspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellationToken);
        using var editorA = await _fixture.SignInAsync(_fixture.WorkspaceA.MemberEmail, cancellationToken: cancellationToken); // a member, but not Owner

        var byOwner = await ownerA.PatchAsJsonAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}", new { Name = "Renamed by owner" }, cancellationToken);
        var byEditor = await editorA.PatchAsJsonAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}", new { Name = "Renamed by editor" }, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byEditor.StatusCode);
    }

    [Fact]
    public async Task A_nonmember_cannot_rename_the_other_workspace_and_gets_404_not_403()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellationToken);

        var response = await ownerB.PatchAsJsonAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}", new { Name = "Hijacked" }, cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Renaming_one_workspace_never_touches_the_other_similarly_named_workspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellationToken);
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellationToken);

        await ownerA.PatchAsJsonAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}", new { Name = "Sam's Kitchen (renamed)" }, cancellationToken);

        var stillB = await (await ownerB.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}", cancellationToken))
            .Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(_fixture.WorkspaceB.Name, stillB.GetProperty("name").GetString());
    }

    private static async Task<JsonElement> GetMembershipsAsync(GatewayClient client, CancellationToken cancellationToken) =>
        await (await client.GetAsync("/api/v1/me", cancellationToken)).Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

    private static void AssertSingleMembership(JsonElement memberships, Guid expectedWorkspaceId, WorkspaceRole expectedRole)
    {
        var only = Assert.Single(memberships.EnumerateArray());
        Assert.Equal(expectedWorkspaceId, only.GetProperty("workspaceId").GetGuid());
        // Role is currently serialized as its numeric value.
        Assert.Equal((int)expectedRole, only.GetProperty("role").GetInt32());
    }
}
