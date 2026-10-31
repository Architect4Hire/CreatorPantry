using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The plan and the credit packs on the account's own read of its allowance (PRICE-007/008, USAGE-008, AP.5.3):
/// which plan it is on, whether the allowance comes back, and what its packs hold — each the server's own figure.
/// </summary>
/// <remarks>
/// SQLite, because nothing here is a race. The catalogue is <see cref="PlanAllowanceTests"/>'s — trial 1,000 once,
/// Creator 6,000 and Pro 18,000 a month — with the trial as the default, so the trial is reachable with no plan row.
/// </remarks>
public sealed class AccountAiUsagePlanAndPackTests : IDisposable
{
    private const string Sam = "user-sam";

    private const string Alex = "user-alex";

    private static readonly DateTimeOffset Now = new(2037, 3, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private readonly FixedClock _clock = new(Now);

    private ServiceProvider? _provider;

    public AccountAiUsagePlanAndPackTests() => _connection.Open();

    public void Dispose()
    {
        _provider?.Dispose();
        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the plan ----------------------------------------------------------------------------------------

    /// <summary>Every production host today: no catalogue, so there is no plan to name and none is invented.</summary>
    [Fact]
    public async Task A_host_with_no_plan_catalogue_names_no_plan()
    {
        Start(plans: false);

        Assert.Null((await CurrentAsync(Sam)).Plan);
    }

    [Fact]
    public async Task The_plan_the_account_is_on_is_named()
    {
        Start();
        await PutOnAsync(Sam, "creator");

        var plan = (await CurrentAsync(Sam)).Plan;

        Assert.NotNull(plan);
        Assert.Equal("creator", plan.Key);
        Assert.Equal("Creator", plan.Name);
        Assert.True(plan.AllowanceRenews);
    }

    /// <summary>
    /// A trial's credits are granted once (B-48), so its allowance does not come back at the reset — and the page
    /// must not say it does. The default here is the trial, so an account with no plan row is on it.
    /// </summary>
    [Fact]
    public async Task A_trials_allowance_does_not_come_back()
    {
        Start();

        var plan = (await CurrentAsync(Sam)).Plan;

        Assert.NotNull(plan);
        Assert.Equal("trial", plan.Key);
        Assert.False(plan.AllowanceRenews);
    }

    /// <summary>
    /// USAGE-009: an administrator's allowance wins over the plan, and renews every period as that allowance does.
    /// The account is still on exactly one plan (PRICE-007), so the plan is still named.
    /// </summary>
    [Fact]
    public async Task An_ops_set_allowance_on_a_trial_still_comes_back_and_still_names_the_plan()
    {
        Start();
        await GiveQuotaAsync(Sam, AiQuotaUnit.Credits, allowance: 500m);

        var usage = await CurrentAsync(Sam);

        Assert.Equal(500m, usage.Period.Allowance);
        Assert.Equal("trial", usage.Plan?.Key);
        Assert.True(usage.Plan?.AllowanceRenews);
    }

    [Fact]
    public async Task Another_accounts_plan_is_not_this_accounts()
    {
        Start();
        await PutOnAsync(Alex, "pro");

        Assert.Equal("trial", (await CurrentAsync(Sam)).Plan?.Key);
        Assert.Equal("pro", (await CurrentAsync(Alex)).Plan?.Key);
    }

    // ---- the credit packs --------------------------------------------------------------------------------

    [Fact]
    public async Task An_account_with_no_grants_has_no_pack_credits()
    {
        Start();

        Assert.Equal(0m, (await CurrentAsync(Sam)).PackRemaining);
    }

    /// <summary>
    /// The packs are said beside the allowance, never added to it: the period's figures are unchanged by a grant,
    /// and what is left in each grant is summed into the one balance.
    /// </summary>
    [Fact]
    public async Task Pack_credits_are_the_unspent_grants_and_are_kept_apart_from_the_allowance()
    {
        Start();
        await GrantAsync(Sam, granted: 300m, remaining: 300m);
        await GrantAsync(Sam, granted: 200m, remaining: 120m);

        var usage = await CurrentAsync(Sam);

        Assert.Equal(420m, usage.PackRemaining);
        Assert.Equal(1000m, usage.Period.Allowance);
        Assert.Equal(1000m, usage.Period.Remaining);
    }

    /// <summary>A clawed-back grant has nothing left to spend, so it adds nothing to the balance (AP.5.1).</summary>
    [Fact]
    public async Task A_clawed_back_grant_adds_nothing()
    {
        Start();
        await GrantAsync(Sam, granted: 300m, remaining: 300m);
        await GrantAsync(Sam, granted: 500m, remaining: 0m, clawedBack: 500m);

        Assert.Equal(300m, (await CurrentAsync(Sam)).PackRemaining);
    }

    /// <summary>One word, one number: the balance a page shows is the one the next admission judges against.</summary>
    [Fact]
    public async Task The_pack_balance_matches_what_admission_reads()
    {
        Start();
        await GrantAsync(Sam, granted: 250m, remaining: 175m);

        using var scope = _provider!.CreateScope();
        var peek = await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
            .PeekAsync(Sam, AiTaskType.RecipeConcepts, isMeterable: true, Ct);

        Assert.Equal(peek.PackRemaining, (await CurrentAsync(Sam)).PackRemaining);
    }

    /// <summary>A period counted in tokens never spends packs (AP.5.2), so it reports none rather than a figure it cannot use.</summary>
    [Fact]
    public async Task A_period_counted_in_tokens_reports_no_pack_credits()
    {
        Start();
        await GiveQuotaAsync(Sam, AiQuotaUnit.Tokens, allowance: 100_000m);
        await GrantAsync(Sam, granted: 300m, remaining: 300m);

        Assert.Equal(0m, (await CurrentAsync(Sam)).PackRemaining);
    }

    /// <summary>The two-account case: another account's packs are never this account's (B-47).</summary>
    [Fact]
    public async Task Another_accounts_packs_are_not_this_accounts()
    {
        Start();
        await GrantAsync(Sam, granted: 40m, remaining: 40m);
        await GrantAsync(Alex, granted: 900m, remaining: 900m);

        Assert.Equal(40m, (await CurrentAsync(Sam)).PackRemaining);
        Assert.Equal(900m, (await CurrentAsync(Alex)).PackRemaining);
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private void Start(bool plans = true)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (plans)
        {
            values["Billing:DefaultPlan"] = "trial";
            Plan(values, 0, "trial", "Trial", 1000, "Once", trialDays: 14);
            Plan(values, 1, "creator", "Creator", 6000, "Monthly");
            Plan(values, 2, "pro", "Pro", 18000, "Monthly");
        }

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddBillingModule(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options => options.DefaultAllowance = 1000m)
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreated();
    }

    private static void Plan(
        Dictionary<string, string?> values, int index, string key, string name, int credits, string period,
        int? trialDays = null)
    {
        var prefix = $"Billing:Plans:{index}:";
        values[prefix + "Key"] = key;
        values[prefix + "Name"] = name;
        values[prefix + "Credits"] = credits.ToString();
        values[prefix + "DailyImageCap"] = "5";
        values[prefix + "RunningImages"] = "1";
        values[prefix + "Period"] = period;
        values[prefix + "TrialDays"] = trialDays?.ToString();
        values[prefix + "QualityClasses:0"] = "standard";
    }

    private async Task<AccountAiUsageServiceModel> CurrentAsync(string accountId)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiUsageReadFacade>().GetCurrentAsync(accountId, Ct);
    }

    /// <remarks>Through Business, as <see cref="PlanAllowanceTests"/> does: the audited facade write needs an actor.</remarks>
    private async Task PutOnAsync(string accountId, string planKey)
    {
        using var scope = _provider!.CreateScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<IAccountPlanBusiness>()
            .ChangeAsync(accountId, planKey, AccountPlanSource.Ops, effectiveFrom: null, audit: null, Ct);

        Assert.Equal(AccountPlanChangeOutcome.Changed, outcome.Outcome);
    }

    private async Task GiveQuotaAsync(string accountId, AiQuotaUnit unit, decimal allowance)
    {
        using var scope = _provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Unit = unit,
            Allowance = allowance,
            PeriodLength = AiQuotaPeriodLength.Monthly,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            IsSuspended = false,
            EffectiveFrom = Now.AddDays(-30),
            LastChangedAt = Now.AddDays(-30),
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task GrantAsync(string accountId, decimal granted, decimal remaining, decimal clawedBack = 0m)
    {
        using var scope = _provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountCreditGrants.Add(new AccountCreditGrant
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Source = CreditGrantSource.Ops,
            CreditsGranted = granted,
            CreditsRemaining = remaining,
            CreditsClawedBack = clawedBack,
            GrantedAt = Now.AddDays(-5),
            ClawedBackAt = clawedBack > 0m ? Now.AddDays(-1) : null,
        });

        await db.SaveChangesAsync(Ct);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
