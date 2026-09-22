extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CreatorPantry.Domain.Data;
using CreatorPantry.Domain.Tenancy;
using CreatorPantry.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CreatorPantry.Tests.Tenancy;

/// <summary>
/// The full seam for <c>GET /me</c>, <c>POST /workspaces</c>, and <c>GET/PATCH /workspaces/{slug}</c> over the
/// real <c>Program.cs</c> pipeline (real authentication, real authorization policies, real EF over SQLite).
/// </summary>
public sealed class WorkspaceManagementEndpointTests : IAsyncLifetime
{
    private SqliteApiHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await SqliteApiHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Creating_a_workspace_makes_the_caller_its_owner_atomically()
    {
        var userId = await _host.CreateUserAsync("cook@example.com", "correct horse battery");

        var response = await PostAsync("/api/v1/workspaces", userId, new { Name = "Sam's Kitchen" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/workspaces/sams-kitchen", response.Headers.Location?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("sams-kitchen", body.GetProperty("slug").GetString());
        Assert.Equal((int)WorkspaceRole.Owner, body.GetProperty("role").GetInt32());

        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var membership = await db.WorkspaceMemberships.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(userId, membership.UserId);
        Assert.Equal(WorkspaceRole.Owner, membership.Role);
    }

    [Fact]
    public async Task Creating_a_workspace_with_a_taken_name_gets_a_disambiguated_address()
    {
        var userId = await _host.CreateUserAsync("cook@example.com", "correct horse battery");
        await PostAsync("/api/v1/workspaces", userId, new { Name = "Sam's Kitchen" });

        var second = await PostAsync("/api/v1/workspaces", userId, new { Name = "Sam's Kitchen" });

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("sams-kitchen-2", body.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task An_empty_name_is_rejected_with_a_field_error()
    {
        var userId = await _host.CreateUserAsync("cook@example.com", "correct horse battery");

        var response = await PostAsync("/api/v1/workspaces", userId, new { Name = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("errors").TryGetProperty("name", out _));
    }

    [Fact]
    public async Task Me_lists_only_the_callers_own_memberships()
    {
        var userId = await _host.CreateUserAsync("cook@example.com", "correct horse battery");
        var otherUserId = await _host.CreateUserAsync("other@example.com", "correct horse battery");
        await PostAsync("/api/v1/workspaces", userId, new { Name = "Mine" });
        await PostAsync("/api/v1/workspaces", otherUserId, new { Name = "Not Mine" });

        var response = await GetAsync("/api/v1/me", userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var memberships = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var only = Assert.Single(memberships.EnumerateArray());
        Assert.Equal("mine", only.GetProperty("workspaceSlug").GetString());
        Assert.Equal((int)WorkspaceRole.Owner, only.GetProperty("role").GetInt32());
    }

    [Fact]
    public async Task A_member_can_read_the_workspace_they_belong_to()
    {
        var userId = await _host.CreateUserAsync("cook@example.com", "correct horse battery");
        await PostAsync("/api/v1/workspaces", userId, new { Name = "Sam's Kitchen" });

        var response = await GetAsync("/api/v1/workspaces/sams-kitchen", userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Sam's Kitchen", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_nonmember_gets_404_reading_a_workspace_they_do_not_belong_to()
    {
        var owner = await _host.CreateUserAsync("cook@example.com", "correct horse battery");
        var stranger = await _host.CreateUserAsync("stranger@example.com", "correct horse battery");
        await PostAsync("/api/v1/workspaces", owner, new { Name = "Sam's Kitchen" });

        var response = await GetAsync("/api/v1/workspaces/sams-kitchen", stranger);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_owner_can_rename_the_workspace_without_changing_its_address()
    {
        var userId = await _host.CreateUserAsync("cook@example.com", "correct horse battery");
        await PostAsync("/api/v1/workspaces", userId, new { Name = "Sam's Kitchen" });

        var response = await PatchAsync("/api/v1/workspaces/sams-kitchen", userId, new { Name = "Sam's Bakery" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Sam's Bakery", body.GetProperty("name").GetString());
        Assert.Equal("sams-kitchen", body.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task A_non_owner_member_cannot_rename_the_workspace()
    {
        var owner = await _host.CreateUserAsync("cook@example.com", "correct horse battery");
        var editor = await _host.CreateUserAsync("editor@example.com", "correct horse battery");
        await PostAsync("/api/v1/workspaces", owner, new { Name = "Sam's Kitchen" });
        await SeedMembershipAsync("sams-kitchen", editor, WorkspaceRole.Editor);

        var response = await PatchAsync("/api/v1/workspaces/sams-kitchen", editor, new { Name = "Hijacked" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task SeedMembershipAsync(string workspaceSlug, string userId, WorkspaceRole role)
    {
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var workspace = await db.Workspaces.SingleAsync(w => w.Slug == workspaceSlug);
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = userId,
            Role = role,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> GetAsync(string path, string userId) => SendAsync(HttpMethod.Get, path, userId, null);

    private Task<HttpResponseMessage> PostAsync(string path, string userId, object body) => SendAsync(HttpMethod.Post, path, userId, body);

    private Task<HttpResponseMessage> PatchAsync(string path, string userId, object body) => SendAsync(HttpMethod.Patch, path, userId, body);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string userId, object? body)
    {
        using var client = _host.Factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateUserToken(userId));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Mints a real ES256 internal user token, the same shape the gateway would sign (baseline B-13).</summary>
    private static string CreateUserToken(string userId)
    {
        // Not disposed: IdentityModel caches signature providers per key, and a disposed key poisons that cache.
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(TestKeyPair.Shared.PrivateKeyPem);
        var now = DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = InternalTokenDefaults.Issuer,
            Audience = InternalTokenDefaults.Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim(InternalTokenDefaults.TokenUseClaim, InternalTokenDefaults.UserTokenUse),
            ]),
            NotBefore = now,
            IssuedAt = now,
            Expires = now.AddMinutes(1),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(ecdsa), SecurityAlgorithms.EcdsaSha256),
        });
    }
}
