using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Data;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// "One effective plan per account at a time, enforced by the schema" (AP.4.1), against the schema a deployment
/// applies: every way of storing two plans at once is refused by SQL Server itself, and two writers who read the
/// same chain cannot both extend it.
/// </summary>
/// <remarks>
/// SQL Server rather than SQLite, because the guarantee is the migration's: its filtered indexes, its foreign key
/// to an alternate key and its check constraint, as SQL Server enforces them (memory: SQLite cannot catch what
/// SQL Server refuses). The rows are written straight through the context so each refusal is the database's,
/// not a check in Business that happens to agree with it.
/// </remarks>
public sealed class AccountPlanConstraintTests : IAsyncLifetime
{
    private const string Sam = "user-sam";

    private const string Alex = "user-alex";

    private static readonly DateTimeOffset Start = new(2037, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly SqlServerTestDatabase _container = new();

    private readonly StoppedClock _clock = new();

    private ServiceProvider? _provider;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Billing:DefaultPlan"] = "trial",
                ["Billing:Plans:0:Key"] = "trial",
                ["Billing:Plans:0:Name"] = "Trial",
                ["Billing:Plans:0:Period"] = "Once",
                ["Billing:Plans:0:TrialDays"] = "14",
                ["Billing:Plans:0:RunningImages"] = "1",
                ["Billing:Plans:1:Key"] = "pro",
                ["Billing:Plans:1:Name"] = "Pro",
                ["Billing:Plans:1:Period"] = "Monthly",
                ["Billing:Plans:1:RunningImages"] = "2",
                ["Billing:Plans:2:Key"] = "studio",
                ["Billing:Plans:2:Name"] = "Studio",
                ["Billing:Plans:2:Period"] = "Monthly",
                ["Billing:Plans:2:RunningImages"] = "3",
            })
            .Build();

        _provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock>(_clock)
            .AddBillingModule(configuration)
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()))
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>The shape the schema exists to allow: one chain per account, and the same instants for another.</summary>
    [Fact]
    public async Task A_chain_of_rows_is_stored_and_another_account_may_use_the_same_instants()
    {
        await InsertAsync(Row(Sam, "trial", Start));
        await InsertAsync(Row(Sam, "pro", Start.AddDays(14), supersedes: Start));
        await InsertAsync(Row(Sam, null, Start.AddDays(44), supersedes: Start.AddDays(14)));
        await InsertAsync(Row(Alex, "pro", Start));
        await InsertAsync(Row(Alex, "studio", Start.AddDays(14), supersedes: Start));

        Assert.Equal(5, await CountAsync());
    }

    public static TheoryData<string, AccountPlan> Overlaps() => new()
    {
        // Two chains for one account: two rows that each claim to be where the account's plans begin.
        { "a second first row", Row(Sam, "studio", Start.AddDays(20)) },

        // A fork: two rows superseding the same row would both be in force after it.
        { "a second successor of one row", Row(Sam, "studio", Start.AddDays(20), supersedes: Start) },

        // A row starting inside the span of the row it supersedes.
        { "a successor that starts first", Row(Sam, "studio", Start.AddDays(-1), supersedes: Start.AddDays(10)) },

        // Superseding a row that does not exist, so the chain would have a gap where anything could sit.
        { "a successor of nothing", Row(Sam, "studio", Start.AddDays(40), supersedes: Start.AddDays(25)) },

        // Two rows starting together are refused by the alternate key too, but in a chain any such row is already
        // a fork, a second first row or a successor that starts first, so the cases above reach it first.
    };

    /// <summary>
    /// Every way of putting an account on two plans at once, against a chain of two rows: each is refused by
    /// SQL Server, and the chain is as it was.
    /// </summary>
    [Theory]
    [MemberData(nameof(Overlaps))]
    public async Task The_schema_refuses_every_way_of_storing_two_plans_at_once(string why, AccountPlan overlap)
    {
        await InsertAsync(Row(Sam, "trial", Start));
        await InsertAsync(Row(Sam, "pro", Start.AddDays(10), supersedes: Start));

        await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(overlap));

        Assert.Equal(2, await CountAsync());
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    /// <summary>
    /// The account is part of the link, so one account's row cannot supersede another's — a regression to a link
    /// on the instant alone would let Alex's plans run on from Sam's chain. And a change for Alex leaves Sam's
    /// chain, scheduled row included, exactly as it was.
    /// </summary>
    [Fact]
    public async Task One_accounts_row_cannot_supersede_anothers_and_a_change_for_one_leaves_the_other()
    {
        await InsertAsync(Row(Sam, "trial", Start));
        await InsertAsync(Row(Sam, "pro", Start.AddDays(10), supersedes: Start));

        // Sam's latest start, named as the row Alex's supersedes. Alex has no row starting then.
        await Assert.ThrowsAsync<DbUpdateException>(
            () => InsertAsync(Row(Alex, "studio", Start.AddDays(20), supersedes: Start.AddDays(10))));
        Assert.Empty(await ChainAsync(Alex));

        _clock.UtcNow = Start.AddDays(1);
        using (var scope = _provider!.CreateScope())
        {
            var business = scope.ServiceProvider.GetRequiredService<IAccountPlanBusiness>();
            Assert.Equal(
                AccountPlanChangeOutcome.Changed,
                await business.ChangeAsync(Alex, "studio", AccountPlanSource.Ops, null, CancellationToken.None));
        }

        Assert.Equal(["trial", "pro"], (await ChainAsync(Sam)).Select(row => row.PlanKey));
        Assert.Equal(["studio"], (await ChainAsync(Alex)).Select(row => row.PlanKey));
    }

    public static TheoryData<string, AccountPlan> Malformed() => new()
    {
        { "no source", Row(Sam, "pro", Start, source: AccountPlanSource.Unspecified) },
        { "a blank key", Row(Sam, "  ", Start) },
        { "a key wider than the column", Row(Sam, new string('k', BillingPolicy.PlanKeyMaxLength + 1), Start) },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task The_schema_refuses_a_row_that_names_no_plan_or_no_source(string why, AccountPlan row)
    {
        await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(row));

        Assert.Equal(0, await CountAsync());
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    /// <summary>
    /// The race the successor index exists for: two writers both read the same end of the chain, then both
    /// append. The second is told, nothing of its write survives in its context, and the chain has one successor.
    /// Staged rather than raced so it fails the same way every run.
    /// </summary>
    [Fact]
    public async Task Two_writers_who_read_the_same_latest_row_cannot_both_supersede_it()
    {
        await InsertAsync(Row(Sam, "trial", Start));
        _clock.UtcNow = Start.AddDays(1);

        using var first = _provider!.CreateScope();
        using var second = _provider!.CreateScope();
        var firstData = first.ServiceProvider.GetRequiredService<IAccountPlanDataLayer>();
        var secondData = second.ServiceProvider.GetRequiredService<IAccountPlanDataLayer>();

        var firstRead = await firstData.FindLatestAsync(Sam, CancellationToken.None);
        var secondRead = await secondData.FindLatestAsync(Sam, CancellationToken.None);

        Assert.Equal(
            AccountPlanChangeOutcome.Changed,
            await firstData.AppendAsync(Sam, firstRead, "pro", AccountPlanSource.Ops, Start.AddDays(1), CancellationToken.None));
        Assert.Equal(
            AccountPlanChangeOutcome.Contended,
            await secondData.AppendAsync(Sam, secondRead, "studio", AccountPlanSource.Stripe, Start.AddDays(2), CancellationToken.None));

        // The refused insert was forgotten, so a later save in the loser's scope commits nothing of it.
        var loserContext = second.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Empty(loserContext.ChangeTracker.Entries());
        await loserContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var chain = await ChainAsync(Sam);
        Assert.Equal(["trial", "pro"], chain.Select(row => row.PlanKey));
    }

    /// <summary>The same race on an account with no rows yet, decided by the one-first-row index.</summary>
    [Fact]
    public async Task Two_writers_cannot_both_give_an_account_its_first_plan()
    {
        using var first = _provider!.CreateScope();
        using var second = _provider!.CreateScope();
        var firstData = first.ServiceProvider.GetRequiredService<IAccountPlanDataLayer>();
        var secondData = second.ServiceProvider.GetRequiredService<IAccountPlanDataLayer>();

        Assert.Null(await firstData.FindLatestAsync(Sam, CancellationToken.None));
        Assert.Null(await secondData.FindLatestAsync(Sam, CancellationToken.None));

        Assert.Equal(
            AccountPlanChangeOutcome.Changed,
            await firstData.AppendAsync(Sam, null, "pro", AccountPlanSource.Ops, Start, CancellationToken.None));
        Assert.Equal(
            AccountPlanChangeOutcome.Contended,
            await secondData.AppendAsync(Sam, null, "studio", AccountPlanSource.Ops, Start.AddSeconds(1), CancellationToken.None));

        Assert.Equal(["pro"], (await ChainAsync(Sam)).Select(row => row.PlanKey));
    }

    /// <summary>
    /// Many writers at once, through Business, with no staging: however they interleave, each that reports a
    /// change wrote exactly one row, each that did not wrote none, and what is stored is still one chain.
    /// </summary>
    [Fact]
    public async Task Concurrent_changes_leave_one_chain_and_every_reported_change_in_it()
    {
        // A clock that moves, as a real one does: every reading a millisecond on from the last.
        _clock.Ticking = true;
        string[] keys = ["pro", "studio", "trial", "pro", "studio", "trial", "pro", "studio"];

        var outcomes = await Task.WhenAll(keys.Select(async key =>
        {
            using var scope = _provider!.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IAccountPlanBusiness>()
                .ChangeAsync(Sam, key, AccountPlanSource.Ops, effectiveFrom: null, CancellationToken.None);
        }));

        Assert.All(outcomes, outcome => Assert.True(
            outcome is AccountPlanChangeOutcome.Changed or AccountPlanChangeOutcome.Contended,
            $"Unexpected outcome {outcome}."));

        var chain = await ChainAsync(Sam);
        var changed = outcomes.Count(outcome => outcome == AccountPlanChangeOutcome.Changed);

        Assert.InRange(changed, 1, keys.Length);
        Assert.Equal(changed, chain.Count);

        // One chain: the first supersedes nothing, and each later row supersedes the one before it.
        Assert.Null(chain[0].SupersedesFrom);
        for (var i = 1; i < chain.Count; i++)
        {
            Assert.Equal(chain[i - 1].EffectiveFrom, chain[i].SupersedesFrom);
        }
    }

    private static AccountPlan Row(
        string accountId, string? planKey, DateTimeOffset from, DateTimeOffset? supersedes = null,
        AccountPlanSource source = AccountPlanSource.Ops) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        PlanKey = planKey,
        Source = source,
        EffectiveFrom = from,
        SupersedesFrom = supersedes,
    };

    private async Task InsertAsync(AccountPlan row)
    {
        using var scope = _provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.AccountPlans.Add(row);
        await db.SaveChangesAsync();
    }

    private async Task<int> CountAsync()
    {
        using var scope = _provider!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AccountPlans.CountAsync();
    }

    private async Task<List<AccountPlan>> ChainAsync(string accountId)
    {
        using var scope = _provider!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AccountPlans
            .AsNoTracking()
            .Where(plan => plan.AccountId == accountId)
            .OrderBy(plan => plan.EffectiveFrom)
            .ToListAsync();
    }

    /// <summary>Stopped unless <see cref="Ticking"/>, when each reading is a millisecond after the one before.</summary>
    private sealed class StoppedClock : IClock
    {
        private DateTimeOffset _now = Start;

        private long _ticks;

        public bool Ticking { get; set; }

        public DateTimeOffset UtcNow
        {
            get => Ticking ? Start.AddMilliseconds(Interlocked.Increment(ref _ticks)) : _now;
            set => _now = value;
        }
    }
}
