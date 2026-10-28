using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// Ops administration of the credits granted to an account beyond its allowance (PRICE-008, B-50, AP.5.1): grant
/// them by hand, list them, and claw back what is unspent — each audited, none of it reachable by a creator.
/// </summary>
/// <remarks>
/// Against the real API host, because the half of this worth testing here is the wire, the idempotency key and the
/// authorization. The schema, the claw-back's arithmetic and every race are proved against SQL Server in
/// <see cref="AccountCreditGrantSqlServerTests"/>, where a row version actually moves.
/// </remarks>
public sealed class OpsCreditGrantTests
{
    private const string Password = "Correct horse battery staple";
    private const string Route = "/api/v1/ops/ai-usage/accounts";

    // ---- granting ----------------------------------------------------------------------------------------

    /// <summary>
    /// A grant is created, answered 201 with where to read it, listed under the account with the unspent total,
    /// and audited once with the operator, the reason and the figure.
    /// </summary>
    [Fact]
    public async Task A_grant_is_created_listed_and_audited()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync("acme-support"));

        var response = await Grant(ops, accountId, "key-1", new { credits = 500, source = "Ops", reason = "Goodwill for the outage" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("Granted", body.GetProperty("outcome").GetString());
        var grant = body.GetProperty("grant");
        var grantId = grant.GetProperty("id").GetGuid();
        Assert.Equal(500m, grant.GetProperty("creditsGranted").GetDecimal());
        Assert.Equal(500m, grant.GetProperty("creditsRemaining").GetDecimal());
        Assert.Equal(0m, grant.GetProperty("creditsClawedBack").GetDecimal());
        Assert.Equal(JsonValueKind.Null, grant.GetProperty("clawedBackAt").ValueKind);
        Assert.EndsWith($"/accounts/{accountId}/credit-grants/{grantId:D}", response.Headers.Location!.ToString());

        // Where the Location says, and in the list, with nothing that expires (B-50).
        var read = await BodyOf(await ops.GetAsync(response.Headers.Location, Ct));
        Assert.Equal(grantId, read.GetProperty("id").GetGuid());
        Assert.False(read.TryGetProperty("expiresAt", out _));

        var list = await BodyOf(await ops.GetAsync($"{Route}/{accountId}/credit-grants", Ct));
        Assert.Equal(500m, list.GetProperty("creditsRemaining").GetDecimal());
        Assert.Equal(grantId, Assert.Single(list.GetProperty("grants").EnumerateArray()).GetProperty("id").GetGuid());

        var audit = Assert.Single(await AuditAsync(host, accountId));
        Assert.Equal(AccountCreditGrantAuditActions.Granted, audit.Action);
        Assert.Equal(AccountCreditGrantAuditActions.SubjectType, audit.SubjectType);
        Assert.Equal("acme-support", audit.ActorName);
        Assert.Equal("Goodwill for the outage", audit.Reason);
        Assert.Null(audit.BeforeReference);
        Assert.Equal($"grantId={grantId:D};source=Ops;credits=500", audit.AfterReference);
    }

    /// <summary>A grant is a new row every time, so a request that cannot be told from its repeat is refused.</summary>
    [Fact]
    public async Task A_grant_without_an_idempotency_key_is_refused_and_writes_nothing()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await Grant(ops, accountId, key: null, new { credits = 500, source = "Ops", reason = "Goodwill" });

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, (await BodyOf(response)).GetProperty("code").GetString());
        Assert.Equal(0, await GrantCountAsync(host, accountId));
    }

    /// <summary>An operator's double submit grants once, and the repeat says it is one.</summary>
    [Fact]
    public async Task A_repeated_grant_answers_the_first_and_grants_once()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());
        var body = new { credits = 500, source = "Promo", reason = "Spring campaign" };

        var first = await Grant(ops, accountId, "key-1", body);
        var again = await Grant(ops, accountId, "key-1", body);

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal("true", Assert.Single(again.Headers.GetValues(IdempotencyPolicy.ReplayedHeader)));
        Assert.Equal(
            (await BodyOf(first)).GetProperty("grant").GetProperty("id").GetGuid(),
            (await BodyOf(again)).GetProperty("grant").GetProperty("id").GetGuid());
        Assert.Equal(1, await GrantCountAsync(host, accountId));
        Assert.Single(await AuditAsync(host, accountId));
    }

    /// <summary>
    /// A reference grants once: sent again under another key it answers 200 with the grant it already made, and
    /// writes nothing.
    /// </summary>
    [Fact]
    public async Task A_reference_sent_again_answers_unchanged()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());
        var body = new { credits = 500, source = "Ops", externalReferenceId = "ticket-41", reason = "Goodwill" };

        var first = await BodyOf(await Grant(ops, accountId, "key-1", body));
        var again = await Grant(ops, accountId, "key-2", body);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var replay = await BodyOf(again);
        Assert.Equal("Unchanged", replay.GetProperty("outcome").GetString());
        Assert.Equal(first.GetProperty("grant").GetProperty("id").GetGuid(), replay.GetProperty("grant").GetProperty("id").GetGuid());
        Assert.Equal(1, await GrantCountAsync(host, accountId));
        Assert.Single(await AuditAsync(host, accountId));
    }

    public static TheoryData<string, object> Unprocessable() => new()
    {
        { "credits", new { credits = 0, source = "Ops", reason = "Goodwill" } },
        { "credits", new { credits = -5, source = "Ops", reason = "Goodwill" } },
        { "credits", new { credits = 1.00001m, source = "Ops", reason = "Goodwill" } },

        // A pack is granted by the payment that made it (AP.6.3), and a grant names where its credits came from.
        { "source", new { credits = 500, source = "Purchase", externalReferenceId = "cs_1", reason = "Goodwill" } },
        { "source", new { credits = 500, reason = "Goodwill" } },

        { "externalReferenceId", new { credits = 500, source = "Ops", externalReferenceId = "  ", reason = "Goodwill" } },
        { "reason", new { credits = 500, source = "Ops", reason = "" } },
    };

    /// <summary>Each malformed grant is a 422 naming the field, and writes neither a grant nor an audit row.</summary>
    [Theory]
    [MemberData(nameof(Unprocessable))]
    public async Task A_malformed_grant_is_refused_and_writes_nothing(string field, object body)
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await Grant(ops, accountId, "key-1", body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(AccountCreditGrantAdministrationErrors.Unprocessable, problem.GetProperty("code").GetString());
        Assert.True(
            problem.GetProperty("errors").EnumerateObject().Any(error => error.Name.Equals(field, StringComparison.OrdinalIgnoreCase)),
            $"Expected an error on {field}: {problem}");
        Assert.Equal(0, await GrantCountAsync(host, accountId));
        Assert.Empty(await AuditAsync(host, accountId));
    }

    /// <summary>An account id no column can hold is a refusal naming it, not a 500.</summary>
    [Fact]
    public async Task An_over_long_account_id_is_refused_rather_than_thrown()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await Grant(ops, new string('a', 451), "key-1", new { credits = 500, source = "Ops", reason = "Goodwill" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---- clawing back ------------------------------------------------------------------------------------

    /// <summary>
    /// The unspent part is taken back and the grant says so; a repeat changes nothing; the grant is still listed,
    /// with nothing left on it, and the account's total says nought.
    /// </summary>
    [Fact]
    public async Task A_claw_back_takes_what_is_left_once_and_keeps_the_grant()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());
        var grantId = await GrantIdAsync(ops, accountId, 500);

        var response = await ClawBack(ops, accountId, grantId, new { reason = "Refunded in full" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal("ClawedBack", body.GetProperty("outcome").GetString());
        Assert.Equal(0m, body.GetProperty("grant").GetProperty("creditsRemaining").GetDecimal());
        Assert.Equal(500m, body.GetProperty("grant").GetProperty("creditsClawedBack").GetDecimal());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("grant").GetProperty("clawedBackAt").ValueKind);

        var again = await BodyOf(await ClawBack(ops, accountId, grantId, new { reason = "Disputed as well" }));
        Assert.Equal("Unchanged", again.GetProperty("outcome").GetString());

        var list = await BodyOf(await ops.GetAsync($"{Route}/{accountId}/credit-grants", Ct));
        Assert.Equal(0m, list.GetProperty("creditsRemaining").GetDecimal());
        Assert.Single(list.GetProperty("grants").EnumerateArray());

        Assert.Equal(
            [AccountCreditGrantAuditActions.Granted, AccountCreditGrantAuditActions.ClawedBack],
            (await AuditAsync(host, accountId)).Select(row => row.Action));
    }

    /// <summary>
    /// A grant is the account's in the route or it is not found — another account's included, so the route cannot
    /// be used to read or take somebody else's credits.
    /// </summary>
    [Fact]
    public async Task Another_accounts_grant_is_not_found_to_read_or_claw_back()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var sam = await host.CreateUserAsync("sam@example.com", Password);
        var alex = await host.CreateUserAsync("alex@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());
        var grantId = await GrantIdAsync(ops, sam, 500);

        var read = await ops.GetAsync($"{Route}/{alex}/credit-grants/{grantId:D}", Ct);
        var taken = await ClawBack(ops, alex, grantId, new { reason = "Wrong account" });
        var unknown = await ClawBack(ops, sam, Guid.NewGuid(), new { reason = "No such grant" });

        foreach (var response in new[] { read, taken, unknown })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(AccountCreditGrantAdministrationErrors.NotFound, (await BodyOf(response)).GetProperty("code").GetString());
        }

        var stored = await BodyOf(await ops.GetAsync($"{Route}/{sam}/credit-grants/{grantId:D}", Ct));
        Assert.Equal(500m, stored.GetProperty("creditsRemaining").GetDecimal());
        Assert.Single(await AuditAsync(host, sam));
        Assert.Empty(await AuditAsync(host, alex));
    }

    /// <summary>A claw-back with no reason is refused and takes nothing.</summary>
    [Fact]
    public async Task A_claw_back_without_a_reason_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());
        var grantId = await GrantIdAsync(ops, accountId, 500);

        var response = await ClawBack(ops, accountId, grantId, new { reason = "" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var stored = await BodyOf(await ops.GetAsync($"{Route}/{accountId}/credit-grants/{grantId:D}", Ct));
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("clawedBackAt").ValueKind);
    }

    // ---- listing -----------------------------------------------------------------------------------------

    /// <summary>
    /// Pages oldest first, each grant once, with the total across all of them on every page; a cursor that names
    /// no grant of the account is refused rather than read as the start.
    /// </summary>
    [Fact]
    public async Task Grants_are_listed_a_page_at_a_time_with_the_total_across_all()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var other = await host.CreateUserAsync("alex@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        Guid[] granted = [await GrantIdAsync(ops, accountId, 100), await GrantIdAsync(ops, accountId, 200), await GrantIdAsync(ops, accountId, 300.5m)];
        var foreign = await GrantIdAsync(ops, other, 999);

        var first = await BodyOf(await ops.GetAsync($"{Route}/{accountId}/credit-grants?limit=2", Ct));
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await BodyOf(await ops.GetAsync($"{Route}/{accountId}/credit-grants?limit=2&cursor={cursor}", Ct));

        Guid[] listed =
        [
            .. first.GetProperty("grants").EnumerateArray().Select(grant => grant.GetProperty("id").GetGuid()),
            .. second.GetProperty("grants").EnumerateArray().Select(grant => grant.GetProperty("id").GetGuid()),
        ];

        Assert.Equal(granted.Order(), listed.Order());
        Assert.Equal(3, listed.Distinct().Count());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal(600.5m, first.GetProperty("creditsRemaining").GetDecimal());
        Assert.Equal(600.5m, second.GetProperty("creditsRemaining").GetDecimal());

        // Another account's grant is not a place to page from.
        var refused = await ops.GetAsync($"{Route}/{accountId}/credit-grants?cursor={foreign:D}", Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }

    /// <summary>An account with no grants lists none and nought, as the plan read answers the default.</summary>
    [Fact]
    public async Task An_account_with_no_grants_lists_none()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var list = await BodyOf(await ops.GetAsync($"{Route}/{accountId}/credit-grants", Ct));

        Assert.Equal(0m, list.GetProperty("creditsRemaining").GetDecimal());
        Assert.Empty(list.GetProperty("grants").EnumerateArray());
    }

    // ---- who may -----------------------------------------------------------------------------------------

    /// <summary>
    /// A creator's own session cannot grant itself credits, whatever its platform roles: the policy names a scheme
    /// no gateway-minted token satisfies (B-14).
    /// </summary>
    [Fact]
    public async Task A_creators_own_token_cannot_grant_itself_credits()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);

        var creator = host.Factory.CreateClient();
        creator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", UserToken(accountId, PlatformRoles.PlatformAdmin));

        var response = await Grant(creator, accountId, "key-1", new { credits = 100000, source = "Ops", reason = "For me" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await GrantCountAsync(host, accountId));
    }

    /// <summary>A key without the AI-usage scope can neither grant nor claw back.</summary>
    [Fact]
    public async Task A_key_without_the_usage_scope_cannot_grant_or_claw_back()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var admin = host.CreateOpsClient(await host.CreateOpsClientAsync("acme-support"));
        var reader = host.CreateOpsClient(await host.CreateOpsClientAsync("reader", "some.other.scope"));
        var grantId = await GrantIdAsync(admin, accountId, 500);

        var grant = await Grant(reader, accountId, "key-9", new { credits = 500, source = "Ops", reason = "Nice try" });
        var clawBack = await ClawBack(reader, accountId, grantId, new { reason = "Nice try" });

        Assert.Equal(HttpStatusCode.Forbidden, grant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, clawBack.StatusCode);
        Assert.Equal(1, await GrantCountAsync(host, accountId));
        Assert.Single(await AuditAsync(host, accountId));
    }

    // ---- harness -----------------------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> Grant(HttpClient client, string accountId, string? key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Route}/{accountId}/credit-grants")
        {
            Content = JsonContent.Create(body),
        };

        if (key is not null)
        {
            request.Headers.Add(IdempotencyPolicy.KeyHeader, key);
        }

        return client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> ClawBack(HttpClient client, string accountId, Guid grantId, object body) =>
        client.PostAsJsonAsync($"{Route}/{accountId}/credit-grants/{grantId:D}/claw-back", body, Ct);

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

    private static async Task<Guid> GrantIdAsync(HttpClient ops, string accountId, decimal credits)
    {
        var response = await Grant(ops, accountId, Guid.NewGuid().ToString("N"), new { credits, source = "Ops", reason = "Goodwill" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await BodyOf(response)).GetProperty("grant").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<int> GrantCountAsync(SqliteApiHost host, string accountId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountCreditGrants
            .AsNoTracking()
            .CountAsync(row => row.AccountId == accountId, Ct);
    }

    /// <summary>The platform audit rows written against one account, oldest first.</summary>
    private static async Task<IReadOnlyList<PlatformAuditLog>> AuditAsync(SqliteApiHost host, string accountId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PlatformAuditLogs
            .AsNoTracking()
            .Where(row => row.SubjectType == AccountCreditGrantAuditActions.SubjectType && row.SubjectId == accountId)
            .OrderBy(row => row.OccurredAt)
            .ToListAsync(Ct);
    }
}
