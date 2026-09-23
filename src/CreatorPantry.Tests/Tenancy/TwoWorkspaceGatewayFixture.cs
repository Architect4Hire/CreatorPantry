extern alias Gateway;

using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Tenancy;

/// <summary>
/// Two workspaces behind the real Gateway (real cookie session, real gateway-signed internal token, real API
/// over SQLite) — the standard starting point for any workspace feature's isolation tests
/// (tenancy.md: "Every workspace feature includes a test with Workspace A and Workspace B..."). Both
/// workspaces are created through the real <c>POST /api/v1/workspaces</c> with the same name, so their
/// slugs are only as similar as the product's own disambiguation makes them (<c>sams-kitchen</c>,
/// <c>sams-kitchen-2</c>) — a more honest test that isolation is keyed by <c>WorkspaceId</c>, not by
/// name/slug pattern, than hand-picked similar strings would be. Each workspace has its creating Owner plus
/// one lower-role member.
/// </summary>
/// <remarks>
/// Reusable as-is by any feature that just needs two isolated workspaces with an Owner and one lower-role
/// member each (recipes, media, content projects, ...). It hardcodes that one role pair (Editor/Viewer) per
/// workspace; a feature that needs a different role combination, a bare workspace with no second member, or
/// more than two workspaces needs its own fixture rather than stretching this one.
/// </remarks>
internal sealed class TwoWorkspaceGatewayFixture : IAsyncDisposable
{
    private const string SeedPassword = "correct horse battery";
    private const string SharedWorkspaceName = "Sam's Kitchen";

    private TwoWorkspaceGatewayFixture(SqliteApiHost api, WebApplicationFactory<Gateway::Program> gateway)
    {
        Api = api;
        Gateway = gateway;
    }

    public SqliteApiHost Api { get; }

    public WebApplicationFactory<Gateway::Program> Gateway { get; }

    public SeededWorkspace WorkspaceA { get; private set; } = null!;

    public SeededWorkspace WorkspaceB { get; private set; } = null!;

    public static async Task<TwoWorkspaceGatewayFixture> CreateAsync()
    {
        var api = await SqliteApiHost.StartAsync();
        var gateway = GatewayTestHost.Create(proxyDownstream: api.Factory.Server.CreateHandler, apiSessions: api.Factory.Server.CreateHandler);
        var fixture = new TwoWorkspaceGatewayFixture(api, gateway);

        fixture.WorkspaceA = await fixture.SeedWorkspaceAsync("owner-a@example.com", "editor-a@example.com", WorkspaceRole.Editor);
        fixture.WorkspaceB = await fixture.SeedWorkspaceAsync("owner-b@example.com", "viewer-b@example.com", WorkspaceRole.Viewer);

        return fixture;
    }

    public async ValueTask DisposeAsync()
    {
        Gateway.Dispose();
        await Api.DisposeAsync();
    }

    /// <summary>
    /// Signs in through the real Gateway BFF (antiforgery token, then <c>/bff/login</c>) and returns a client
    /// carrying the resulting session cookie, ready to call any <c>/api/v1/...</c> route through the gateway.
    /// </summary>
    public async Task<GatewayClient> SignInAsync(
        string email, string password = SeedPassword, CancellationToken cancellationToken = default)
    {
        var http = Gateway.CreateClient(GatewayTestHost.HttpsClient);
        try
        {
            var anonymousToken = await AntiforgeryTokenAsync(http, cancellationToken);

            using var login = new HttpRequestMessage(HttpMethod.Post, "/bff/login") { Content = JsonContent.Create(new { email, password }) };
            login.Headers.Add("X-XSRF-TOKEN", anonymousToken);
            var response = await http.SendAsync(login, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Sign-in failed for '{email}': {response.StatusCode}.");
            }

            // A fresh token: the anonymous one used to sign in is bound to the pre-login user and is stale now.
            var signedInToken = await AntiforgeryTokenAsync(http, cancellationToken);
            return new GatewayClient(http, signedInToken);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    private async Task<SeededWorkspace> SeedWorkspaceAsync(string ownerEmail, string memberEmail, WorkspaceRole memberRole)
    {
        await Api.CreateUserAsync(ownerEmail, SeedPassword);
        var memberUserId = await Api.CreateUserAsync(memberEmail, SeedPassword);

        using var owner = await SignInAsync(ownerEmail);
        var created = await owner.PostAsJsonAsync("/api/v1/workspaces", new { Name = SharedWorkspaceName });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var workspaceId = body.GetProperty("workspaceId").GetGuid();
        var slug = body.GetProperty("slug").GetString()!;

        await using (var scope = Api.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.WorkspaceMemberships.Add(new WorkspaceMembership
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = memberUserId,
                Role = memberRole,
                Status = WorkspaceMembershipStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        return new SeededWorkspace(workspaceId, slug, SharedWorkspaceName, ownerEmail, memberEmail, memberRole);
    }

    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, CancellationToken cancellationToken) =>
        (await client.GetFromJsonAsync<JsonElement>("/bff/antiforgery", cancellationToken)).GetProperty("requestToken").GetString()!;
}

/// <param name="Id">The workspace's id — the ground truth every isolation assertion should key on.</param>
/// <param name="Slug">The route segment. Deliberately similar to the other seeded workspace's slug.</param>
/// <param name="Name">Deliberately identical to the other seeded workspace's name.</param>
/// <param name="OwnerEmail">Signs in as this workspace's Owner.</param>
/// <param name="MemberEmail">Signs in as a member with <paramref name="MemberRole"/>, below Owner.</param>
internal sealed record SeededWorkspace(Guid Id, string Slug, string Name, string OwnerEmail, string MemberEmail, WorkspaceRole MemberRole);

/// <summary>A cookie-authenticated client for one signed-in user, through the real Gateway.</summary>
internal sealed class GatewayClient(HttpClient http, string antiforgeryToken) : IDisposable
{
    public Task<HttpResponseMessage> GetAsync(string path, CancellationToken cancellationToken = default) =>
        http.GetAsync(path, cancellationToken);

    public Task<HttpResponseMessage> PostAsJsonAsync(string path, object body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, path, body, cancellationToken);

    public Task<HttpResponseMessage> PatchAsJsonAsync(string path, object body, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, path, body, cancellationToken);

    public void Dispose() => http.Dispose();

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-XSRF-TOKEN", antiforgeryToken);
        return http.SendAsync(request, cancellationToken);
    }
}
