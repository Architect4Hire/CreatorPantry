using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// What a platform administrator can do to an account's allowance, and what it records (USAGE-009).
/// </summary>
public sealed class OpsQuotaAdministrationTests
{
    private const string Password = "Correct horse battery staple";

    /// <summary>Reading an account nobody has is a 404 with a stable code.</summary>
    [Fact]
    public async Task An_unknown_account_is_not_found()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await ops.GetAsync(
            "/api/v1/ops/ai-usage/accounts/nobody", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            AiUsageAdministrationErrors.AccountNotFound, await CodeAsync(response));
    }

    /// <summary>
    /// An account that has never run anything still reads: it has real period boundaries and the configured
    /// allowance, computed from the terms in force and thrown away rather than written.
    /// </summary>
    [Fact]
    public async Task An_account_with_no_quota_reads_the_platform_default()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var body = await ReadAsync(ops, accountId);

        Assert.Equal(accountId, body.GetProperty("accountId").GetString());
        Assert.Equal("PlatformDefault", body.GetProperty("terms").GetProperty("source").GetString());
        Assert.Equal(1000m, body.GetProperty("terms").GetProperty("allowance").GetDecimal());
        Assert.False(body.GetProperty("terms").GetProperty("isSuspended").GetBoolean());

        // Projected, never opened: a GET must not give the account a balance row it never asked for.
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Empty(await context.AccountAiQuotaPeriods.ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Setting terms puts them in force and reports them back without a second read.</summary>
    [Fact]
    public async Task Setting_a_quota_puts_the_terms_in_force()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var set = await SetQuotaAsync(ops, accountId, allowance: 5000m);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var body = await JsonAsync(set);
        Assert.Equal("Changed", body.GetProperty("outcome").GetString());
        Assert.Equal("Account", body.GetProperty("terms").GetProperty("source").GetString());
        Assert.Equal(5000m, body.GetProperty("terms").GetProperty("allowance").GetDecimal());

        var reread = await ReadAsync(ops, accountId);
        Assert.Equal(5000m, reread.GetProperty("terms").GetProperty("allowance").GetDecimal());
    }

    /// <summary>
    /// <strong>The terms are effective-dated, never edited.</strong> A second change closes the first row and
    /// opens another, so the account's quota history survives the change that replaced it — and exactly one
    /// row is ever open.
    /// </summary>
    [Fact]
    public async Task Changing_a_quota_closes_the_previous_terms_rather_than_editing_them()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await SetQuotaAsync(ops, accountId, allowance: 5000m);
        await SetQuotaAsync(ops, accountId, allowance: 9000m);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var rows = await context.AccountAiQuotas
            .OrderBy(quota => quota.EffectiveFrom)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].EffectiveTo);
        Assert.Equal(5000m, rows[0].Allowance);
        Assert.Null(rows[1].EffectiveTo);
        Assert.Equal(9000m, rows[1].Allowance);
        Assert.Single(rows, quota => quota.EffectiveTo is null);
    }

    /// <summary>
    /// <strong>A command that changes nothing writes nothing — including no audit row.</strong> This is what
    /// makes a retried administrative command safe without an idempotency key, and it is what keeps the audit
    /// trail countable: every row in it is a change somebody actually made.
    /// </summary>
    [Fact]
    public async Task Re_sending_identical_terms_writes_nothing()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await SetQuotaAsync(ops, accountId, allowance: 5000m);
        var repeat = await SetQuotaAsync(ops, accountId, allowance: 5000m);

        Assert.Equal("Unchanged", (await JsonAsync(repeat)).GetProperty("outcome").GetString());

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        Assert.Single(await context.AccountAiQuotas.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await context.PlatformAuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <strong>Every change names actor, before, after and reason.</strong> The before/after are compact state
    /// pointers rather than prose, so an operator can diff two rows and a reader can tell what moved.
    /// </summary>
    [Fact]
    public async Task Every_change_writes_an_audit_row_naming_actor_before_after_and_reason()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync("operator"));

        await SetQuotaAsync(ops, accountId, allowance: 5000m, reason: "Beta programme grant.");
        await CommandAsync(ops, accountId, "ai-access/suspend", "Suspected abuse, ticket OPS-14.");
        await CommandAsync(ops, accountId, "ai-access/restore", "Cleared by review, ticket OPS-14.");
        await CommandAsync(ops, accountId, "quota/clear", "Beta programme ended.");

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var rows = await context.PlatformAuditLogs
            .OrderBy(log => log.OccurredAt)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                AiUsageAuditActions.QuotaSet,
                AiUsageAuditActions.AccessSuspended,
                AiUsageAuditActions.AccessRestored,
                AiUsageAuditActions.QuotaCleared,
            ],
            rows.Select(log => log.Action));

        Assert.All(rows, log =>
        {
            Assert.Equal(PlatformAuditActorType.OpsClient, log.ActorType);
            Assert.Equal("operator", log.ActorName);
            Assert.NotEmpty(log.ActorId);
            Assert.Equal(AiUsageAuditActions.SubjectType, log.SubjectType);
            Assert.Equal(accountId, log.SubjectId);
            Assert.NotEmpty(log.Reason);
            Assert.NotNull(log.BeforeReference);
            Assert.NotNull(log.AfterReference);
        });

        // The first change was from no quota at all, and says so rather than inventing a previous allowance.
        Assert.Equal(AccountAiQuotaAuditReference.PlatformDefault, rows[0].BeforeReference);
        Assert.Contains("allowance=5000", rows[0].AfterReference);
        Assert.Equal("Beta programme grant.", rows[0].Reason);

        // Suspension moves exactly one field, and both pointers show it.
        Assert.Contains("suspended=false", rows[1].BeforeReference);
        Assert.Contains("suspended=true", rows[1].AfterReference);

        // Clearing lands back on the platform default.
        Assert.Equal(AccountAiQuotaAuditReference.PlatformDefault, rows[3].AfterReference);
    }

    /// <summary>A reason is required; a blank one is refused before anything is written.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no")]
    public async Task A_change_without_a_usable_reason_is_refused(string reason)
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await CommandAsync(ops, accountId, "ai-access/suspend", reason);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Empty(await context.PlatformAuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <strong>Suspend, then the account's next AI request is refused.</strong> Suspension is read from the
    /// terms in force rather than from the running period's frozen copy, which is why it binds immediately
    /// rather than at the next roll.
    /// </summary>
    [Fact]
    public async Task Suspending_an_account_refuses_its_next_ai_request()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        Assert.Equal(AiQuotaStateOutcome.Available, await PeekAsync(host, accountId));

        await CommandAsync(ops, accountId, "ai-access/suspend", "Suspected abuse, ticket OPS-14.");

        Assert.Equal(AiQuotaStateOutcome.Suspended, await PeekAsync(host, accountId));

        await CommandAsync(ops, accountId, "ai-access/restore", "Cleared by review.");

        Assert.Equal(AiQuotaStateOutcome.Available, await PeekAsync(host, accountId));
    }

    /// <summary>
    /// Suspending an account that has no quota gives it the flag and nothing else: the allowance stays null,
    /// so it keeps resolving to whatever platform configuration says rather than being frozen at today's value.
    /// </summary>
    [Fact]
    public async Task Suspending_an_account_with_no_quota_does_not_pin_its_allowance()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await CommandAsync(ops, accountId, "ai-access/suspend", "Suspected abuse.");

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var quota = await context.AccountAiQuotas.SingleAsync(TestContext.Current.CancellationToken);

        Assert.True(quota.IsSuspended);
        Assert.Null(quota.Allowance);
    }

    /// <summary>
    /// <strong>Clearing returns every term to the platform default, not just the allowance.</strong> That is
    /// why it closes the row rather than nulling one column: unit, period, anchor, zone and carry-over all go
    /// back to configuration.
    /// </summary>
    [Fact]
    public async Task Clearing_a_quota_falls_back_to_the_platform_default()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var set = await SetQuotaAsync(
            ops,
            accountId,
            allowance: 5000m,
            periodLength: nameof(AiQuotaPeriodLength.Weekly),
            periodAnchor: 3,
            timeZoneId: "Europe/London");
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var cleared = await CommandAsync(ops, accountId, "quota/clear", "Beta programme ended.");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var terms = (await ReadAsync(ops, accountId)).GetProperty("terms");

        Assert.Equal("PlatformDefault", terms.GetProperty("source").GetString());
        Assert.Equal(1000m, terms.GetProperty("allowance").GetDecimal());
        Assert.Equal(nameof(AiQuotaPeriodLength.Monthly), terms.GetProperty("periodLength").GetString());
        Assert.Equal(1, terms.GetProperty("periodAnchor").GetInt32());
        Assert.Equal("Etc/UTC", terms.GetProperty("timeZoneId").GetString());

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Empty(await context.AccountAiQuotas
            .Where(quota => quota.EffectiveTo == null)
            .ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Clearing a quota the account never had changes nothing and records nothing.</summary>
    [Fact]
    public async Task Clearing_a_quota_that_was_never_set_is_a_no_op()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await CommandAsync(ops, accountId, "quota/clear", "Tidying up.");

        Assert.Equal("Unchanged", (await JsonAsync(response)).GetProperty("outcome").GetString());

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Empty(await context.PlatformAuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Raising an allowance is not a way to restore an account somebody else switched off. The two commands
    /// are separate, carry separate audit codes, and neither does the other's job by accident.
    /// </summary>
    [Fact]
    public async Task Setting_a_quota_does_not_lift_a_suspension()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await CommandAsync(ops, accountId, "ai-access/suspend", "Suspected abuse.");
        await SetQuotaAsync(ops, accountId, allowance: 9000m, reason: "Unrelated adjustment.");

        var terms = (await ReadAsync(ops, accountId)).GetProperty("terms");

        Assert.True(terms.GetProperty("isSuspended").GetBoolean());
        Assert.Equal(9000m, terms.GetProperty("allowance").GetDecimal());
        Assert.Equal(AiQuotaStateOutcome.Suspended, await PeekAsync(host, accountId));
    }

    /// <summary>Terms the quota table's check constraints forbid are refused with a field error, not a 500.</summary>
    [Theory]
    [InlineData(nameof(AiQuotaPeriodLength.Monthly), 29, "Etc/UTC", "periodAnchor")]
    [InlineData(nameof(AiQuotaPeriodLength.Weekly), 9, "Etc/UTC", "periodAnchor")]
    [InlineData(nameof(AiQuotaPeriodLength.Daily), 4, "Etc/UTC", "periodAnchor")]
    [InlineData(nameof(AiQuotaPeriodLength.Monthly), 1, "Mars/Olympus_Mons", "timeZoneId")]
    public async Task Invalid_terms_are_refused(string length, int anchor, string zone, string field)
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await SetQuotaAsync(
            ops, accountId, allowance: 5000m, periodLength: length, periodAnchor: anchor, timeZoneId: zone);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AiUsageAdministrationErrors.QuotaInvalid, await CodeAsync(response));
        Assert.Contains(field, (await JsonAsync(response)).GetProperty("errors").EnumerateObject()
            .Select(property => property.Name));
    }

    /// <summary>
    /// <strong>An administrator sees workspace identifiers and never workspace names.</strong> The name is a
    /// fact about a workspace they hold no membership in; the id is what lets them correlate spend.
    /// </summary>
    [Fact]
    public async Task The_admin_read_carries_workspace_ids_and_no_names()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var workspaceId = await SeedSpendAsync(host, accountId, "Sam's Secret Test Kitchen");
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await ops.GetAsync(
            $"/api/v1/ops/ai-usage/accounts/{accountId}", TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Secret Test Kitchen", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspaceName", raw, StringComparison.OrdinalIgnoreCase);

        var byWorkspace = JsonDocument.Parse(raw).RootElement.GetProperty("byWorkspace");
        Assert.Contains(
            byWorkspace.EnumerateArray(),
            row => row.GetProperty("workspaceId").GetGuid() == workspaceId);
    }

    /// <summary>
    /// <strong>One account, two workspaces, one balance.</strong> The leaderboard counts an account's spend
    /// wherever the work was done, which is the whole reason usage is measured per account.
    /// </summary>
    [Fact]
    public async Task Top_consumers_totals_an_accounts_spend_across_every_workspace()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var sam = await host.CreateUserAsync("sam@example.com", Password);
        var alex = await host.CreateUserAsync("alex@example.com", Password, displayName: "Alex");

        await SeedSpendAsync(host, sam, "Kitchen A", amount: 30m);
        await SeedSpendAsync(host, sam, "Kitchen B", amount: 40m);
        await SeedSpendAsync(host, alex, "Kitchen C", amount: 50m);

        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());
        var now = DateTimeOffset.UtcNow;
        var response = await ops.GetAsync(
            $"/api/v1/ops/ai-usage/top-consumers?from={Iso(now.AddDays(-1))}&to={Iso(now.AddDays(1))}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var accounts = (await JsonAsync(response)).GetProperty("accounts").EnumerateArray().ToList();

        Assert.Equal(sam, accounts[0].GetProperty("accountId").GetString());
        Assert.Equal(70m, accounts[0].GetProperty("amount").GetDecimal());
        Assert.Equal(2, accounts[0].GetProperty("runs").GetInt32());
        Assert.Equal(alex, accounts[1].GetProperty("accountId").GetString());
        Assert.Equal(50m, accounts[1].GetProperty("amount").GetDecimal());
    }

    /// <summary>
    /// <strong>Two accounts, and neither reads the other.</strong> The ops seam is the one place in this
    /// feature that names an account from a request, so the boundary it introduces is asserted rather than
    /// inferred from the repositories' predicates: Alex's spend never appears in Sam's figures.
    /// </summary>
    [Fact]
    public async Task One_accounts_read_never_includes_another_accounts_spend()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var sam = await host.CreateUserAsync("sam@example.com", Password);
        var alex = await host.CreateUserAsync("alex@example.com", Password, displayName: "Alex");

        await SeedSpendAsync(host, sam, "Sam's Kitchen", amount: 30m);
        var alexWorkspace = await SeedSpendAsync(host, alex, "Alex's Kitchen", amount: 70m);

        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());
        var body = await ReadAsync(ops, sam);

        Assert.Equal(sam, body.GetProperty("accountId").GetString());

        var byWorkspace = body.GetProperty("byWorkspace").EnumerateArray().ToList();
        Assert.DoesNotContain(
            byWorkspace, row => row.GetProperty("workspaceId").GetGuid() == alexWorkspace);
        Assert.Equal(30m, byWorkspace.Sum(row => row.GetProperty("amount").GetDecimal()));
        Assert.Equal(30m, body.GetProperty("period").GetProperty("consumed").GetDecimal());
    }

    /// <summary>
    /// <strong>Administering one account leaves every other account's terms alone.</strong> A quota belongs to
    /// an account, and the route names exactly one.
    /// </summary>
    [Fact]
    public async Task Changing_one_accounts_quota_leaves_another_accounts_terms_untouched()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var sam = await host.CreateUserAsync("sam@example.com", Password);
        var alex = await host.CreateUserAsync("alex@example.com", Password, displayName: "Alex");
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await SetQuotaAsync(ops, alex, allowance: 250m, reason: "Alex's own arrangement.");

        await SetQuotaAsync(ops, sam, allowance: 9000m, reason: "Sam's beta grant.");
        await CommandAsync(ops, sam, "ai-access/suspend", "Suspected abuse.");

        var alexTerms = (await ReadAsync(ops, alex)).GetProperty("terms");

        Assert.Equal(250m, alexTerms.GetProperty("allowance").GetDecimal());
        Assert.False(alexTerms.GetProperty("isSuspended").GetBoolean());
        Assert.Equal(AiQuotaStateOutcome.Available, await PeekAsync(host, alex));
        Assert.Equal(AiQuotaStateOutcome.Suspended, await PeekAsync(host, sam));

        // And the audit trail names only the account each change was actually made against.
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var subjects = await context.PlatformAuditLogs
            .Select(log => log.SubjectId)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, subjects.Count(subject => subject == sam));
        Assert.Equal(1, subjects.Count(subject => subject == alex));
    }

    /// <summary>A backwards or absurdly long window is refused rather than scanned.</summary>
    [Theory]
    [InlineData("2026-02-01T00:00:00Z", "2026-01-01T00:00:00Z")]
    [InlineData("2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z")]
    [InlineData("2020-01-01T00:00:00Z", "2100-01-01T00:00:00Z")]
    public async Task An_unusable_window_is_refused(string from, string to)
    {
        await using var host = await SqliteApiHost.StartAsync();
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var response = await ops.GetAsync(
            $"/api/v1/ops/ai-usage/top-consumers?from={from}&to={to}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AiUsageAdministrationErrors.WindowInvalid, await CodeAsync(response));
    }

    /// <summary>
    /// <strong>A clear racing a set loses, rather than both reporting success.</strong> The filtered unique
    /// index cannot catch this pair — a clear inserts nothing, so the two never collide on it — and if both
    /// committed, the audit trail would carry a <c>cleared</c> row for an account that is not on the platform
    /// default. <c>EffectiveTo</c> being a concurrency token is what makes the second writer find no open row
    /// to close.
    /// </summary>
    [Fact]
    public async Task A_clear_racing_a_set_is_refused_rather_than_both_committing()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        await SetQuotaAsync(ops, accountId, allowance: 5000m, reason: "Beta grant.");

        // Driven at the data layer so the interleaving is the one that matters rather than whatever the
        // scheduler happens to produce: both writers read the same open row, and only then does either write.
        await using var first = host.Factory.Services.CreateAsyncScope();
        await using var second = host.Factory.Services.CreateAsyncScope();

        var clearing = first.ServiceProvider.GetRequiredService<IAiUsageAdministrationDataLayer>();
        var setting = second.ServiceProvider.GetRequiredService<IAiUsageAdministrationDataLayer>();

        var readByClear = await clearing.FindCurrentQuotaAsync(accountId, TestContext.Current.CancellationToken);
        var readBySet = await setting.FindCurrentQuotaAsync(accountId, TestContext.Current.CancellationToken);

        Assert.NotNull(readByClear);
        Assert.NotNull(readBySet);

        // The clear commits first: it closes the open row and inserts nothing, so it touches the unique index
        // not at all. Before EffectiveTo was a concurrency token, the set would then have closed an
        // already-closed row, inserted its own, and reported success — leaving a `cleared` audit row beside an
        // account that was not on the platform default.
        var cleared = await clearing.ReplaceQuotaAsync(
            accountId, readByClear, null, Audit(accountId, AiUsageAuditActions.QuotaCleared),
            TestContext.Current.CancellationToken);

        var set = await setting.ReplaceQuotaAsync(
            accountId,
            readBySet,
            new AccountAiQuotaWrite(
                AiQuotaUnit.Credits, 9000m, AiQuotaPeriodLength.Monthly, 1, "Etc/UTC",
                AiQuotaCarryOver.None, null, IsSuspended: false),
            Audit(accountId, AiUsageAuditActions.QuotaSet),
            TestContext.Current.CancellationToken);

        Assert.Equal(AiQuotaAdministrationWriteOutcome.Applied, cleared);
        Assert.Equal(AiQuotaAdministrationWriteOutcome.Contended, set);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // The loser wrote nothing at all: no open row, and no audit entry claiming a change it did not make.
        Assert.Empty(await context.AccountAiQuotas
            .Where(quota => quota.EffectiveTo == null)
            .ToListAsync(TestContext.Current.CancellationToken));

        var actions = await context.PlatformAuditLogs
            .Select(log => log.Action)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([AiUsageAuditActions.QuotaSet, AiUsageAuditActions.QuotaCleared], actions);
    }

    private static PlatformAuditEntry Audit(string accountId, string action) => new(
        PlatformAuditActorType.OpsClient,
        "client-1",
        "tests",
        action,
        AiUsageAuditActions.SubjectType,
        accountId,
        Guid.NewGuid(),
        "Racing writers.");

    /// <summary>
    /// Two commands landing on the same clock reading still produce a legal range.
    /// <c>CK_AccountAiQuotas_Effective_Range</c> demands <c>EffectiveTo &gt; EffectiveFrom</c>, so closing a row
    /// created in the same tick has to step past it rather than stamp the instant it was opened.
    /// </summary>
    [Fact]
    public async Task Two_changes_in_the_same_clock_tick_both_commit()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

        await using var host = await SqliteApiHost.StartAsync(clock);
        var accountId = await host.CreateUserAsync("sam@example.com", Password);
        var ops = host.CreateOpsClient(await host.CreateOpsClientAsync());

        var set = await SetQuotaAsync(ops, accountId, allowance: 5000m, reason: "Beta grant.");
        var suspended = await CommandAsync(ops, accountId, "ai-access/suspend", "Suspected abuse.");

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var rows = await context.AccountAiQuotas
            .OrderBy(quota => quota.EffectiveFrom)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, quota => Assert.True(quota.EffectiveTo is null || quota.EffectiveTo > quota.EffectiveFrom));

        // The rows still abut exactly, so no reader can land between them and find no terms at all.
        Assert.Equal(rows[0].EffectiveTo, rows[1].EffectiveFrom);
    }

    /// <summary>Asks the admission seam what it would say, without spending anything to find out.</summary>
    private static async Task<AiQuotaStateOutcome> PeekAsync(SqliteApiHost host, string accountId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var admission = scope.ServiceProvider
            .GetRequiredService<Domain.Modules.AiUsage.Facade.IAiQuotaAdmissionFacade>();

        var state = await admission.PeekAsync(
            accountId,
            Domain.Modules.Ai.Managers.AiTaskType.RecipeConcepts,
            isMeterable: true,
            TestContext.Current.CancellationToken);

        return state.Outcome;
    }

    /// <summary>
    /// Posts a settled, charged run for the account in a workspace, so there is spend to read back.
    /// </summary>
    private static async Task<Guid> SeedSpendAsync(
        SqliteApiHost host, string accountId, string workspaceName, decimal amount = 25m)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var now = DateTimeOffset.UtcNow;

        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = workspaceName,
            Slug = $"ws-{Guid.NewGuid():N}",
            CreatedAt = now,
        };
        context.Workspaces.Add(workspace);

        var period = new AccountAiQuotaPeriod
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            StartsAt = now.AddDays(-1),
            EndsAt = now.AddDays(29),
            TimeZoneId = "Etc/UTC",
            LocalStartDate = DateOnly.FromDateTime(now.AddDays(-1).UtcDateTime),
            Unit = AiQuotaUnit.Credits,
            Allowance = 1000m,
            CarriedOver = 0m,
            Consumed = amount,
            Reserved = 0m,
        };
        context.AccountAiQuotaPeriods.Add(period);

        var operationId = Guid.NewGuid();
        context.AccountAiQuotaReservations.Add(new AccountAiQuotaReservation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PeriodId = period.Id,
            AiOperationId = operationId,
            LeaseToken = Guid.NewGuid(),
            TaskType = Domain.Modules.Ai.Managers.AiTaskType.RecipeConcepts,
            Unit = AiQuotaUnit.Credits,
            ReservedAmount = amount,
            PerAttemptEstimate = amount,
            SettledAmount = amount,
            UsageReported = true,
            Status = AiQuotaReservationStatus.Settled,
            HeldAt = now,
            ExpiresAt = now.AddHours(1),
            SettledAt = now,
            PostedAt = now,
        });

        context.AccountAiUsageEntries.Add(new AccountAiUsageEntry
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            OccurredAt = now,
            AiOperationId = operationId,
            AttemptNumber = 1,
            TaskType = Domain.Modules.Ai.Managers.AiTaskType.RecipeConcepts,
            WorkspaceId = workspace.Id,
            ProviderName = "test",
            ModelName = "test-model",
            InputTokens = 100,
            OutputTokens = 50,
            TotalTokens = 150,
            IsBillable = true,
            UsageReported = true,
            Outcome = AiUsageOutcome.Succeeded,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return workspace.Id;
    }

    private static Task<HttpResponseMessage> SetQuotaAsync(
        HttpClient ops,
        string accountId,
        decimal? allowance,
        string reason = "Adjusting the allowance.",
        string periodLength = nameof(AiQuotaPeriodLength.Monthly),
        int periodAnchor = 1,
        string timeZoneId = "Etc/UTC") =>
        ops.PutAsJsonAsync(
            $"/api/v1/ops/ai-usage/accounts/{accountId}/quota",
            new
            {
                unit = nameof(AiQuotaUnit.Credits),
                allowance,
                periodLength,
                periodAnchor,
                timeZoneId,
                carryOver = nameof(AiQuotaCarryOver.None),
                carryOverCap = (decimal?)null,
                reason,
            },
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> CommandAsync(
        HttpClient ops, string accountId, string command, string reason) =>
        ops.PostAsJsonAsync(
            $"/api/v1/ops/ai-usage/accounts/{accountId}/{command}",
            new { reason },
            TestContext.Current.CancellationToken);

    private static async Task<JsonElement> ReadAsync(HttpClient ops, string accountId) =>
        await JsonAsync(await ops.GetAsync(
            $"/api/v1/ops/ai-usage/accounts/{accountId}", TestContext.Current.CancellationToken));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

    /// <summary>URL-safe ISO 8601 UTC, so a query string carries an instant the model binder reads back.</summary>
    private static string Iso(DateTimeOffset instant) =>
        Uri.EscapeDataString(instant.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("code").GetString();
}
