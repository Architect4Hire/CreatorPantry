using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Data;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The credit grant ledger against the schema a deployment applies (PRICE-008, B-50, AP.5.1): no write makes a
/// balance negative, a claw-back is a recorded state and happens once, a grant is never deleted, and writers who
/// meet on one grant or one reference cannot both win.
/// </summary>
/// <remarks>
/// SQL Server rather than SQLite, because both halves are the database's: the check constraints and the filtered
/// unique index as SQL Server enforces them, and a <c>rowversion</c> that actually moves on update — under SQLite it
/// never does, so a lost race there cannot be seen at all. The constraint cases write straight through the context
/// so each refusal is the schema's, not a check in Business that happens to agree with it.
/// </remarks>
public sealed class AccountCreditGrantSqlServerTests : IAsyncLifetime
{
    private const string Sam = "user-sam";

    private const string Alex = "user-alex";

    private static readonly DateTimeOffset Start = new(2037, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly PlatformActor Operator = new(PlatformAuditActorType.OpsClient, "ops-client-1", "acme-support");

    private readonly SqlServerTestDatabase _container = new();

    private readonly StagedRace _race = new();

    private ServiceProvider? _provider;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Idempotency:FingerprintKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock>(new FixedClock())
            .AddSingleton(_race)
            .AddBillingModule()
            .AddIdempotency(configuration)
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()));

        // Every call goes through the real data layer; the decorator only acts when a test stages a race on it.
        services.RemoveAll<IAccountCreditGrantDataLayer>();
        services.AddScoped<AccountCreditGrantDataLayer>();
        services.AddScoped<IAccountCreditGrantDataLayer>(provider => new RacingDataLayer(
            provider.GetRequiredService<AccountCreditGrantDataLayer>(),
            provider.GetRequiredService<StagedRace>(),
            provider.GetRequiredService<IServiceScopeFactory>()));

        _provider = services.BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.MigrateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    // ---- the schema --------------------------------------------------------------------------------------

    /// <summary>The shapes the schema exists to allow: a grant that stands, part spent, and one clawed back.</summary>
    [Fact]
    public async Task A_standing_grant_and_a_clawed_back_one_are_both_stored()
    {
        await InsertAsync(Grant(Sam, granted: 500, remaining: 120));
        await InsertAsync(Grant(Sam, granted: 500, remaining: 0, clawedBack: 120, clawedBackAt: Start.AddDays(3)));

        // A fully spent grant clawed back takes nought and still records that it was.
        await InsertAsync(Grant(Alex, granted: 87.5m, remaining: 0, clawedBack: 0, clawedBackAt: Start));

        Assert.Equal(3, await CountAsync());
    }

    public static TheoryData<string, AccountCreditGrant> Invalid() => new()
    {
        // The restriction AP.5.1 names.
        { "a balance below nought", Grant(Sam, granted: 500, remaining: -0.0001m) },

        // Spent would be negative: credits the grant never had.
        { "more remaining than was granted", Grant(Sam, granted: 500, remaining: 500.0001m) },
        { "a grant of nought", Grant(Sam, granted: 0, remaining: 0) },
        { "more clawed back than was granted", Grant(Sam, granted: 500, remaining: 0, clawedBack: 501, clawedBackAt: Start) },
        { "a negative claw-back", Grant(Sam, granted: 500, remaining: 0, clawedBack: -1, clawedBackAt: Start) },

        // The claw-back is the instant and the figure together.
        { "credits clawed back with no claw-back", Grant(Sam, granted: 500, remaining: 0, clawedBack: 120) },
        { "a claw-back that left credits to spend", Grant(Sam, granted: 500, remaining: 10, clawedBack: 120, clawedBackAt: Start) },
        { "a claw-back before the grant", Grant(Sam, granted: 500, remaining: 0, clawedBack: 120, clawedBackAt: Start.AddTicks(-1)) },

        { "an undeclared source", Grant(Sam, granted: 500, remaining: 500, source: CreditGrantSource.Unspecified) },
        { "a purchase with no checkout", Grant(Sam, granted: 500, remaining: 500, source: CreditGrantSource.Purchase) },
        { "a blank reference", Grant(Sam, granted: 500, remaining: 500, reference: "   ") },
    };

    /// <summary>Each way of storing a balance that does not add up is refused by SQL Server itself.</summary>
    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task The_schema_refuses_a_grant_that_does_not_add_up(string why, AccountCreditGrant grant)
    {
        await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(grant));

        Assert.Equal(0, await CountAsync());
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    /// <summary>
    /// The same restriction on the write AP.5.2 will make: a spend that would take a grant below nought is refused,
    /// and the balance is what it was.
    /// </summary>
    [Fact]
    public async Task A_spend_past_the_balance_is_refused_and_leaves_it()
    {
        var grant = Grant(Sam, granted: 500, remaining: 40);
        await InsertAsync(grant);

        await Assert.ThrowsAsync<DbUpdateException>(() => SpendAsync(grant.Id, 40.0001m));

        Assert.Equal(40, (await ReadAsync(grant.Id)).CreditsRemaining);
    }

    /// <summary>
    /// A clawed-back grant is final: it cannot be given credits to spend again, nor un-clawed. Refused before the
    /// database sees it; the schema would refuse the first as well (the insert cases above prove that half).
    /// </summary>
    [Fact]
    public async Task A_clawed_back_grant_is_final()
    {
        var grant = Grant(Sam, granted: 500, remaining: 0, clawedBack: 120, clawedBackAt: Start);
        await InsertAsync(grant);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SetRemainingAsync(grant.Id, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync(grant.Id, row =>
        {
            row.ClawedBackAt = null;
            row.CreditsClawedBack = 0;
            row.CreditsRemaining = 120;
        }));

        var stored = await ReadAsync(grant.Id);
        Assert.Equal(0, stored.CreditsRemaining);
        Assert.Equal(Start, stored.ClawedBackAt);
    }

    public static TheoryData<string, Action<AccountCreditGrant>> FixedFacts() => new()
    {
        // Every one of these leaves a row the check constraints accept, which is why the guard exists.
        { "more credits granted", row => row.CreditsGranted = 5000 },
        { "another account", row => row.AccountId = Alex },
        { "another source", row => row.Source = CreditGrantSource.Promo },
        { "another reference", row => row.ExternalReferenceId = "ticket-42" },
        { "another instant", row => row.GrantedAt = Start.AddDays(-30) },
    };

    /// <summary>
    /// What a grant is for, from, of and when are fixed once it is made: an update that would mint credits, move
    /// them to another account or re-date them is refused, and the grant is as it was.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixedFacts))]
    public async Task A_grants_fixed_facts_cannot_be_changed(string why, Action<AccountCreditGrant> change)
    {
        var grant = Grant(Sam, granted: 500, remaining: 500, reference: "ticket-41");
        await InsertAsync(grant);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateAsync(grant.Id, change));

        Assert.Contains("refused", refused.Message, StringComparison.Ordinal);
        var stored = await ReadAsync(grant.Id);
        Assert.Equal((Sam, CreditGrantSource.Ops, 500m, "ticket-41", Start),
            (stored.AccountId, stored.Source, stored.CreditsGranted, stored.ExternalReferenceId, stored.GrantedAt));
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    /// <summary>
    /// The balance moves both ways within the grant: a spend lowers it, and a released hold returns credits to the
    /// grant they came from (AP.5.2) — never past what it granted.
    /// </summary>
    [Fact]
    public async Task A_balance_may_fall_and_rise_again_within_the_grant()
    {
        var grant = Grant(Sam, granted: 500, remaining: 500);
        await InsertAsync(grant);

        await SpendAsync(grant.Id, 300);
        await SetRemainingAsync(grant.Id, 450);

        Assert.Equal(450, (await ReadAsync(grant.Id)).CreditsRemaining);
        await Assert.ThrowsAsync<DbUpdateException>(() => SetRemainingAsync(grant.Id, 500.0001m));
    }

    /// <summary>
    /// Paging on SQL Server with every grant made at one instant — the frozen clock here — so the order rests on
    /// the id alone, and the cursor's comparison has to agree with SQL Server's own ordering of a
    /// <c>uniqueidentifier</c>: each grant once, none skipped.
    /// </summary>
    [Fact]
    public async Task Grants_made_at_one_instant_page_once_each()
    {
        var granted = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            granted.Add((await GrantThroughFacadeAsync(Sam, 10 + index)).Id);
        }

        var listed = new List<Guid>();
        string? cursor = null;
        do
        {
            using var scope = _provider!.CreateScope();
            var page = await scope.ServiceProvider.GetRequiredService<IAccountCreditGrantFacade>()
                .ListAsync(Sam, cursor, limit: 2, Ct);

            Assert.True(page.Succeeded);
            Assert.Equal(60, page.Value!.CreditsRemaining);
            listed.AddRange(page.Value.Grants.Select(grant => grant.Id));
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(granted.Order(), listed.Order());
        Assert.Equal(5, listed.Distinct().Count());
    }

    /// <summary>
    /// A reference grants once within its source — what AP.6.3's one grant per checkout session rests on — while
    /// the same text under another source, and any number of grants with no reference, are separate.
    /// </summary>
    [Fact]
    public async Task A_reference_grants_once_within_its_source()
    {
        await InsertAsync(Grant(Sam, granted: 500, remaining: 500, source: CreditGrantSource.Purchase, reference: "cs_1"));
        await InsertAsync(Grant(Sam, granted: 500, remaining: 500, source: CreditGrantSource.Ops, reference: "cs_1"));
        await InsertAsync(Grant(Sam, granted: 10, remaining: 10));
        await InsertAsync(Grant(Alex, granted: 10, remaining: 10));

        // To another account makes no difference: the reference is spent, not the account's copy of it.
        await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(
            Grant(Alex, granted: 500, remaining: 500, source: CreditGrantSource.Purchase, reference: "cs_1")));

        Assert.Equal(4, await CountAsync());
    }

    /// <summary>
    /// A grant is never deleted (AP.5.1): the delete is refused before it reaches the database, and the grant —
    /// what it granted and what became of it — is still there.
    /// </summary>
    [Fact]
    public async Task A_grant_cannot_be_deleted()
    {
        var grant = Grant(Sam, granted: 500, remaining: 0, clawedBack: 500, clawedBackAt: Start);
        await InsertAsync(grant);

        using (var scope = _provider!.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.AccountCreditGrants.Remove(await db.AccountCreditGrants.SingleAsync(row => row.Id == grant.Id, Ct));

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("cannot be deleted", refused.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1, await CountAsync());
    }

    // ---- claw-back ---------------------------------------------------------------------------------------

    /// <summary>
    /// The unspent part is taken back and recorded on the grant; what was spent stays spent (B-50) and is still
    /// readable from it. One audit row, naming the operator, the reason and the figure taken.
    /// </summary>
    [Fact]
    public async Task A_claw_back_takes_what_is_unspent_and_records_it()
    {
        var grant = await GrantThroughFacadeAsync(Sam, 500);
        await SpendAsync(grant.Id, 380);

        var change = await ClawBackAsync(Sam, grant.Id);

        Assert.True(change.Succeeded);
        Assert.Equal(AccountCreditGrantChangeReport.ClawedBack, change.Value!.Outcome);

        var stored = await ReadAsync(grant.Id);
        Assert.Equal(500, stored.CreditsGranted);
        Assert.Equal(0, stored.CreditsRemaining);
        Assert.Equal(120, stored.CreditsClawedBack);
        Assert.Equal(Start, stored.ClawedBackAt);

        var audit = Assert.Single(await AuditAsync(AccountCreditGrantAuditActions.ClawedBack));
        Assert.Equal(Sam, audit.SubjectId);
        Assert.Equal("acme-support", audit.ActorName);
        Assert.Equal("Refunded in full", audit.Reason);
        Assert.Equal($"grantId={grant.Id:D};source=Ops;remaining=120", audit.BeforeReference);
        Assert.Equal($"grantId={grant.Id:D};source=Ops;clawedBack=120", audit.AfterReference);
    }

    /// <summary>
    /// A grant that is fully spent is still clawed back: nought is taken, because spent credits are not recovered,
    /// and the grant then says it was.
    /// </summary>
    [Fact]
    public async Task A_fully_spent_grant_records_a_claw_back_of_nought()
    {
        var grant = await GrantThroughFacadeAsync(Sam, 500);
        await SpendAsync(grant.Id, 500);

        var change = await ClawBackAsync(Sam, grant.Id);

        Assert.Equal(AccountCreditGrantChangeReport.ClawedBack, change.Value!.Outcome);
        var stored = await ReadAsync(grant.Id);
        Assert.Equal(0, stored.CreditsClawedBack);
        Assert.NotNull(stored.ClawedBackAt);
    }

    /// <summary>
    /// Once: a refund and then a dispute on one payment, or an operator's repeat, change nothing the second time
    /// and audit nothing the second time.
    /// </summary>
    [Fact]
    public async Task A_second_claw_back_changes_nothing()
    {
        var grant = await GrantThroughFacadeAsync(Sam, 500);
        await ClawBackAsync(Sam, grant.Id);

        var again = await ClawBackAsync(Sam, grant.Id);

        Assert.Equal(AccountCreditGrantChangeReport.Unchanged, again.Value!.Outcome);
        Assert.Equal(500, again.Value.Grant.CreditsClawedBack);
        Assert.Single(await AuditAsync(AccountCreditGrantAuditActions.ClawedBack));
    }

    /// <summary>The account in the route owns the grant, or there is no grant: another account's answers not found.</summary>
    [Fact]
    public async Task Another_accounts_grant_cannot_be_clawed_back()
    {
        var grant = await GrantThroughFacadeAsync(Sam, 500);

        var change = await ClawBackAsync(Alex, grant.Id);

        Assert.Equal(AccountCreditGrantAdministrationErrors.NotFound, change.Error!.Code);
        Assert.Null((await ReadAsync(grant.Id)).ClawedBackAt);
        Assert.Empty(await AuditAsync(AccountCreditGrantAuditActions.ClawedBack));
    }

    // ---- concurrency -------------------------------------------------------------------------------------

    /// <summary>
    /// A spend lands between the claw-back's read and its write. The claw-back is refused rather than recording the
    /// credits it read — some of which are now spent — and nothing is taken or audited; sent again, it takes what is
    /// left now.
    /// </summary>
    [Fact]
    public async Task A_claw_back_that_a_spend_overtakes_takes_nothing_and_is_told_to_reread()
    {
        var grant = await GrantThroughFacadeAsync(Sam, 500);
        _race.AfterRead = scopes => Spend(scopes, grant.Id, 300);

        var refused = await ClawBackAsync(Sam, grant.Id);

        Assert.Equal(AccountCreditGrantAdministrationErrors.Conflict, refused.Error!.Code);
        var stored = await ReadAsync(grant.Id);
        Assert.Equal(200, stored.CreditsRemaining);
        Assert.Null(stored.ClawedBackAt);
        Assert.Empty(await AuditAsync(AccountCreditGrantAuditActions.ClawedBack));

        var retried = await ClawBackAsync(Sam, grant.Id);

        Assert.Equal(AccountCreditGrantChangeReport.ClawedBack, retried.Value!.Outcome);
        Assert.Equal(200, (await ReadAsync(grant.Id)).CreditsClawedBack);
    }

    /// <summary>
    /// Two claw-backs of one grant, the second landing between the first's read and its write: the first is refused,
    /// and on reading again finds the grant already clawed back — one change, one audit row.
    /// </summary>
    [Fact]
    public async Task Two_claw_backs_of_one_grant_take_it_once()
    {
        var grant = await GrantThroughFacadeAsync(Sam, 500);
        _race.AfterRead = scopes => ClawBack(scopes, grant.Id);

        var loser = await ClawBackAsync(Sam, grant.Id);

        Assert.Equal(AccountCreditGrantAdministrationErrors.Conflict, loser.Error!.Code);
        Assert.Equal(AccountCreditGrantChangeReport.Unchanged, (await ClawBackAsync(Sam, grant.Id)).Value!.Outcome);
        Assert.Equal(500, (await ReadAsync(grant.Id)).CreditsClawedBack);
        Assert.Single(await AuditAsync(AccountCreditGrantAuditActions.ClawedBack));
    }

    /// <summary>
    /// Many claw-backs of one grant at once, unstaged: whatever order the database serves them in, the grant is
    /// clawed back exactly once and audited exactly once, and every other answer is a repeat or a reread.
    /// </summary>
    [Fact]
    public async Task Concurrent_claw_backs_of_one_grant_take_it_once()
    {
        var grant = await GrantThroughFacadeAsync(Sam, 500);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => ClawBackAsync(Sam, grant.Id))));

        Assert.Single(answers, answer => answer.Value?.Outcome == AccountCreditGrantChangeReport.ClawedBack);
        Assert.All(answers, answer => Assert.True(
            answer.Value?.Outcome is AccountCreditGrantChangeReport.ClawedBack or AccountCreditGrantChangeReport.Unchanged
                || answer.Error?.Code == AccountCreditGrantAdministrationErrors.Conflict));
        Assert.Equal(500, (await ReadAsync(grant.Id)).CreditsClawedBack);
        Assert.Single(await AuditAsync(AccountCreditGrantAuditActions.ClawedBack));
    }

    /// <summary>
    /// One reference sent at once under different idempotency keys — two operators, or two deliveries of one
    /// webhook: one grant and one audit row, and every answer names that grant.
    /// </summary>
    [Fact]
    public async Task Concurrent_grants_of_one_reference_grant_once()
    {
        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => Task.Run(() =>
            GrantAsync(Sam, 500, $"key-{index}", reference: "ticket-41"))));

        var grant = Assert.Single(await GrantsAsync(Sam));
        Assert.Single(answers, answer => answer.Result.Value?.Outcome == AccountCreditGrantChangeReport.Granted);
        Assert.All(answers, answer => Assert.Equal(grant.Id, answer.Result.Value!.Grant.Id));
        Assert.Single(await AuditAsync(AccountCreditGrantAuditActions.Granted));
    }

    /// <summary>The same reference for another account, or another figure, is a mistake shown, not a second grant.</summary>
    [Fact]
    public async Task A_used_reference_for_another_account_or_figure_is_refused()
    {
        await GrantAsync(Sam, 500, "key-1", reference: "ticket-41");

        Assert.Equal(
            AccountCreditGrantAdministrationErrors.ReferenceUsed,
            (await GrantAsync(Alex, 500, "key-2", reference: "ticket-41")).Result.Error!.Code);
        Assert.Equal(
            AccountCreditGrantAdministrationErrors.ReferenceUsed,
            (await GrantAsync(Sam, 600, "key-3", reference: "ticket-41")).Result.Error!.Code);

        Assert.Single(await GrantsAsync(Sam));
        Assert.Empty(await GrantsAsync(Alex));
    }

    /// <summary>
    /// One idempotency key and body sent at once — an operator's double submit: one grant, one audit row, and the
    /// rest of the answers replay it.
    /// </summary>
    [Fact]
    public async Task Concurrent_grants_under_one_key_grant_once()
    {
        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => GrantAsync(Sam, 500, "key-1"))));

        var grant = Assert.Single(await GrantsAsync(Sam));
        Assert.Single(answers, answer => !answer.Replayed);
        Assert.All(answers, answer => Assert.Equal(grant.Id, answer.Result.Value!.Grant.Id));
        Assert.Single(await AuditAsync(AccountCreditGrantAuditActions.Granted));
    }

    /// <summary>A key already used for another grant is refused, rather than answering with that other grant.</summary>
    [Fact]
    public async Task A_key_reused_for_another_grant_is_refused()
    {
        await GrantAsync(Sam, 500, "key-1");

        var reused = await GrantAsync(Sam, 600, "key-1");

        Assert.Equal(IdempotencyPolicy.KeyReusedCode, reused.Result.Error!.Code);
        Assert.Single(await GrantsAsync(Sam));
    }

    // ---- harness -----------------------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccountCreditGrant Grant(
        string accountId,
        decimal granted,
        decimal remaining,
        decimal clawedBack = 0,
        DateTimeOffset? clawedBackAt = null,
        CreditGrantSource source = CreditGrantSource.Ops,
        string? reference = null) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        Source = source,
        CreditsGranted = granted,
        CreditsRemaining = remaining,
        CreditsClawedBack = clawedBack,
        ExternalReferenceId = reference,
        GrantedAt = Start,
        ClawedBackAt = clawedBackAt,
    };

    private async Task<IdempotentOutcome<AccountCreditGrantChangeServiceModel>> GrantAsync(
        string accountId, decimal credits, string key, string? reference = null)
    {
        using var scope = _provider!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAccountCreditGrantFacade>().GrantAsync(
            accountId,
            new GrantAccountCreditsViewModel(credits, CreditGrantSource.Ops, reference, "Goodwill for the outage"),
            Operator,
            key,
            Ct);
    }

    private async Task<AccountCreditGrantServiceModel> GrantThroughFacadeAsync(string accountId, decimal credits) =>
        (await GrantAsync(accountId, credits, Guid.NewGuid().ToString("N"))).Result.Value!.Grant;

    private async Task<Domain.Managers.Results.OperationResult<AccountCreditGrantChangeServiceModel>> ClawBackAsync(
        string accountId, Guid grantId)
    {
        using var scope = _provider!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAccountCreditGrantFacade>().ClawBackAsync(
            accountId, grantId, new ClawBackAccountCreditGrantViewModel("Refunded in full"), Operator, Ct);
    }

    private async Task InsertAsync(AccountCreditGrant grant)
    {
        using var scope = _provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.AccountCreditGrants.Add(grant);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Stands in for AP.5.2's spend: lowers the balance through the context, moving the row version.</summary>
    private Task SpendAsync(Guid grantId, decimal credits) => Spend(_provider!.GetRequiredService<IServiceScopeFactory>(), grantId, credits);

    private Task SetRemainingAsync(Guid grantId, decimal remaining) =>
        UpdateAsync(grantId, grant => grant.CreditsRemaining = remaining);

    private async Task UpdateAsync(Guid grantId, Action<AccountCreditGrant> change)
    {
        using var scope = _provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var grant = await db.AccountCreditGrants.SingleAsync(row => row.Id == grantId, Ct);
        change(grant);
        await db.SaveChangesAsync(Ct);
    }

    private static async Task Spend(IServiceScopeFactory scopes, Guid grantId, decimal credits)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var grant = await db.AccountCreditGrants.SingleAsync(row => row.Id == grantId, Ct);
        grant.CreditsRemaining -= credits;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A whole claw-back through the facade, in its own scope, with no race staged on it.</summary>
    private static async Task ClawBack(IServiceScopeFactory scopes, Guid grantId)
    {
        using var scope = scopes.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IAccountCreditGrantFacade>().ClawBackAsync(
            Sam, grantId, new ClawBackAccountCreditGrantViewModel("Disputed"), Operator, Ct);
        Assert.True(result.Succeeded);
    }

    private async Task<AccountCreditGrant> ReadAsync(Guid grantId)
    {
        using var scope = _provider!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AccountCreditGrants
            .AsNoTracking()
            .SingleAsync(row => row.Id == grantId, Ct);
    }

    private async Task<List<AccountCreditGrant>> GrantsAsync(string accountId)
    {
        using var scope = _provider!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AccountCreditGrants
            .AsNoTracking()
            .Where(row => row.AccountId == accountId)
            .ToListAsync(Ct);
    }

    private async Task<int> CountAsync()
    {
        using var scope = _provider!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AccountCreditGrants.CountAsync(Ct);
    }

    private async Task<List<PlatformAuditLog>> AuditAsync(string action)
    {
        using var scope = _provider!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().PlatformAuditLogs
            .AsNoTracking()
            .Where(row => row.Action == action)
            .ToListAsync(Ct);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Start;
    }

    /// <summary>
    /// What to do, once, between a claw-back's read of a grant and its write. Taken atomically, so the staged write —
    /// itself a claw-back through the same data layer — does not stage itself again.
    /// </summary>
    private sealed class StagedRace
    {
        private Func<IServiceScopeFactory, Task>? _afterRead;

        public Func<IServiceScopeFactory, Task>? AfterRead
        {
            set => _afterRead = value;
        }

        public Func<IServiceScopeFactory, Task>? Take() => Interlocked.Exchange(ref _afterRead, null);
    }

    /// <summary>
    /// The real data layer, with a staged write by somebody else slipped in after the claw-back has read the grant —
    /// so the race is the one the row version exists for, every time, rather than whichever order two tasks happen to
    /// run in.
    /// </summary>
    private sealed class RacingDataLayer(
        AccountCreditGrantDataLayer inner, StagedRace race, IServiceScopeFactory scopes) : IAccountCreditGrantDataLayer
    {
        public async Task<AccountCreditGrant?> FindForUpdateAsync(
            string accountId, Guid grantId, CancellationToken cancellationToken)
        {
            var grant = await inner.FindForUpdateAsync(accountId, grantId, cancellationToken);

            if (race.Take() is { } other)
            {
                await other(scopes);
            }

            return grant;
        }

        public Task<AccountCreditGrant?> FindAsync(string accountId, Guid grantId, CancellationToken cancellationToken) =>
            inner.FindAsync(accountId, grantId, cancellationToken);

        public Task<AccountCreditGrant?> FindByReferenceAsync(
            CreditGrantSource source, string externalReferenceId, CancellationToken cancellationToken) =>
            inner.FindByReferenceAsync(source, externalReferenceId, cancellationToken);

        public Task<IReadOnlyList<AccountCreditGrant>> ListAsync(
            string accountId, AccountCreditGrant? after, int take, CancellationToken cancellationToken) =>
            inner.ListAsync(accountId, after, take, cancellationToken);

        public Task<IReadOnlyList<decimal>> ListRemainingAsync(string accountId, CancellationToken cancellationToken) =>
            inner.ListRemainingAsync(accountId, cancellationToken);

        public Task<AccountCreditGrantOutcome> AddAsync(
            AccountCreditGrant grant, PlatformAuditEntry? audit, CancellationToken cancellationToken) =>
            inner.AddAsync(grant, audit, cancellationToken);

        public Task<AccountCreditGrantOutcome> SaveClawBackAsync(
            AccountCreditGrant grant, PlatformAuditEntry? audit, CancellationToken cancellationToken) =>
            inner.SaveClawBackAsync(grant, audit, cancellationToken);
    }
}
