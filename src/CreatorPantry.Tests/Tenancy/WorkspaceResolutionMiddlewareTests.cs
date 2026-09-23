extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using ApiService::CreatorPantry.ApiService.Authorization;
using ApiService::CreatorPantry.ApiService.Tenancy;
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
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CreatorPantry.Tests.Tenancy;

/// <summary>
/// End-to-end pipeline coverage: real internal-token authentication, real authorization, and the real
/// resolution seam (over SQLite) behind <see cref="WorkspaceResolutionMiddleware"/>. No workspace-scoped
/// controller exists yet, so this stands up two minimal probe endpoints of its own.
/// </summary>
public sealed class WorkspaceResolutionMiddlewareTests : IAsyncLifetime
{
    private const string OtherRoute = "/api/v1/account/probe";

    private WorkspaceMiddlewareHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await WorkspaceMiddlewareHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Valid_member_reaches_the_endpoint_with_a_resolved_context()
    {
        var userId = await _host.SeedUserAsync("u1");
        var workspaceId = await _host.SeedWorkspaceAsync("sams-kitchen");
        var membershipId = await _host.SeedMembershipAsync(workspaceId, userId, WorkspaceRole.Editor, WorkspaceMembershipStatus.Active);

        var response = await GetAsync("/api/v1/workspaces/sams-kitchen/probe", userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var probe = await response.Content.ReadFromJsonAsync<ProbeResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(workspaceId, probe!.WorkspaceId);
        Assert.Equal("sams-kitchen", probe.WorkspaceSlug);
        Assert.Equal(membershipId, probe.MembershipId);
        Assert.Equal("Editor", probe.Role);
    }

    [Fact]
    public async Task Nonmember_gets_404_and_never_reaches_the_endpoint()
    {
        var userId = await _host.SeedUserAsync("u1");
        await _host.SeedWorkspaceAsync("sams-kitchen");
        // No membership row for this user.

        var response = await GetAsync("/api/v1/workspaces/sams-kitchen/probe", userId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(WorkspaceMembershipStatus.Invited)]
    [InlineData(WorkspaceMembershipStatus.Removed)]
    public async Task Inactive_membership_gets_404(WorkspaceMembershipStatus status)
    {
        var userId = await _host.SeedUserAsync("u1");
        var workspaceId = await _host.SeedWorkspaceAsync("sams-kitchen");
        await _host.SeedMembershipAsync(workspaceId, userId, WorkspaceRole.Owner, status);

        var response = await GetAsync("/api/v1/workspaces/sams-kitchen/probe", userId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_workspace_gets_404()
    {
        var userId = await _host.SeedUserAsync("u1");
        // No workspace row for this slug at all.

        var response = await GetAsync("/api/v1/workspaces/no-such-workspace/probe", userId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_workspace_less_route_is_left_unresolved_and_still_succeeds()
    {
        var userId = await _host.SeedUserAsync("u1");

        var response = await GetAsync(OtherRoute, userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var probe = await response.Content.ReadFromJsonAsync<ResolvedFlag>(TestContext.Current.CancellationToken);
        Assert.False(probe!.IsResolved);
    }

    [Fact]
    public async Task No_token_is_rejected_by_authorization_not_the_middleware()
    {
        using var client = _host.CreateClient();

        var response = await client.GetAsync("/api/v1/workspaces/sams-kitchen/probe", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_gateway_service_token_is_never_treated_as_a_user_id()
    {
        using var client = _host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/workspaces/sams-kitchen/probe");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateServiceToken());

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string userId)
    {
        using var client = _host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateUserToken(userId));

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Mints a real ES256 internal token, signed the way the gateway would sign one (baseline B-13).</summary>
    private static string CreateUserToken(string userId) => CreateToken(
        new Claim(JwtRegisteredClaimNames.Sub, userId),
        new Claim(InternalTokenDefaults.TokenUseClaim, InternalTokenDefaults.UserTokenUse));

    private static string CreateServiceToken() => CreateToken(
        new Claim(JwtRegisteredClaimNames.Sub, InternalTokenDefaults.GatewayServiceSubject),
        new Claim(InternalTokenDefaults.TokenUseClaim, InternalTokenDefaults.ServiceTokenUse));

    private static string CreateToken(params Claim[] claims)
    {
        // Not disposed: IdentityModel caches signature providers per key, and a disposed key poisons that cache.
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(TestKeyPair.Shared.PrivateKeyPem);
        var now = DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = InternalTokenDefaults.Issuer,
            Audience = InternalTokenDefaults.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now,
            IssuedAt = now,
            Expires = now.AddMinutes(1),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(ecdsa), SecurityAlgorithms.EcdsaSha256),
        });
    }

    private sealed record ProbeResponse(Guid WorkspaceId, string WorkspaceSlug, Guid MembershipId, string Role);

    private sealed record ResolvedFlag(bool IsResolved);
}

/// <summary>The real middleware and resolution seam over an in-memory SQLite database, with two probe endpoints.</summary>
internal sealed class WorkspaceMiddlewareHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private WebApplication _app = null!;

    private WorkspaceMiddlewareHost()
    {
    }

    public static async Task<WorkspaceMiddlewareHost> StartAsync()
    {
        var host = new WorkspaceMiddlewareHost();
        await host._connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["InternalToken:PublicKeyPem"] = TestKeyPair.Shared.PublicKeyPem;

        builder.Services.AddApplicationTime();
        builder.Services.AddDbContext<CreatorPantryDbContext>(options => options.UseSqlite(host._connection));
        builder.Services.AddTenancy();
        builder.Services.AddInternalTokenAuthentication(builder.Configuration);
        builder.Services.AddCreatorPantryAuthorization();

        host._app = builder.Build();
        host._app.UseAuthentication();
        host._app.UseWorkspaceResolution();
        host._app.UseAuthorization();

        host._app.MapGet("/api/v1/workspaces/{workspaceSlug}/probe", (IWorkspaceContext context) =>
            Results.Ok(new { context.WorkspaceId, context.WorkspaceSlug, context.MembershipId, Role = context.Role.ToString() }))
            .RequireAuthorization();

        host._app.MapGet("/api/v1/account/probe", (IWorkspaceContext context) =>
            Results.Ok(new { context.IsResolved }))
            .RequireAuthorization();

        await using (var scope = host._app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreatedAsync();
        }

        await host._app.StartAsync();
        return host;
    }

    public HttpClient CreateClient() => _app.GetTestClient();

    public async Task<string> SeedUserAsync(string userId)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@example.com", Email = $"{userId}@example.com" });
        await db.SaveChangesAsync();
        return userId;
    }

    public async Task<Guid> SeedWorkspaceAsync(string slug)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = slug, Slug = slug, CreatedAt = DateTimeOffset.UtcNow };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        return workspace.Id;
    }

    public async Task<Guid> SeedMembershipAsync(Guid workspaceId, string userId, WorkspaceRole role, WorkspaceMembershipStatus status)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var membership = new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = role,
            Status = status,
            JoinedAt = DateTimeOffset.UtcNow,
        };
        db.WorkspaceMemberships.Add(membership);
        await db.SaveChangesAsync();
        return membership.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
