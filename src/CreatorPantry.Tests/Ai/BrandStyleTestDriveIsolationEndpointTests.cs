using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// 11A.24's test-drive route through the real Gateway, across two workspaces (audit 11A.24a).
/// </summary>
/// <remarks>
/// <para>
/// Isolation for this capability was proven one layer down — <c>AiBrandStyleTestDriveBusinessTests</c> for the
/// request and the read, <c>BrandStyleTestDriveAiTaskHandlerTests</c> for the grounding — and nowhere through
/// the route. tenancy.md asks for the boundary to be proven per feature, and a business test cannot show that
/// the policy, the route template and the workspace middleware are wired the way the controller claims.
/// </para>
/// <para>
/// Separate from <c>BrandStyleTestDriveEndpointTests</c>, which is contract and validator work and needs no
/// database: a gateway fixture per test is expensive, and the tests that do not need one should not pay for it.
/// </para>
/// <para>
/// Nothing here reaches a provider. The test host runs no worker, so an accepted request stays queued — which
/// is all these assertions need.
/// </para>
/// </remarks>
public sealed class BrandStyleTestDriveIsolationEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// A guide belonging to the neighbour is answered exactly as one that never existed, so nothing about it is
    /// disclosed — not that it exists, and not whose it is.
    /// </summary>
    [Fact]
    public async Task A_guide_in_another_workspace_is_answered_as_one_that_does_not_exist()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);
        using var theirs = await OwnerOf(_fixture.WorkspaceB);

        var neighbours = await CreateGuideAsync(theirs, _fixture.WorkspaceB);

        var crossing = await AskAsync(mine, _fixture.WorkspaceA, neighbours);
        var missing = await AskAsync(mine, _fixture.WorkspaceA, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await CodeOf(missing), await CodeOf(crossing));
    }

    /// <summary>A test drive the neighbour asked for cannot be read, and reads as absent rather than refused.</summary>
    [Fact]
    public async Task A_test_drive_in_another_workspace_cannot_be_read()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);
        using var theirs = await OwnerOf(_fixture.WorkspaceB);

        var neighbours = await CreateGuideAsync(theirs, _fixture.WorkspaceB);
        var accepted = await AskAsync(theirs, _fixture.WorkspaceB, neighbours);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var requestId = (await BodyOf(accepted)).GetProperty("requestId").GetGuid();

        var crossing = await mine.GetAsync(
            $"{Route(_fixture.WorkspaceA)}/{requestId}", TestContext.Current.CancellationToken);
        var missing = await mine.GetAsync(
            $"{Route(_fixture.WorkspaceA)}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, crossing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await CodeOf(missing), await CodeOf(crossing));
    }

    /// <summary>
    /// Reading the neighbour's workspace by slug is refused before the route is reached at all, which is the
    /// middleware's job rather than the controller's.
    /// </summary>
    [Fact]
    public async Task Naming_another_workspace_in_the_route_is_refused()
    {
        using var mine = await OwnerOf(_fixture.WorkspaceA);

        var response = await mine.GetAsync(
            $"{Route(_fixture.WorkspaceB)}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- harness ----

    private static string Route(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-style-test-drives";

    private static string GuidesIn(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/brand-style-guides";

    private async Task<GatewayClient> OwnerOf(SeededWorkspace workspace) =>
        await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> AskAsync(
        GatewayClient client, SeededWorkspace workspace, Guid guideId) =>
        client.PostAsJsonAsync(
            Route(workspace),
            new { guideId, versionNumber = 1 },
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

    /// <summary>A guide with one written section, so a test drive against it has something to demonstrate.</summary>
    private static async Task<Guid> CreateGuideAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var response = await client.PostAsJsonAsync(
            GuidesIn(workspace),
            // The questionnaire alone: its voice answer becomes the Voice section, which is what gives the test
            // drive something to demonstrate. Sending a Voice section as well is refused as the same part twice.
            new
            {
                displayName = "House voice",
                questionnaire = new { voice = "Warm, direct, never fussy." },
            },
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await BodyOf(response)).GetProperty("code").GetString();
}
