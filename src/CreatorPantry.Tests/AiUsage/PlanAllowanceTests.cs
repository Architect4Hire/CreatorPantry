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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// Where a period's allowance comes from once accounts have plans, and what a plan change does to it (AP.4.2):
/// an ops-set allowance wins, otherwise the plan in force at the period's start; an upgrade adds the difference
/// at once, a downgrade waits for the roll, and a trial's credits are granted once.
/// </summary>
/// <remarks>
/// SQLite, because nothing here is a race; two admissions applying one upgrade at once is the period's row version,
/// which <see cref="AccountAiQuotaConcurrencyTests"/> covers for every write to it. The catalogue is development's
/// shape — trial 1,000 once, Creator 6,000, Pro 18,000, Studio 40,000 a month — with the trial as the default, so
/// the trial's rule is reachable with no plan row at all.
/// </remarks>
public sealed class PlanAllowanceTests : IDisposable
{
    private const string Sam = "user-sam";

    private const string Alex = "user-alex";

    /// <summary>The platform default, deliberately unlike any plan's credits so a test can tell them apart.</summary>
    private const decimal PlatformDefault = 777m;

    /// <summary>Ten credits a call, one call a run: an admitted text run holds ten.</summary>
    private const decimal Hold = 10m;

    /// <summary>The twentieth of February: the March period is ahead, so a change can be made before it starts.</summary>
    private static readonly DateTimeOffset BeforeMarch = new(2037, 2, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset MarchStart = new(2037, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset AprilStart = new(2037, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private readonly MovableClock _clock = new(BeforeMarch);

    private ServiceProvider? _provider;

    public PlanAllowanceTests() => _connection.Open();

    public void Dispose()
    {
        _provider?.Dispose();
        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- where the allowance comes from --------------------------------------------------------------

    [Fact]
    public async Task With_no_quota_of_its_own_an_account_opens_with_its_plans_credits()
    {
        Start();
        await PutOnAsync(Sam, "creator");
        _clock.Set(MarchStart.AddDays(2));

        await AdmitAsync(Sam);

        var period = await PeriodAsync(Sam);
        Assert.Equal(6000m, period.Allowance);
        Assert.Equal("creator", period.PlanKey);
        Assert.Equal(0m, period.UpgradeGranted);
    }

    /// <summary>USAGE-009: an allowance an administrator set is the account's, whatever its plan says.</summary>
    [Fact]
    public async Task An_ops_set_allowance_wins_over_the_plan_and_is_never_topped_up()
    {
        Start();
        await PutOnAsync(Sam, "pro");
        await GiveQuotaAsync(Sam, allowance: 500m);

        await AdmitAsync(Sam);
        await PutOnAsync(Sam, "studio");
        await AdmitAsync(Sam);

        var period = await PeriodAsync(Sam);
        Assert.Equal(500m, period.Allowance);
        Assert.Null(period.PlanKey);
        Assert.Equal(0m, period.UpgradeGranted);
    }

    /// <summary>
    /// A quota row that names no allowance — one that only suspends or sets a zone, say — means "the platform
    /// default", and under plans the default is the plan.
    /// </summary>
    [Fact]
    public async Task A_quota_row_with_no_allowance_leaves_the_number_to_the_plan()
    {
        Start();
        await PutOnAsync(Sam, "pro");
        await GiveQuotaAsync(Sam, allowance: null);
        _clock.Set(MarchStart.AddDays(2));

        await AdmitAsync(Sam);

        var period = await PeriodAsync(Sam);
        Assert.Equal(18000m, period.Allowance);
        Assert.Equal("pro", period.PlanKey);
    }

    /// <summary>A plan's credits are a monthly allowance in credits; a weekly ops term keeps the platform default.</summary>
    [Fact]
    public async Task A_quota_row_on_other_terms_with_no_allowance_keeps_the_platform_default()
    {
        Start();
        await PutOnAsync(Sam, "pro");
        await GiveQuotaAsync(Sam, allowance: null, length: AiQuotaPeriodLength.Weekly);

        await AdmitAsync(Sam);

        var period = await PeriodAsync(Sam);
        Assert.Equal(PlatformDefault, period.Allowance);
        Assert.Null(period.PlanKey);
    }

    /// <summary>Every deployment today: no catalogue, no plan, and the flat default as before plans existed.</summary>
    [Fact]
    public async Task With_no_plans_configured_the_platform_default_stands()
    {
        Start(plans: false);

        await AdmitAsync(Sam);

        var period = await PeriodAsync(Sam);
        Assert.Equal(PlatformDefault, period.Allowance);
        Assert.Null(period.PlanKey);
    }

    /// <summary>
    /// The platform default is for an account with no plan at all, not for one whose plan left the catalogue: that
    /// account reads as the default plan (DL.2), and opens with the default plan's credits (DL.8). The default here
    /// is Creator, not the catalogue's first plan, so reading the first plan would not pass for reading the default.
    /// </summary>
    [Fact]
    public async Task A_plan_taken_out_of_the_catalogue_opens_with_the_default_plans_credits()
    {
        Start(defaultPlan: "creator");
        await PutOnRetiredPlanAsync(Sam, "legacy");
        _clock.Set(MarchStart.AddDays(2));

        await AdmitAsync(Sam);

        var period = await PeriodAsync(Sam);
        Assert.Equal(6000m, period.Allowance);
        Assert.Equal("creator", period.PlanKey);
    }

    /// <summary>A plan's credits are credits; a quota row metered in tokens keeps the platform default.</summary>
    [Fact]
    public async Task A_quota_row_in_tokens_with_no_allowance_keeps_the_platform_default()
    {
        Start();
        await PutOnAsync(Sam, "pro");
        await GiveQuotaAsync(Sam, allowance: null, unit: AiQuotaUnit.Tokens);

        await AdmitAsync(Sam);

        var period = await PeriodAsync(Sam);
        Assert.Equal(PlatformDefault, period.Allowance);
        Assert.Null(period.PlanKey);
    }

    // ---- what a change does to the running period ----------------------------------------------------

    /// <summary>
    /// <strong>The difference, never the whole plan</strong> (owner, 2026-10-27): six thousand spent of
    /// Creator's six thousand, then Pro, leaves twelve thousand — not eighteen.
    /// </summary>
    [Fact]
    public async Task An_upgrade_mid_period_adds_the_difference_at_once()
    {
        Start();
        await PutOnAsync(Sam, "creator");
        _clock.Set(MarchStart.AddDays(2));
        await AdmitAsync(Sam);

        await PutOnAsync(Sam, "pro");
        var admission = await AdmitAsync(Sam);

        var period = await PeriodAsync(Sam);
        Assert.Equal(6000m, period.Allowance);
        Assert.Equal(12000m, period.UpgradeGranted);
        Assert.Equal("creator", period.PlanKey);
        Assert.Equal(18000m - 2 * Hold, admission.Remaining);
    }

    /// <summary>B-54: the creator keeps what the period opened with, and the lower plan arrives at the roll.</summary>
    [Fact]
    public async Task A_downgrade_mid_period_waits_for_the_next_roll()
    {
        Start();
        await PutOnAsync(Sam, "studio");
        _clock.Set(MarchStart.AddDays(3));
        await AdmitAsync(Sam);

        await PutOnAsync(Sam, "creator");
        await AdmitAsync(Sam);

        var march = await PeriodAsync(Sam);
        Assert.Equal(40000m, march.Allowance);
        Assert.Equal(0m, march.UpgradeGranted);

        _clock.Set(AprilStart.AddDays(2));
        await AdmitAsync(Sam);

        var april = await PeriodAsync(Sam);
        Assert.Equal(6000m, april.Allowance);
        Assert.Equal("creator", april.PlanKey);
    }

    /// <summary>Up, down and up again in one period: the difference is granted once, and nothing is taken back.</summary>
    [Fact]
    public async Task Upgrading_down_and_up_again_grants_the_difference_once()
    {
        Start();
        await PutOnAsync(Sam, "creator");
        _clock.Set(MarchStart.AddDays(2));
        await AdmitAsync(Sam);

        // A minute apart: two changes on one clock reading would put the second a tick ahead of now, where a third
        // would read as landing behind a scheduled change.
        await PutOnAsync(Sam, "pro");
        await AdmitAsync(Sam);
        _clock.Set(_clock.UtcNow.AddMinutes(1));
        await PutOnAsync(Sam, "creator");
        await AdmitAsync(Sam);
        _clock.Set(_clock.UtcNow.AddMinutes(1));
        await PutOnAsync(Sam, "pro");
        await AdmitAsync(Sam);

        var period = await PeriodAsync(Sam);
        Assert.Equal(6000m, period.Allowance);
        Assert.Equal(12000m, period.UpgradeGranted);
    }

    /// <summary>
    /// The plan read is the one in force at the period's start, not when its row happens to be written: an
    /// account idle across the roll still opens with the plan it rolled over on, so a downgrade made after the
    /// roll waits for the next one as it would have for an account that used AI on the first.
    /// </summary>
    [Fact]
    public async Task A_period_opened_late_still_opens_with_the_plan_in_force_at_its_start()
    {
        Start();
        await PutOnAsync(Sam, "studio");
        await PutOnAsync(Sam, "creator", effectiveFrom: MarchStart.AddDays(4));

        _clock.Set(MarchStart.AddDays(9));
        await AdmitAsync(Sam);

        var period = await PeriodAsync(Sam);
        Assert.Equal(MarchStart, period.StartsAt);
        Assert.Equal(40000m, period.Allowance);
        Assert.Equal("studio", period.PlanKey);
    }

    /// <summary>
    /// An upgrade made before an idle account's period is written still lands at once: opened with the plan at
    /// the start, then topped up to the plan now.
    /// </summary>
    [Fact]
    public async Task An_upgrade_made_before_the_period_is_written_is_granted_when_it_opens()
    {
        Start();
        await PutOnAsync(Sam, "creator");

        _clock.Set(MarchStart.AddDays(4));
        await PutOnAsync(Sam, "pro");
        await AdmitAsync(Sam);

        var period = await PeriodAsync(Sam);
        Assert.Equal(6000m, period.Allowance);
        Assert.Equal(12000m, period.UpgradeGranted);
    }

    /// <summary>
    /// A plan first set after the period started: the period opens under the plan in force at its start — the
    /// default — and the new plan's credits arrive at once as the difference. The total is the new plan's.
    /// </summary>
    [Fact]
    public async Task A_plan_set_after_the_period_started_is_its_credits_in_all_from_then()
    {
        Start();
        _clock.Set(MarchStart.AddDays(9));
        await PutOnAsync(Sam, "creator");

        var admission = await AdmitAsync(Sam);

        var period = await PeriodAsync(Sam);
        Assert.Equal("trial", period.PlanKey);
        Assert.Equal(1000m, period.Allowance);
        Assert.Equal(5000m, period.UpgradeGranted);
        Assert.Equal(6000m - Hold, admission.Remaining);
    }

    // ---- the trial ----------------------------------------------------------------------------------

    /// <summary>
    /// B-48: the trial's credits are for the trial. The default plan here is the trial, so an account with no plan
    /// row is on it — and its second period opens with nothing.
    /// </summary>
    [Fact]
    public async Task A_trials_credits_are_granted_once_in_an_accounts_life()
    {
        Start();
        _clock.Set(MarchStart.AddDays(2));

        await AdmitAsync(Sam);
        var march = await PeriodAsync(Sam);
        Assert.Equal(1000m, march.Allowance);
        Assert.Equal("trial", march.PlanKey);

        _clock.Set(AprilStart.AddDays(2));
        var april = await AdmitAsync(Sam);

        Assert.Equal(AiQuotaAdmissionOutcome.Exhausted, april.Outcome);
        var period = await PeriodAsync(Sam);
        Assert.Equal(0m, period.Allowance);
        Assert.Equal("trial", period.PlanKey);
    }

    /// <summary>Upgrading off a spent trial grants the paid plan's credits, less what the period already had.</summary>
    [Fact]
    public async Task Upgrading_off_a_spent_trial_grants_the_paid_plans_credits()
    {
        Start();
        _clock.Set(MarchStart.AddDays(2));
        await AdmitAsync(Sam);
        _clock.Set(AprilStart.AddDays(2));
        await AdmitAsync(Sam);

        await PutOnAsync(Sam, "creator");
        var admitted = await AdmitAsync(Sam);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, admitted.Outcome);
        var period = await PeriodAsync(Sam);
        Assert.Equal(0m, period.Allowance);
        Assert.Equal(6000m, period.UpgradeGranted);
    }

    /// <summary>
    /// Once in an account's life, not once in a run of trial periods: back on the trial after a paid month, the
    /// trial it had before the paid month still counts.
    /// </summary>
    [Fact]
    public async Task A_trial_returned_to_after_a_paid_period_grants_nothing_again()
    {
        Start();
        _clock.Set(MarchStart.AddDays(2));
        await AdmitAsync(Sam);

        _clock.Set(MarchStart.AddDays(20));
        await PutOnAsync(Sam, "creator");
        _clock.Set(AprilStart.AddDays(2));
        await AdmitAsync(Sam);
        Assert.Equal("creator", (await PeriodAsync(Sam)).PlanKey);

        await PutOnAsync(Sam, null);
        _clock.Set(AprilStart.AddMonths(1).AddDays(2));
        await AdmitAsync(Sam);

        var may = await PeriodAsync(Sam);
        Assert.Equal(0m, may.Allowance);
        Assert.Equal("trial", may.PlanKey);
    }

    /// <summary>One account's trial is not another's: the record of a grant is per account.</summary>
    [Fact]
    public async Task One_accounts_spent_trial_does_not_spend_anothers()
    {
        Start();
        _clock.Set(MarchStart.AddDays(2));
        await AdmitAsync(Sam);

        _clock.Set(AprilStart.AddDays(2));
        await AdmitAsync(Alex);

        Assert.Equal(1000m, (await PeriodAsync(Alex)).Allowance);
    }

    // ---- reading without writing --------------------------------------------------------------------

    /// <summary>
    /// The read seam shows the upgrade at once, without writing it: a peek is a GET, and the top-up is
    /// persisted by the admission that next opens the period.
    /// </summary>
    [Fact]
    public async Task A_peek_shows_an_upgrade_without_writing_it()
    {
        Start();
        await PutOnAsync(Sam, "creator");
        _clock.Set(MarchStart.AddDays(2));
        await AdmitAsync(Sam);
        await PutOnAsync(Sam, "pro");

        var peek = await PeekAsync(Sam);

        Assert.Equal(18000m, peek.Allowance);
        Assert.Equal(18000m - Hold, peek.Remaining);
        Assert.Equal(0m, (await PeriodAsync(Sam)).UpgradeGranted);

        await AdmitAsync(Sam);
        Assert.Equal(12000m, (await PeriodAsync(Sam)).UpgradeGranted);
    }

    /// <summary>An account that has never run anything is shown exactly what its first admission then opens with.</summary>
    [Fact]
    public async Task A_projected_period_matches_the_one_admission_opens()
    {
        Start();
        await PutOnAsync(Sam, "pro");
        _clock.Set(MarchStart.AddDays(2));

        var peek = await PeekAsync(Sam);
        Assert.Empty(await PeriodsAsync(Sam));

        var admission = await AdmitAsync(Sam);

        Assert.Equal(18000m, peek.Allowance);
        Assert.Equal(peek.Remaining - Hold, admission.Remaining);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private void Start(bool plans = true, string defaultPlan = "trial")
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (plans)
        {
            values["Billing:DefaultPlan"] = defaultPlan;
            Plan(values, 0, "trial", "Trial", 1000, "Once", trialDays: 14);
            Plan(values, 1, "creator", "Creator", 6000, "Monthly");
            Plan(values, 2, "pro", "Pro", 18000, "Monthly");
            Plan(values, 3, "studio", "Studio", 40000, "Monthly");
        }

        _provider = new ServiceCollection()
            .AddApplicationTime()
            .AddBillingModule(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultAllowance = PlatformDefault;
                options.DefaultTaskEstimate = Hold;
                options.EstimatedCallsPerRun = 1;
            })
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

    /// <remarks>Through Business: the change is not on the facade until AP.4.4 gives it an audit.</remarks>
    private async Task PutOnAsync(string accountId, string? planKey, DateTimeOffset? effectiveFrom = null)
    {
        using var scope = _provider!.CreateScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<IAccountPlanBusiness>()
            .ChangeAsync(accountId, planKey, AccountPlanSource.Ops, effectiveFrom, audit: null, Ct);

        Assert.Equal(AccountPlanChangeOutcome.Changed, outcome.Outcome);
    }

    /// <remarks>Written directly: Business refuses a key the catalogue does not have, which is what a retired one is.</remarks>
    private async Task PutOnRetiredPlanAsync(string accountId, string planKey)
    {
        using var scope = _provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountPlans.Add(new AccountPlan
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PlanKey = planKey,
            Source = AccountPlanSource.Ops,
            EffectiveFrom = _clock.UtcNow.AddDays(-30),
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task GiveQuotaAsync(
        string accountId,
        decimal? allowance,
        AiQuotaPeriodLength length = AiQuotaPeriodLength.Monthly,
        AiQuotaUnit unit = AiQuotaUnit.Credits)
    {
        using var scope = _provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Unit = unit,
            Allowance = allowance,
            PeriodLength = length,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            IsSuspended = false,
            EffectiveFrom = _clock.UtcNow.AddDays(-30),
            LastChangedAt = _clock.UtcNow.AddDays(-30),
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync(string accountId)
    {
        using var scope = _provider!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitAsync(
            new AiQuotaAdmissionRequestServiceModel(
                accountId, Guid.NewGuid(), Guid.NewGuid(), AiTaskType.RecipeConcepts, true,
                _clock.UtcNow + AiPolicy.LeaseDuration),
            Ct);
    }

    private async Task<AiQuotaStateServiceModel> PeekAsync(string accountId)
    {
        using var scope = _provider!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
            .PeekAsync(accountId, AiTaskType.RecipeConcepts, true, Ct);
    }

    /// <summary>The account's period covering now.</summary>
    private async Task<AccountAiQuotaPeriod> PeriodAsync(string accountId) =>
        (await PeriodsAsync(accountId)).Single(period => period.StartsAt <= _clock.UtcNow && period.EndsAt > _clock.UtcNow);

    private async Task<List<AccountAiQuotaPeriod>> PeriodsAsync(string accountId)
    {
        using var scope = _provider!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AccountAiQuotaPeriods
            .AsNoTracking()
            .Where(period => period.AccountId == accountId)
            .ToListAsync(Ct);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Set(DateTimeOffset instant) => UtcNow = instant;
    }
}
