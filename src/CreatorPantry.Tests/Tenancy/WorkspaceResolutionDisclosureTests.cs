extern alias ApiService;

using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
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
using CreatorPantry.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CreatorPantry.Tests.Tenancy;

/// <summary>
/// Proves the disclosure guarantee end to end through the real <c>Program.cs</c> pipeline (not the bespoke
/// host in <see cref="WorkspaceResolutionMiddlewareTests"/>): unknown workspace, no membership, inactive
/// membership, and a route that never matched any endpoint at all must render byte-for-byte the same
/// ProblemDetails body, not just the same status code.
/// </summary>
public sealed class WorkspaceResolutionDisclosureTests : IAsyncLifetime
{
    private SqliteApiHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await SqliteApiHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Unknown_nonmember_inactive_and_a_genuinely_unmatched_route_render_identical_problem_bodies()
    {
        var userId = await _host.CreateUserAsync("cook@example.com", "correct horse battery");
        var workspaceId = await SeedWorkspaceAsync("sams-kitchen");
        await SeedMembershipAsync(workspaceId, userId, WorkspaceMembershipStatus.Invited);

        var unknown = await GetProblemAsync("/api/v1/workspaces/no-such-workspace/anything", userId);
        var nonmember = await GetProblemAsync("/api/v1/workspaces/sams-kitchen/anything", await _host.CreateUserAsync("other@example.com", "correct horse battery"));
        var inactive = await GetProblemAsync("/api/v1/workspaces/sams-kitchen/anything", userId);

        // A path that never matches the workspace shape at all, and never matches any endpoint either: the
        // framework's own "no route" 404, produced by a completely different code path than the middleware.
        var unmatchedRoute = await GetProblemAsync("/api/v1/totally-unrelated-path", userId);

        AssertIdenticalShape(unknown, nonmember);
        AssertIdenticalShape(unknown, inactive);
        AssertIdenticalShape(unknown, unmatchedRoute);
    }

    private static void AssertIdenticalShape(ProblemBody left, ProblemBody right)
    {
        Assert.Equal(left.Status, right.Status);
        Assert.Equal(left.Title, right.Title);
        Assert.Equal(left.Code, right.Code);
        Assert.Equal(404, left.StatusCode); // sanity: both are genuinely 404s, not some other transport error
        Assert.Equal(left.StatusCode, right.StatusCode);
    }

    private async Task<ProblemBody> GetProblemAsync(string path, string userId)
    {
        using var client = _host.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", CreateUserToken(userId));

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        return new ProblemBody(
            (int)response.StatusCode,
            body.TryGetProperty("status", out var status) ? status.GetInt32() : null,
            body.TryGetProperty("title", out var title) ? title.GetString() : null,
            body.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private async Task<Guid> SeedWorkspaceAsync(string slug)
    {
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = slug, Slug = slug, CreatedAt = DateTimeOffset.UtcNow };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        return workspace.Id;
    }

    private async Task SeedMembershipAsync(Guid workspaceId, string userId, WorkspaceMembershipStatus status)
    {
        await using var scope = _host.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Owner,
            Status = status,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
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

    private sealed record ProblemBody(int StatusCode, int? Status, string? Title, string? Code);
}
