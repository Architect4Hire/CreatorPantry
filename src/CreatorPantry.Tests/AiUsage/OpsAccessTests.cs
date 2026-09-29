using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.ServiceDefaults;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// Who may reach the ops quota routes (USAGE-009, baseline B-14): an ops API key with the right scope, and
/// nothing else — not an anonymous caller, not a signed-in creator, not a Workspace Owner, and not a
/// PlatformAdmin's browser session.
/// </summary>
public sealed class OpsAccessTests
{
    private const string Account = "/api/v1/ops/ai-usage/accounts/user-sam";
    private const string TopConsumers = "/api/v1/ops/ai-usage/top-consumers?from=2026-01-01T00:00:00Z&to=2026-02-01T00:00:00Z";
    private const string Quota = "/api/v1/ops/ai-usage/accounts/user-sam/quota";
    private const string Suspend = "/api/v1/ops/ai-usage/accounts/user-sam/ai-access/suspend";
    private const string Restore = "/api/v1/ops/ai-usage/accounts/user-sam/ai-access/restore";
    private const string Clear = "/api/v1/ops/ai-usage/accounts/user-sam/quota/clear";

    public static TheoryData<string> Reads => [Account, TopConsumers];

    public static TheoryData<string> Commands => [Clear, Suspend, Restore];

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task An_anonymous_caller_is_refused(string route)
    {
        await using var host = await SqliteApiHost.StartAsync();

        var response = await host.Factory.CreateClient().GetAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// <strong>The non-admin case, and the load-bearing one.</strong> A gateway-minted user token is what every
    /// product route accepts, and it opens nothing here — because the ops policy names its own authentication
    /// scheme, so the JWT scheme is never even consulted on these routes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Reads))]
    public async Task A_signed_in_creators_token_is_refused(string route)
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("user-sam"));

        var response = await client.GetAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A platform administrator's browser session is still a browser session. <c>PlatformAdmin</c> is an
    /// Identity role that says who a person is; an ops key is a machine credential, and B-14 says the two are
    /// not interchangeable. Holding the role buys nothing here.
    /// </summary>
    [Theory]
    [MemberData(nameof(Reads))]
    public async Task A_platform_admins_session_token_is_refused(string route)
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", UserToken("user-sam", PlatformRoles.PlatformAdmin));

        var response = await client.GetAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// <strong>The workspace-owner case.</strong> Owner is the highest role a workspace has, and it is a role
    /// <em>in a workspace</em> — the quota it would be administering belongs to an account that may work in
    /// five of them. A token carrying it is refused exactly as any other creator's is.
    /// </summary>
    [Theory]
    [MemberData(nameof(Reads))]
    public async Task A_workspace_owners_token_is_refused(string route)
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", UserToken("user-sam", "Owner"));

        var response = await client.GetAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The commands are barred the same way the reads are, and none of them changes anything first.</summary>
    [Theory]
    [MemberData(nameof(Commands))]
    public async Task A_creators_token_cannot_run_a_command(string route)
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", "Correct horse battery staple");

        var client = host.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", UserToken(accountId, PlatformRoles.PlatformAdmin));

        var response = await client.PostAsJsonAsync(
            route, new { reason = "Trying it on." }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A key nobody issued, a key whose secret is wrong, and a credential of another shape.</summary>
    [Theory]
    [InlineData("cpops_unknown.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("cpops_malformed-no-separator")]
    [InlineData("not-one-of-ours")]
    [InlineData("")]
    public async Task An_invalid_key_is_refused(string key)
    {
        await using var host = await SqliteApiHost.StartAsync();
        await host.CreateOpsClientAsync();

        var response = await host.CreateOpsClient(key).GetAsync(Account, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The right client, the wrong secret. The prefix identifies a real row, so this is the case a scan of
    /// hashes would get wrong; it is refused on the constant-time comparison instead.
    /// </summary>
    [Fact]
    public async Task A_valid_prefix_with_the_wrong_secret_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var key = await host.CreateOpsClientAsync();
        var prefix = key[..key.IndexOf('.', StringComparison.Ordinal)];

        var response = await host
            .CreateOpsClient($"{prefix}.{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=')}")
            .GetAsync(Account, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>A revoked client authenticates nothing, key or not.</summary>
    [Fact]
    public async Task A_revoked_clients_key_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", "Correct horse battery staple");
        var key = await host.CreateOpsClientAsync("operator");

        var granted = await host.CreateOpsClient(key)
            .GetAsync($"/api/v1/ops/ai-usage/accounts/{accountId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);

        await host.RevokeOpsClientAsync("operator");

        var refused = await host.CreateOpsClient(key)
            .GetAsync($"/api/v1/ops/ai-usage/accounts/{accountId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    /// <summary>
    /// Rotation invalidates the previous key at the instant the new hash commits. That is the whole reason a
    /// key is rotatable rather than merely replaceable.
    /// </summary>
    [Fact]
    public async Task Rotating_a_key_invalidates_the_previous_one()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", "Correct horse battery staple");
        var route = $"/api/v1/ops/ai-usage/accounts/{accountId}";

        var first = await host.CreateOpsClientAsync("operator");
        var second = await host.CreateOpsClientAsync("operator");

        var stale = await host.CreateOpsClient(first).GetAsync(route, TestContext.Current.CancellationToken);
        var fresh = await host.CreateOpsClient(second).GetAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    /// <summary>
    /// A valid key with the wrong scope is a 403, not a 401: the caller is who they say they are and still may
    /// not do this. An automation issued a narrower grant cannot widen it by holding a key.
    /// </summary>
    [Fact]
    public async Task A_key_without_the_scope_is_forbidden()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", "Correct horse battery staple");
        var key = await host.CreateOpsClientAsync("narrow", "some-other-scope");

        var response = await host.CreateOpsClient(key)
            .GetAsync($"/api/v1/ops/ai-usage/accounts/{accountId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// An ops key opens ops routes and nothing else. Presented at a product route it is not a credential at
    /// all: that route's policy demands <c>token_use=user</c>, which an ops principal never carries.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/me")]
    [InlineData("/api/v1/me/ai-usage")]
    [InlineData("/api/v1/me/ai-usage/history")]
    public async Task An_ops_key_opens_no_product_route(string route)
    {
        await using var host = await SqliteApiHost.StartAsync();
        var key = await host.CreateOpsClientAsync();

        var response = await host.CreateOpsClient(key).GetAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Mints a real ES256 internal token, signed the way the gateway would sign one (baseline B-13).</summary>
    private static string UserToken(string userId, params string[] roles)
    {
        // Not disposed: IdentityModel caches signature providers per key, and a disposed key poisons that cache.
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(TestKeyPair.Shared.PrivateKeyPem);
        var now = DateTime.UtcNow;

        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, userId),
            new(InternalTokenDefaults.TokenUseClaim, InternalTokenDefaults.UserTokenUse),

            // The claim type the API's RoleClaimType names, not ClaimTypes.Role: MapInboundClaims is off.
            .. roles.Select(role => new Claim(InternalTokenDefaults.RoleClaim, role)),
        ];

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
}
