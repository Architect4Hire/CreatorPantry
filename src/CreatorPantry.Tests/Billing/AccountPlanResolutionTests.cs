using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Tests.Auth;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// Which plan an account resolves to, and the rules a change is held to before it reaches the schema (AP.4.1).
/// </summary>
/// <remarks>
/// SQLite, because nothing here is a race. What the schema refuses, and what happens when two writers meet, is
/// <see cref="AccountPlanConstraintTests"/>'s, against SQL Server.
/// </remarks>
public sealed class AccountPlanResolutionTests : IDisposable
{
    private const string Sam = "user-sam";

    private const string Alex = "user-alex";

    private static readonly DateTimeOffset Start = new(2037, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly MovableClock _clock = new() { UtcNow = Start };

    private readonly CapturingLoggerProvider _logs = new();

    private readonly List<ServiceProvider> _hosts = [];

    private readonly List<SqliteConnection> _connections = [];

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        foreach (var connection in _connections)
        {
            connection.Dispose();
        }
    }

    [Fact]
    public async Task An_account_with_no_row_is_on_the_default_plan_with_its_terms()
    {
        var plan = await ResolveAsync(Catalogue(), Sam);

        Assert.NotNull(plan);
        Assert.Equal("trial", plan.Key);
        Assert.Equal("Trial", plan.Name);
        Assert.Equal(1000, plan.Credits);
        Assert.Equal(PlanAllowancePeriod.Once, plan.Period);
        Assert.Equal(5, plan.DailyImageCap);
        Assert.Equal(1, plan.RunningImages);
        Assert.Equal(["draft", "standard"], plan.QualityClasses);
        Assert.Equal(14, plan.TrialDays);
        Assert.Equal(10, plan.TrialImageCap);

        Assert.True(plan.IsDefault);
        Assert.Null(plan.Source);
        Assert.Null(plan.EffectiveFrom);
        Assert.Null(plan.EffectiveUntil);
    }

    [Fact]
    public async Task An_account_put_on_a_plan_resolves_to_it_and_to_the_catalogues_figures_for_it()
    {
        var host = Host(Catalogue());

        Assert.Equal(AccountPlanChangeOutcome.Changed, await ChangeAsync(host, Sam, "pro", AccountPlanSource.Ops));

        var plan = await ResolveAsync(host, Sam);
        Assert.NotNull(plan);
        Assert.Equal("pro", plan.Key);
        Assert.Equal(18000, plan.Credits);
        Assert.Equal(PlanAllowancePeriod.Monthly, plan.Period);
        Assert.Equal(75, plan.DailyImageCap);
        Assert.Equal(2, plan.RunningImages);
        Assert.Null(plan.TrialDays);
        Assert.False(plan.IsDefault);
        Assert.Equal(AccountPlanSource.Ops, plan.Source);
        Assert.Equal(Start, plan.EffectiveFrom);
        Assert.Null(plan.EffectiveUntil);
    }

    /// <summary>
    /// A row lapsing to the default stores no key, so it follows <c>Billing:DefaultPlan</c> rather than freezing
    /// what the default happened to be on the day — the owner changing the default moves it too.
    /// </summary>
    [Fact]
    public async Task A_row_that_says_the_default_follows_whatever_the_default_is_configured_as()
    {
        var connection = Connection();
        var onTrial = Host(Catalogue(), connection);
        Assert.Equal(AccountPlanChangeOutcome.Changed, await ChangeAsync(onTrial, Sam, null, AccountPlanSource.Trial));

        var underTrial = await ResolveAsync(onTrial, Sam);
        Assert.NotNull(underTrial);
        Assert.Equal("trial", underTrial.Key);
        Assert.True(underTrial.IsDefault);
        Assert.Equal(AccountPlanSource.Trial, underTrial.Source);

        // The same database, read by a host whose default is now Creator.
        var underCreator = await ResolveAsync(Host([.. Catalogue(), ("Billing:DefaultPlan", "creator")], connection), Sam);
        Assert.NotNull(underCreator);
        Assert.Equal("creator", underCreator.Key);
        Assert.True(underCreator.IsDefault);
    }

    /// <summary>
    /// A plan taken out of the catalogue while accounts are still on it never throws on a creator's request
    /// (DL.2): the account reads as the default, and the log names the row to look at — not the person.
    /// </summary>
    [Fact]
    public async Task A_plan_the_catalogue_no_longer_has_resolves_to_the_default_and_is_logged()
    {
        var host = Host(Catalogue());
        var id = Guid.NewGuid();
        await WithDbAsync(host, db =>
        {
            db.AccountPlans.Add(new AccountPlan
            {
                Id = id,
                AccountId = Sam,
                PlanKey = "legacy",
                Source = AccountPlanSource.Ops,
                EffectiveFrom = Start.AddDays(-30),
            });
            return db.SaveChangesAsync();
        });

        var plan = await ResolveAsync(host, Sam);

        Assert.NotNull(plan);
        Assert.Equal("trial", plan.Key);
        Assert.True(plan.IsDefault);
        Assert.Equal(AccountPlanSource.Ops, plan.Source);

        var warning = Assert.Single(_logs.Entries, entry => entry.Contains("legacy", StringComparison.Ordinal));
        Assert.Contains(id.ToString(), warning, StringComparison.Ordinal);
        Assert.DoesNotContain(Sam, warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A downgrade at period end (B-54) is a row that starts later. Until it does, the account is on what it paid
    /// for, and the resolution says when that ends.
    /// </summary>
    [Fact]
    public async Task A_scheduled_change_is_not_in_force_until_it_starts_and_says_when_it_will()
    {
        var host = Host(Catalogue());
        var periodEnd = Start.AddDays(30);

        Assert.Equal(AccountPlanChangeOutcome.Changed, await ChangeAsync(host, Sam, "pro", AccountPlanSource.Ops));
        Assert.Equal(
            AccountPlanChangeOutcome.Changed,
            await ChangeAsync(host, Sam, "creator", AccountPlanSource.Stripe, periodEnd));

        var before = await ResolveAsync(host, Sam);
        Assert.NotNull(before);
        Assert.Equal("pro", before.Key);
        Assert.Equal(periodEnd, before.EffectiveUntil);

        _clock.UtcNow = periodEnd;

        var after = await ResolveAsync(host, Sam);
        Assert.NotNull(after);
        Assert.Equal("creator", after.Key);
        Assert.Equal(AccountPlanSource.Stripe, after.Source);
        Assert.Equal(periodEnd, after.EffectiveFrom);
        Assert.Null(after.EffectiveUntil);
    }

    [Fact]
    public async Task Before_an_accounts_first_row_starts_it_is_on_the_default()
    {
        var host = Host(Catalogue());
        var tomorrow = Start.AddDays(1);

        Assert.Equal(
            AccountPlanChangeOutcome.Changed,
            await ChangeAsync(host, Sam, "studio", AccountPlanSource.Ops, tomorrow));

        var plan = await ResolveAsync(host, Sam);
        Assert.NotNull(plan);
        Assert.Equal("trial", plan.Key);
        Assert.True(plan.IsDefault);
        Assert.Null(plan.Source);
        Assert.Equal(tomorrow, plan.EffectiveUntil);
    }

    /// <summary>
    /// Every deployment today configures no plans. That resolves to no plan at all — not the default, not an
    /// error — so whatever reads a plan keeps the flat limits it had; and there is nothing to put an account on.
    /// </summary>
    [Fact]
    public async Task With_no_catalogue_an_account_is_on_no_plan_and_cannot_be_put_on_one()
    {
        var host = Host([("Billing:PublishPlans", "false")]);

        Assert.Null(await ResolveAsync(host, Sam));
        Assert.Equal(AccountPlanChangeOutcome.UnknownPlan, await ChangeAsync(host, Sam, "pro", AccountPlanSource.Ops));
        Assert.Equal(AccountPlanChangeOutcome.UnknownPlan, await ChangeAsync(host, Sam, null, AccountPlanSource.Ops));
        Assert.Equal(0, await CountAsync(host));
    }

    /// <summary>
    /// The account-scoped counterpart of a two-workspace test: one account's plan, change or scheduled change is
    /// invisible to another's resolution, and the same instants can start rows for both.
    /// </summary>
    [Fact]
    public async Task One_accounts_plan_is_never_anothers()
    {
        var host = Host(Catalogue());

        Assert.Equal(AccountPlanChangeOutcome.Changed, await ChangeAsync(host, Sam, "studio", AccountPlanSource.Ops));
        Assert.Equal(AccountPlanChangeOutcome.Changed, await ChangeAsync(host, Alex, "creator", AccountPlanSource.Ops));
        Assert.Equal(
            AccountPlanChangeOutcome.Changed,
            await ChangeAsync(host, Alex, "pro", AccountPlanSource.Ops, Start.AddDays(7)));

        var sam = await ResolveAsync(host, Sam);
        var alex = await ResolveAsync(host, Alex);
        var nobody = await ResolveAsync(host, "user-nobody");

        Assert.Equal("studio", sam!.Key);
        Assert.Null(sam.EffectiveUntil);
        Assert.Equal("creator", alex!.Key);
        Assert.Equal(Start.AddDays(7), alex.EffectiveUntil);
        Assert.Equal("trial", nobody!.Key);
        Assert.True(nobody.IsDefault);
    }

    /// <summary>
    /// The refusals a change meets before the schema would refuse it, each with nothing written: a plan that is
    /// not offered, a start already past, and a start inside the span of a row already written — an overlap.
    /// </summary>
    [Fact]
    public async Task A_change_that_would_overlap_rewrite_history_or_name_no_plan_is_refused_and_writes_nothing()
    {
        var host = Host(Catalogue());
        Assert.Equal(
            AccountPlanChangeOutcome.Changed,
            await ChangeAsync(host, Sam, "pro", AccountPlanSource.Ops, Start.AddDays(10)));

        Assert.Equal(
            AccountPlanChangeOutcome.NotAfterLatest,
            await ChangeAsync(host, Sam, "creator", AccountPlanSource.Ops, Start.AddDays(5)));
        Assert.Equal(
            AccountPlanChangeOutcome.NotAfterLatest,
            await ChangeAsync(host, Sam, "creator", AccountPlanSource.Ops, Start.AddDays(10)));
        Assert.Equal(
            AccountPlanChangeOutcome.InThePast,
            await ChangeAsync(host, Sam, "creator", AccountPlanSource.Ops, Start.AddSeconds(-1)));
        Assert.Equal(
            AccountPlanChangeOutcome.UnknownPlan,
            await ChangeAsync(host, Sam, "enterprise", AccountPlanSource.Ops));

        Assert.Equal(1, await CountAsync(host));
    }

    /// <summary>
    /// An upgrade asked for now, after a downgrade was scheduled for period end, cannot go on the end of the chain:
    /// it would arrive at period end and still report a change. It is refused and nothing is written, until AP.6.2
    /// designs replacing a scheduled change.
    /// </summary>
    [Fact]
    public async Task A_change_for_now_behind_a_scheduled_one_is_refused_and_writes_nothing()
    {
        var host = Host(Catalogue());
        Assert.Equal(AccountPlanChangeOutcome.Changed, await ChangeAsync(host, Sam, "pro", AccountPlanSource.Ops));
        Assert.Equal(
            AccountPlanChangeOutcome.Changed,
            await ChangeAsync(host, Sam, "creator", AccountPlanSource.Stripe, Start.AddDays(30)));

        _clock.UtcNow = Start.AddDays(3);

        Assert.Equal(
            AccountPlanChangeOutcome.ChangeScheduled,
            await ChangeAsync(host, Sam, "studio", AccountPlanSource.Stripe));
        Assert.Equal(2, await CountAsync(host));
        Assert.Equal("pro", (await ResolveAsync(host, Sam))!.Key);
    }

    /// <summary>
    /// Refused before the database, because the data layer reads a refused write as a race and a caller that
    /// retries on a race would retry these for ever.
    /// </summary>
    [Fact]
    public async Task A_change_with_no_account_or_no_source_is_refused_before_the_database()
    {
        var host = Host(Catalogue());

        await Assert.ThrowsAsync<ArgumentException>(() => ChangeAsync(host, " ", "pro", AccountPlanSource.Ops));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ChangeAsync(host, new string('a', 451), "pro", AccountPlanSource.Ops));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ChangeAsync(host, Sam, "pro", AccountPlanSource.Unspecified));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ChangeAsync(host, Sam, "pro", (AccountPlanSource)9));
        Assert.Equal(0, await CountAsync(host));
    }

    /// <summary>A start given with an offset is stored as the same instant in UTC.</summary>
    [Fact]
    public async Task A_start_given_with_an_offset_is_stored_in_utc()
    {
        var host = Host(Catalogue());
        var chicago = new DateTimeOffset(2037, 3, 2, 4, 0, 0, TimeSpan.FromHours(-5));

        Assert.Equal(AccountPlanChangeOutcome.Changed, await ChangeAsync(host, Sam, "pro", AccountPlanSource.Ops, chicago));

        var row = await WithDbAsync(host, db => db.AccountPlans.SingleAsync());
        Assert.Equal(TimeSpan.Zero, row.EffectiveFrom.Offset);
        Assert.Equal(chicago.UtcDateTime, row.EffectiveFrom.UtcDateTime);
    }

    /// <summary>
    /// Two changes on one clock reading — a frozen clock, or two in quick succession — both land, the second a
    /// tick after the first, so the chain stays in order without either caller choosing a time.
    /// </summary>
    [Fact]
    public async Task Two_changes_at_one_instant_both_land_in_order()
    {
        var host = Host(Catalogue());

        Assert.Equal(AccountPlanChangeOutcome.Changed, await ChangeAsync(host, Sam, "pro", AccountPlanSource.Ops));
        Assert.Equal(AccountPlanChangeOutcome.Changed, await ChangeAsync(host, Sam, "studio", AccountPlanSource.Ops));

        var rows = await WithDbAsync(host, db => db.AccountPlans.OrderBy(plan => plan.EffectiveFrom).ToListAsync());
        Assert.Equal(["pro", "studio"], rows.Select(row => row.PlanKey));
        Assert.Null(rows[0].SupersedesFrom);
        Assert.Equal(rows[0].EffectiveFrom, rows[1].SupersedesFrom);
        Assert.Equal(Start.AddTicks(1), rows[1].EffectiveFrom);

        _clock.UtcNow = Start.AddTicks(1);
        Assert.Equal("studio", (await ResolveAsync(host, Sam))!.Key);
    }

    /// <summary>A key is stored as the catalogue spells it, so "Pro" and "pro" cannot become two plans.</summary>
    [Fact]
    public async Task A_key_is_stored_as_the_catalogue_spells_it()
    {
        var host = Host(Catalogue());

        Assert.Equal(AccountPlanChangeOutcome.Changed, await ChangeAsync(host, Sam, "PRO", AccountPlanSource.Ops));

        var row = await WithDbAsync(host, db => db.AccountPlans.SingleAsync());
        Assert.Equal("pro", row.PlanKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task The_facade_refuses_to_resolve_no_account(string accountId)
    {
        var host = Host(Catalogue());
        using var scope = host.CreateScope();
        var facade = scope.ServiceProvider.GetRequiredService<IAccountPlanFacade>();

        await Assert.ThrowsAsync<ArgumentException>(() => facade.ResolveAsync(accountId, CancellationToken.None));
    }

    /// <summary>The trial is the default, as development configures it, and a monthly plan of each size.</summary>
    private static (string, string?)[] Catalogue() =>
    [
        ("Billing:PublishPlans", "false"),
        ("Billing:DefaultPlan", "trial"),
        .. Plan(0, "trial", "Trial", credits: 1000, cap: 5, running: 1, "Once", ["draft", "standard"], trialDays: 14, trialCap: 10),
        .. Plan(1, "creator", "Creator", credits: 6000, cap: 25, running: 1, "Monthly", ["draft", "standard", "high"]),
        .. Plan(2, "pro", "Pro", credits: 18000, cap: 75, running: 2, "Monthly", ["draft", "standard", "high", "high-2k"]),
        .. Plan(3, "studio", "Studio", credits: 40000, cap: 150, running: 3, "Monthly", ["draft", "standard", "high", "high-2k", "high-4k"]),
    ];

    private static IEnumerable<(string, string?)> Plan(
        int index, string key, string name, int credits, int cap, int running, string period, string[] classes,
        int? trialDays = null, int? trialCap = null)
    {
        var prefix = $"Billing:Plans:{index}:";
        yield return (prefix + "Key", key);
        yield return (prefix + "Name", name);
        yield return (prefix + "Credits", credits.ToString());
        yield return (prefix + "DailyImageCap", cap.ToString());
        yield return (prefix + "RunningImages", running.ToString());
        yield return (prefix + "Period", period);
        yield return (prefix + "TrialDays", trialDays?.ToString());
        yield return (prefix + "TrialImageCap", trialCap?.ToString());

        for (var i = 0; i < classes.Length; i++)
        {
            yield return ($"{prefix}QualityClasses:{i}", classes[i]);
        }
    }

    private Task<AccountPlanServiceModel?> ResolveAsync((string, string?)[] settings, string accountId) =>
        ResolveAsync(Host(settings), accountId);

    private static async Task<AccountPlanServiceModel?> ResolveAsync(ServiceProvider host, string accountId)
    {
        using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAccountPlanFacade>()
            .ResolveAsync(accountId, CancellationToken.None);
    }

    /// <remarks>Through Business: the change is not on the facade until AP.4.4 gives it an audit.</remarks>
    private static async Task<AccountPlanChangeOutcome> ChangeAsync(
        ServiceProvider host, string accountId, string? planKey, AccountPlanSource source,
        DateTimeOffset? effectiveFrom = null)
    {
        using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAccountPlanBusiness>()
            .ChangeAsync(accountId, planKey, source, effectiveFrom, CancellationToken.None);
    }

    private static Task<int> CountAsync(ServiceProvider host) => WithDbAsync(host, db => db.AccountPlans.CountAsync());

    private static async Task<T> WithDbAsync<T>(ServiceProvider host, Func<CreatorPantryDbContext, Task<T>> work)
    {
        using var scope = host.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>());
    }

    private SqliteConnection Connection()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        _connections.Add(connection);
        return connection;
    }

    /// <summary>A host over <paramref name="connection"/>, or over a database of its own when none is given.</summary>
    private ServiceProvider Host((string Key, string? Value)[] settings, SqliteConnection? connection = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        connection ??= Connection();

        var provider = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(_logs))
            .AddSingleton<IClock>(_clock)
            .AddBillingModule(configuration)
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreated();
        }

        _hosts.Add(provider);

        return provider;
    }

    private sealed class MovableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
