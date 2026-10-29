using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// Ops administration of which plan an account is on (PRICE-007, USAGE-009, AP.4.4): read it, put the account on
/// one or schedule the change, and list the accounts on a plan — each audited, and none of it reachable by a
/// creator.
/// </summary>
/// <remarks>
/// Against the real API host, because the half of this worth testing is the authorization and the wire: the
/// change itself is covered where the chain is (<see cref="AccountPlanResolutionTests"/>,
/// <see cref="AccountPlanConstraintTests"/>).
/// </remarks>
public sealed class OpsAccountPlanTests
{
    private const string Password = "Correct horse battery staple";
    private const string Route = "/api/v1/ops/ai-usage/accounts";

    /// <summary>Three nested plans, the cheapest of them the default — the shape a real catalogue has.</summary>
    private static Action<IServiceCollection> Catalogue() =>
        services => services.PostConfigure<BillingOptions>(billing =>
        {
            billing.DefaultPlan = "trial";
            billing.Plans =
            [
                Plan("trial", "Trial", 1000, ["draft", "standard"], trial: true),
                Plan("creator", "Creator", 6000, ["draft", "standard", "high"]),
                Plan("studio", "Studio", 40000, ["draft", "standard", "high", "high-2k"]),
            ];
        });

    private static PlanOption Plan(
        string key, string name, int credits, string[] classes, bool trial = false) => new()
    {
        Key = key,
        Name = name,
        Credits = credits,
        // The host publishes its catalogue in development, and a published plan has to say what its credits buy.
        ExampleCounts = new PlanExampleOption { RecipeDrafts = 20, Pictures = 10 },
        DailyImageCap = 5,
        RunningImages = 1,
        Period = trial ? PlanAllowancePeriod.Once : PlanAllowancePeriod.Monthly,
        QualityClasses = [.. classes],
        TrialDays = trial ? 14 : null,
        TrialImageCap = trial ? 10 : null,
    };

    // ---- reading -----------------------------------------------------------------------------------------

    /// <summary>An account nobody moved is on the configured default, and the answer says so.</summary>
    [Fact]
    public async Task An_account_with_no_row_reads_the_default_plan()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var body = await BodyOf(await ops.GetAsync($"{Route}/{accountId}/plan", Ct));

        Assert.Equal("trial", body.GetProperty("key").GetString());
        Assert.Equal("Trial", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("isDefault").GetBoolean());
        Assert.Equal(1000, body.GetProperty("credits").GetInt32());

        // No row is in force, so there is nothing to say about who put them there or when.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("source").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("effectiveFrom").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("effectiveUntil").ValueKind);
    }

    /// <summary>A host with no catalogue has no plan to report, and does not invent one.</summary>
    [Fact]
    public async Task A_host_with_no_catalogue_has_no_plan_to_read()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: NoCatalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await ops.GetAsync($"{Route}/{accountId}/plan", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            AccountPlanAdministrationErrors.NoCatalogue,
            (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- setting -----------------------------------------------------------------------------------------

    /// <summary>
    /// A change for now takes effect at once, is reported as in force rather than scheduled, and leaves exactly
    /// one audit row naming the operator, the reason, and the plan on each side of the change.
    /// </summary>
    [Fact]
    public async Task A_change_now_takes_effect_and_is_audited()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync("acme-support"));

        var body = await BodyOf(await Put(ops, accountId, new { planKey = "studio", reason = "Paid invoice 41" }));

        Assert.Equal("Changed", body.GetProperty("outcome").GetString());
        Assert.Equal("studio", body.GetProperty("plan").GetProperty("key").GetString());
        Assert.False(body.GetProperty("plan").GetProperty("isDefault").GetBoolean());
        Assert.Equal("Ops", body.GetProperty("plan").GetProperty("source").GetString());

        // When each part applies (DL.3, B-56): the caps from now, and — Studio granting more than the trial — the
        // difference in credits at once.
        Assert.Equal(body.GetProperty("plan").GetProperty("effectiveFrom").GetDateTimeOffset(), body.GetProperty("capsFrom").GetDateTimeOffset());
        Assert.Equal("DifferenceNow", body.GetProperty("allowance").GetString());

        var audit = Assert.Single(await AuditAsync(host, accountId));
        Assert.Equal(AccountPlanAuditActions.Set, audit.Action);
        Assert.Equal(AccountPlanAuditActions.SubjectType, audit.SubjectType);
        Assert.Equal(accountId, audit.SubjectId);
        Assert.Equal("Paid invoice 41", audit.Reason);
        Assert.Equal("acme-support", audit.ActorName);

        // The pointer form: where they were, and where they are. A first change has no plan before it.
        Assert.Equal("planKey=(default)", audit.BeforeReference);
        Assert.Equal("planKey=studio", audit.AfterReference);
    }

    /// <summary>
    /// A future start schedules the change — which is how a downgrade at period end is recorded (B-54) — so the
    /// account is still on its old plan, the answer says <c>scheduled</c>, and the audit says so too.
    /// </summary>
    [Fact]
    public async Task A_change_for_later_is_scheduled_and_leaves_the_account_where_it_was()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var at = DateTimeOffset.UtcNow.AddDays(20);
        var body = await BodyOf(await Put(
            ops, accountId, new { planKey = "studio", effectiveFrom = at, reason = "Upgrade at renewal" }));

        Assert.Equal("Scheduled", body.GetProperty("outcome").GetString());

        // The caps follow the row from its start; the larger allowance's difference does too.
        Assert.Equal(at.ToUniversalTime(), body.GetProperty("capsFrom").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));
        Assert.Equal("DifferenceNow", body.GetProperty("allowance").GetString());

        // Still the default, with the scheduled change showing as when it takes over.
        var plan = body.GetProperty("plan");
        Assert.Equal("trial", plan.GetProperty("key").GetString());
        Assert.NotEqual(JsonValueKind.Null, plan.GetProperty("effectiveUntil").ValueKind);

        var audit = Assert.Single(await AuditAsync(host, accountId));
        Assert.Equal(AccountPlanAuditActions.Scheduled, audit.Action);
        Assert.StartsWith("planKey=studio;effectiveFrom=", audit.AfterReference, StringComparison.Ordinal);
    }

    /// <summary>
    /// A move to a plan granting fewer credits changes the caps at once and the allowance at the next period roll —
    /// the running period keeps what it opened with (B-56, DL.3).
    /// </summary>
    [Fact]
    public async Task A_move_to_fewer_credits_says_the_allowance_waits_for_the_next_period()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await Put(ops, accountId, new { planKey = "studio", reason = "Paid invoice 41" });
        var body = await BodyOf(await Put(ops, accountId, new { planKey = "creator", reason = "Moved down on request" }));

        Assert.Equal("Changed", body.GetProperty("outcome").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("capsFrom").ValueKind);
        Assert.Equal("NextPeriod", body.GetProperty("allowance").GetString());
    }

    /// <summary>A change that changes nothing says nothing applies.</summary>
    [Fact]
    public async Task A_change_that_changes_nothing_says_no_part_of_it_applies()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await Put(ops, accountId, new { planKey = "studio", reason = "Paid invoice 41" });
        var again = await BodyOf(await Put(ops, accountId, new { planKey = "studio", reason = "Paid invoice 41" }));

        Assert.Equal("Unchanged", again.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("capsFrom").ValueKind);
        Assert.Equal("None", again.GetProperty("allowance").GetString());
    }

    /// <summary>
    /// Null means "the configured default", the same thing a stored row with no key means — so the account
    /// tracks a later change of <c>Billing:DefaultPlan</c> rather than being pinned to today's.
    /// </summary>
    [Fact]
    public async Task A_change_with_no_key_puts_the_account_on_the_configured_default()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await Put(ops, accountId, new { planKey = "studio", reason = "Paid invoice 41" });
        var body = await BodyOf(await Put(ops, accountId, new { reason = "Refunded; back to the default" }));

        var plan = body.GetProperty("plan");
        Assert.Equal("trial", plan.GetProperty("key").GetString());
        Assert.True(plan.GetProperty("isDefault").GetBoolean());

        // A row is in force even so — it was put there deliberately — which is what tells this apart from an
        // account that simply never moved.
        Assert.NotEqual(JsonValueKind.Null, plan.GetProperty("effectiveFrom").ValueKind);
        Assert.Equal("planKey=(default)", (await AuditAsync(host, accountId))[^1].AfterReference);
    }

    /// <summary>
    /// <strong>The same command twice writes once.</strong> A chain is append-only and this route takes no
    /// idempotency key, so a retry — a double submit, a lost answer — must not append a second row or record a
    /// second change in the audit trail.
    /// </summary>
    [Fact]
    public async Task A_repeated_change_writes_nothing_the_second_time()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await Put(ops, accountId, new { planKey = "studio", reason = "Paid invoice 41" });
        var again = await BodyOf(await Put(ops, accountId, new { planKey = "studio", reason = "Paid invoice 41" }));

        // A success, not a refusal: the account is on the plan that was asked for.
        Assert.Equal("Unchanged", again.GetProperty("outcome").GetString());
        Assert.Equal("studio", again.GetProperty("plan").GetProperty("key").GetString());

        Assert.Single(await AuditAsync(host, accountId));
        Assert.Equal(1, await ChainLengthAsync(host, accountId));
    }

    /// <summary>And a repeated scheduled change, which must not be refused as "not after the latest row".</summary>
    [Fact]
    public async Task A_repeated_scheduled_change_writes_nothing_the_second_time()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var at = DateTimeOffset.UtcNow.AddDays(20);
        await Put(ops, accountId, new { planKey = "studio", effectiveFrom = at, reason = "At renewal" });
        var again = await BodyOf(
            await Put(ops, accountId, new { planKey = "studio", effectiveFrom = at, reason = "At renewal" }));

        Assert.Equal("Unchanged", again.GetProperty("outcome").GetString());
        Assert.Single(await AuditAsync(host, accountId));
        Assert.Equal(1, await ChainLengthAsync(host, accountId));
    }

    /// <summary>
    /// A row naming a plan the catalogue no longer has resolves to the default (AP.4.1), so the default's
    /// listing finds it — otherwise "who is on the default" goes quietly incomplete just after a plan is
    /// withdrawn, which is when somebody asks.
    /// </summary>
    [Fact]
    public async Task The_default_plan_lists_an_account_whose_plan_was_withdrawn()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await Put(ops, accountId, new { planKey = "studio", reason = "Paid up" });

        // The owner takes Studio out of the catalogue while an account is still on it.
        var billing = host.Factory.Services.GetRequiredService<IOptions<BillingOptions>>().Value;
        billing.Plans = [.. billing.Plans.Where(plan => plan.Key != "studio")];

        Assert.Equal([accountId], await ListAsync(ops, "trial"));
        Assert.Equal("trial", await PlanOfAsync(host, accountId));
    }

    /// <summary>An account id the column cannot hold is a refusal naming the field, not an unhandled throw.</summary>
    [Fact]
    public async Task An_over_long_account_id_is_refused_rather_than_thrown()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await Put(ops, new string('a', 600), new { planKey = "studio", reason = "Because" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(AccountPlanAdministrationErrors.Unprocessable, problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("accountId", out _));
    }

    /// <summary>
    /// A host with no catalogue answers the same way on all three routes: there is no plan to report, list or
    /// move anybody to, which is not a complaint about what was sent.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_catalogue_answers_the_same_on_every_route()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: NoCatalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var read = await ops.GetAsync($"{Route}/{accountId}/plan", Ct);
        var listed = await ops.GetAsync($"{Route}/by-plan?planKey=studio", Ct);
        var written = await Put(ops, accountId, new { planKey = "studio", reason = "Because" });

        foreach (var response in new[] { read, listed, written })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(
                AccountPlanAdministrationErrors.NoCatalogue,
                (await BodyOf(response)).GetProperty("code").GetString());
        }

        Assert.Empty(await AuditAsync(host, accountId));
    }

    [Theory]
    [InlineData("nonesuch", null, HttpStatusCode.UnprocessableEntity, "planKey")]
    [InlineData("studio", "-1", HttpStatusCode.UnprocessableEntity, "effectiveFrom")]
    public async Task A_change_the_catalogue_or_the_chain_cannot_take_is_refused(
        string planKey, string? daysFromNow, HttpStatusCode status, string field)
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var effectiveFrom = daysFromNow is null
            ? (DateTimeOffset?)null
            : DateTimeOffset.UtcNow.AddDays(int.Parse(daysFromNow, System.Globalization.CultureInfo.InvariantCulture));

        var response = await Put(ops, accountId, new { planKey, effectiveFrom, reason = "Because" });

        Assert.Equal(status, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(AccountPlanAdministrationErrors.Unprocessable, problem.GetProperty("code").GetString());
        Assert.Equal("none", problem.GetProperty("retry").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));

        // Nothing written, so nothing audited: a refusal is not an action.
        Assert.Empty(await AuditAsync(host, accountId));
    }

    /// <summary>A reason is required on every change, as it is on every other ops write.</summary>
    [Fact]
    public async Task A_change_with_no_reason_is_refused_and_writes_nothing()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await Put(ops, accountId, new { planKey = "studio", reason = " " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await AuditAsync(host, accountId));
        Assert.Equal("trial", (await BodyOf(await ops.GetAsync($"{Route}/{accountId}/plan", Ct))).GetProperty("key").GetString());
    }

    /// <summary>
    /// A change for now behind a scheduled one is refused rather than silently deferred — the operator asked for
    /// "now" and would otherwise be told it was done while the account did not move. Replacing a scheduled
    /// change is AP.6.2's.
    /// </summary>
    [Fact]
    public async Task A_change_now_behind_a_scheduled_one_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await Put(ops, accountId, new
        {
            planKey = "studio",
            effectiveFrom = DateTimeOffset.UtcNow.AddDays(20),
            reason = "At renewal",
        });

        var response = await Put(ops, accountId, new { planKey = "creator", reason = "Actually, now" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal(AccountPlanAdministrationErrors.ChangeScheduled, problem.GetProperty("code").GetString());
        Assert.Equal("none", problem.GetProperty("retry").GetString());

        // Only the scheduled change is recorded; the refusal wrote nothing.
        Assert.Single(await AuditAsync(host, accountId));
    }

    // ---- listing -----------------------------------------------------------------------------------------

    /// <summary>
    /// The accounts whose row in force names the plan, by account id, with a cursor that continues the page.
    /// Identifiers and instants only: an operator counting who is on what is in nobody's workspace.
    /// </summary>
    [Fact]
    public async Task Accounts_are_listed_by_the_plan_in_force_a_page_at_a_time()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var accounts = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var accountId = await host.CreateUserAsync($"sam{index}@example.com", Password);
            await Put(ops, accountId, new { planKey = "studio", reason = "Paid up" });
            accounts.Add(accountId);
        }

        accounts.Sort(StringComparer.Ordinal);

        var first = await BodyOf(await ops.GetAsync($"{Route}/by-plan?planKey=studio&limit=2", Ct));
        Assert.Equal("studio", first.GetProperty("planKey").GetString());
        Assert.Equal(
            accounts.Take(2),
            first.GetProperty("accounts").EnumerateArray().Select(row => row.GetProperty("accountId").GetString()));
        Assert.Equal(accounts[1], first.GetProperty("nextCursor").GetString());

        // Identifiers and instants, and nothing else — no email, no workspace, no name.
        Assert.Equal(
            ["accountId", "planKey", "source", "effectiveFrom"],
            first.GetProperty("accounts")[0].EnumerateObject().Select(property => property.Name));

        var second = await BodyOf(
            await ops.GetAsync($"{Route}/by-plan?planKey=studio&limit=2&cursor={accounts[1]}", Ct));

        Assert.Equal(
            [accounts[2]],
            second.GetProperty("accounts").EnumerateArray().Select(row => row.GetProperty("accountId").GetString()));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    /// <summary>
    /// A scheduled change does not move the account in the list: the question is who is on this plan, not who
    /// will be.
    /// </summary>
    [Fact]
    public async Task A_scheduled_change_does_not_list_the_account_under_the_plan_it_will_move_to()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await Put(ops, accountId, new { planKey = "creator", reason = "Paid up" });
        await Put(ops, accountId, new
        {
            planKey = "studio",
            effectiveFrom = DateTimeOffset.UtcNow.AddDays(20),
            reason = "At renewal",
        });

        Assert.Equal([accountId], await ListAsync(ops, "creator"));
        Assert.Empty(await ListAsync(ops, "studio"));
    }

    /// <summary>
    /// Asking for the default plan's key finds the accounts put there explicitly, because a row saying "the
    /// default" resolves to it. Accounts with no row at all are on it by absence and cannot be listed — Billing
    /// has no table of accounts.
    /// </summary>
    [Fact]
    public async Task The_default_plan_lists_rows_that_say_the_default_and_not_accounts_with_no_row()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var moved = await host.CreateUserAsync("moved@example.com", Password);
        var untouched = await host.CreateUserAsync("untouched@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await Put(ops, moved, new { reason = "Explicitly on the default" });

        var listed = await ListAsync(ops, "trial");

        Assert.Equal([moved], listed);
        Assert.DoesNotContain(untouched, listed);
    }

    [Fact]
    public async Task A_plan_the_catalogue_does_not_have_cannot_be_listed()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await ops.GetAsync($"{Route}/by-plan?planKey=nonesuch", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            AccountPlanAdministrationErrors.Unprocessable,
            (await BodyOf(response)).GetProperty("code").GetString());
    }

    /// <summary>A limit above the ceiling is clamped rather than refused, as the usage routes clamp theirs.</summary>
    [Fact]
    public async Task A_limit_above_the_ceiling_is_clamped()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await ops.GetAsync($"{Route}/by-plan?planKey=studio&limit=100000", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- authorization -----------------------------------------------------------------------------------

    /// <summary>
    /// <strong>A creator cannot set their own plan</strong> (the RESTRICTION this prompt carries). A
    /// gateway-minted user token is what every product route accepts and it opens nothing here, because the ops
    /// policy names an authentication scheme no gateway token can satisfy (B-14). An Owner's is the same token.
    /// </summary>
    /// <remarks>
    /// The reads are covered by <c>OpsAccessTests</c>, which owns "who can reach an ops route" and now names
    /// both plan reads in its theories. This is the write, which that file's command theory cannot carry
    /// because it posts and this route is a PUT.
    /// </remarks>
    [Fact]
    public async Task A_creators_own_token_cannot_set_its_own_plan()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);

        var creator = host.Factory.CreateClient();
        creator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", UserToken(accountId, PlatformRoles.PlatformAdmin));

        var response = await Put(creator, accountId, new { planKey = "studio", reason = "Give me the top plan" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await AuditAsync(host, accountId));
        Assert.Equal("trial", await PlanOfAsync(host, accountId));
    }

    /// <summary>No credential at all writes nothing either.</summary>
    [Fact]
    public async Task An_anonymous_caller_cannot_set_a_plan()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);

        var response = await Put(
            host.Factory.CreateClient(), accountId, new { planKey = "studio", reason = "Nice try" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await AuditAsync(host, accountId));
    }

    /// <summary>
    /// A key issued without the AI-usage scope holds a valid credential and still cannot change a plan: naming
    /// the capability is what lets a read-only automation exist (B-14).
    /// </summary>
    [Fact]
    public async Task A_key_without_the_usage_scope_cannot_set_a_plan()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync("reader", "some.other.scope"));

        var response = await Put(ops, accountId, new { planKey = "studio", reason = "Nice try" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await AuditAsync(host, accountId));
    }

    /// <summary>A revoked key is no key, on these routes as on the others.</summary>
    [Fact]
    public async Task A_revoked_key_cannot_set_a_plan()
    {
        await using var host = await SqliteApiHost.StartAsync(configureServices: Catalogue());
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync("acme-support"));
        await host.RevokeOpsClientAsync("acme-support");

        var response = await Put(ops, accountId, new { planKey = "studio", reason = "Nice try" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await AuditAsync(host, accountId));
    }

    // ---- harness -----------------------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A host with no catalogue. Emptied deliberately: the host reads development's settings, which do
    /// configure one. This is the shape every production deployment has today.
    /// </summary>
    private static Action<IServiceCollection> NoCatalogue() =>
        services => services.PostConfigure<BillingOptions>(billing =>
        {
            billing.PublishPlans = false;
            billing.DefaultPlan = null;
            billing.Plans = [];
        });

    /// <summary>How many rows the account's plan chain holds, which a repeated command must not grow.</summary>
    private static async Task<int> ChainLengthAsync(SqliteApiHost host, string accountId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountPlans
            .AsNoTracking()
            .CountAsync(row => row.AccountId == accountId, Ct);
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

    /// <summary>The plan key an account resolves to, read through the ops route an operator would read.</summary>
    private static async Task<string?> PlanOfAsync(SqliteApiHost host, string accountId)
    {
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync("reader-" + Guid.NewGuid().ToString("N")[..8]));

        return (await BodyOf(await ops.GetAsync($"{Route}/{accountId}/plan", Ct))).GetProperty("key").GetString();
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, string accountId, object body) =>
        client.PutAsJsonAsync($"{Route}/{accountId}/plan", body, Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<IReadOnlyList<string?>> ListAsync(HttpClient ops, string planKey)
    {
        var body = await BodyOf(await ops.GetAsync($"{Route}/by-plan?planKey={planKey}", Ct));

        return [.. body.GetProperty("accounts").EnumerateArray().Select(row => row.GetProperty("accountId").GetString())];
    }

    /// <summary>The platform audit rows written against one account, oldest first.</summary>
    private static async Task<IReadOnlyList<PlatformAuditLog>> AuditAsync(SqliteApiHost host, string accountId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PlatformAuditLogs
            .AsNoTracking()
            .Where(row => row.SubjectType == AccountPlanAuditActions.SubjectType && row.SubjectId == accountId)
            .OrderBy(row => row.OccurredAt)
            .ToListAsync(Ct);
    }
}
