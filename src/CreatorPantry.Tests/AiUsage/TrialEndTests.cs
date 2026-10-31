extern alias ApiService;

using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// A trial ends <c>TrialDays</c> after its account was created, and from then on no AI work is admitted while the
/// account is still on it — pack credits included (AP.7.1, <c>ai-pricing.md</c> B-48, B-54; owner, 2026-10-29).
/// Running out of the trial's credits or pictures before then refuses that work only, and does not end it.
/// </summary>
/// <remarks>
/// Billing and AiUsage together over SQLite, with a catalogue whose default is the trial, as the daily-limits
/// document proposes for a deployment. The accounts exist in Identity's table, because the trial's start is read
/// from there; an account that does not is <see cref="An_account_identity_has_no_record_of_has_no_trial_end"/>.
/// </remarks>
public sealed class TrialEndTests : IDisposable
{
    private const string Sam = "user-sam";

    private const string Alex = "user-alex";

    private const string Model = "test-image";

    private const int TrialDays = 14;

    private static readonly DateTimeOffset Created = new(2037, 3, 3, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Ends = Created.AddDays(TrialDays);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private readonly MovableClock _clock = new(Created.AddDays(1));

    private ServiceProvider? _provider;

    public TrialEndTests() => _connection.Open();

    public void Dispose()
    {
        _provider?.Dispose();
        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- when it ends ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_trial_ends_its_days_after_the_account_was_created()
    {
        await StartAsync();

        var plan = await ResolveAsync(Sam);

        Assert.Equal("trial", plan.Key);
        Assert.Equal(Ends, plan.TrialEndsAt);
        Assert.False(plan.TrialHasEnded(Ends.AddTicks(-1)));
        Assert.True(plan.TrialHasEnded(Ends));
    }

    /// <summary>
    /// One date per account: a row that puts an older account on the trial later does not start a second trial,
    /// and the account's own creation is what is read.
    /// </summary>
    [Fact]
    public async Task A_trial_is_dated_from_the_account_not_from_the_row_that_put_it_on_the_trial()
    {
        await StartAsync();
        _clock.Set(Created.AddDays(30));
        await PutOnAsync(Sam, "studio");
        _clock.Set(Created.AddDays(31));
        await PutOnAsync(Sam, "trial");

        var plan = await ResolveAsync(Sam);

        Assert.Equal("trial", plan.Key);
        Assert.Equal(Ends, plan.TrialEndsAt);
        Assert.Equal(AiQuotaAdmissionOutcome.TrialEnded, (await AdmitTextAsync(Sam)).Outcome);
    }

    [Fact]
    public async Task A_paid_plan_has_no_trial_end()
    {
        await StartAsync();
        await PutOnAsync(Sam, "studio");

        var plan = await ResolveAsync(Sam);

        Assert.Null(plan.TrialEndsAt);
        Assert.False(plan.TrialHasEnded(_clock.UtcNow.AddYears(5)));
    }

    /// <summary>
    /// An erased account has no session to ask from; work it queued before is bounded by its trial's once-only credits
    /// and picture cap, not refused for a date nobody can read.
    /// </summary>
    [Fact]
    public async Task An_account_identity_has_no_record_of_has_no_trial_end()
    {
        await StartAsync();
        _clock.Set(Created.AddDays(60));

        Assert.Null((await ResolveAsync("user-erased")).TrialEndsAt);
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitTextAsync("user-erased")).Outcome);
    }

    // ---- what it refuses -------------------------------------------------------------------------------

    /// <summary>The last instant of the trial admits work; its end refuses it, text and pictures alike.</summary>
    [Fact]
    public async Task At_its_end_every_kind_of_work_is_refused_and_not_one_tick_before()
    {
        await StartAsync();

        _clock.Set(Ends.AddTicks(-1));
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitTextAsync(Sam)).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitPictureAsync(Sam)).Outcome);

        _clock.Set(Ends);
        var text = await AdmitTextAsync(Sam);
        var picture = await AdmitPictureAsync(Sam);

        Assert.Equal(AiQuotaAdmissionOutcome.TrialEnded, text.Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.TrialEnded, picture.Outcome);
        Assert.Equal(Ends, text.TrialEndsAt);
        Assert.Equal(Ends, picture.TrialEndsAt);

        // Nothing held, no balance and no reset: the remedy is a plan, not waiting.
        Assert.Null(text.ReservationId);
        Assert.Equal(0m, text.Remaining);
        Assert.Null(text.PeriodEndsAt);
        Assert.Null(picture.ReservationId);
    }

    /// <summary>
    /// Asked about before anything is queued, the answer is the same: the request-time peek and the picture quote
    /// both say the trial has ended, with no figures that could read as a balance to spend.
    /// </summary>
    [Fact]
    public async Task The_peek_and_the_quote_say_the_trial_has_ended()
    {
        await StartAsync();
        _clock.Set(Ends.AddDays(1));

        var state = await PeekAsync(Sam);
        Assert.Equal(AiQuotaStateOutcome.TrialEnded, state.Outcome);
        Assert.Equal(Ends, state.TrialEndedAt);
        Assert.Equal(0m, state.Remaining);
        Assert.Null(state.ResetsAt);

        var quote = await QuoteAsync(Sam);
        Assert.Equal(AiImageQuoteOutcome.TrialEnded, quote.Outcome);
        Assert.Null(quote.PerPicture);
        Assert.Equal(0, quote.AffordableVariants);
    }

    /// <summary>Pack credits do not keep an ended trial going (owner, 2026-10-29): the account must choose a plan.</summary>
    [Fact]
    public async Task Pack_credits_do_not_keep_an_ended_trial_going()
    {
        await StartAsync();
        await GrantPackAsync(Sam, 5000m);
        _clock.Set(Ends);

        Assert.Equal(AiQuotaAdmissionOutcome.TrialEnded, (await AdmitTextAsync(Sam)).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.TrialEnded, (await AdmitPictureAsync(Sam)).Outcome);
    }

    /// <summary>A plan bought after the trial ended lifts it at once; the trial's date is about the trial only.</summary>
    [Fact]
    public async Task A_plan_bought_after_the_trial_ended_admits_work_at_once()
    {
        await StartAsync();
        _clock.Set(Ends.AddDays(3));
        Assert.Equal(AiQuotaAdmissionOutcome.TrialEnded, (await AdmitTextAsync(Sam)).Outcome);

        await PutOnAsync(Sam, "studio");

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitTextAsync(Sam)).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitPictureAsync(Sam)).Outcome);
    }

    /// <summary>
    /// An administrator who sets an account's allowance has decided what it may spend, trial or not (USAGE-009).
    /// </summary>
    [Fact]
    public async Task An_ops_set_allowance_is_not_refused_for_the_trials_end()
    {
        await StartAsync();
        await GiveQuotaAsync(Sam, 2000m);
        _clock.Set(Ends.AddDays(3));

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitTextAsync(Sam)).Outcome);
    }

    /// <summary>One account's trial ending says nothing about another's, created later.</summary>
    [Fact]
    public async Task Two_accounts_trials_end_on_their_own_dates()
    {
        await StartAsync();
        await AddAccountAsync(Alex, Created.AddDays(10));
        _clock.Set(Ends.AddDays(1));

        Assert.Equal(AiQuotaAdmissionOutcome.TrialEnded, (await AdmitTextAsync(Sam)).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitTextAsync(Alex)).Outcome);
    }

    // ---- running out does not end it --------------------------------------------------------------------

    /// <summary>
    /// The trial's pictures spent, its writing goes on (B-54): the lifetime cap refuses pictures only, and the trial
    /// still ends on its date rather than then.
    /// </summary>
    [Fact]
    public async Task Spending_the_trials_pictures_refuses_pictures_only()
    {
        await StartAsync();

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitPictureAsync(Sam, 3, trialCap: 3)).Outcome);

        var refused = await AdmitPictureAsync(Sam, 1, trialCap: 3);

        Assert.Equal(AiQuotaAdmissionOutcome.TrialImageCap, refused.Outcome);
        Assert.Equal(3, refused.TrialImageCap);
        Assert.Equal(Ends, refused.TrialEndsAt);
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitTextAsync(Sam)).Outcome);
        Assert.Equal(Ends, (await ResolveAsync(Sam)).TrialEndsAt);
    }

    /// <summary>
    /// The trial's credits spent, it is exhausted rather than ended: the once-only allowance is gone, and the trial
    /// still ends on its date.
    /// </summary>
    [Fact]
    public async Task Spending_the_trials_credits_does_not_end_it()
    {
        await StartAsync(trialCredits: 10);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitTextAsync(Sam)).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.Exhausted, (await AdmitTextAsync(Sam)).Outcome);

        _clock.Set(Ends);
        Assert.Equal(AiQuotaAdmissionOutcome.TrialEnded, (await AdmitTextAsync(Sam)).Outcome);
    }

    // ---- how it is said ------------------------------------------------------------------------------

    /// <summary>
    /// Both refusals are 403s with nothing to retry, as a suspension is: neither ends by waiting, and a 429 would send
    /// a client round a loop that cannot end differently.
    /// </summary>
    [Theory]
    [InlineData(AiQuotaErrors.TrialEnded)]
    [InlineData(AiQuotaErrors.TrialImagesSpent)]
    public void Each_trial_refusal_is_a_403_with_nothing_to_retry(string code)
    {
        var status = ApiService::CreatorPantry.ApiService.Http.ProblemResults.StatusFor(code);

        Assert.Equal(403, status);
        Assert.Equal(
            ApiService::CreatorPantry.ApiService.Http.ProblemResults.RetryFor(AiQuotaErrors.Suspended, 403),
            ApiService::CreatorPantry.ApiService.Http.ProblemResults.RetryFor(code, status));
    }

    // ---- one source -----------------------------------------------------------------------------------

    /// <summary>
    /// What admission is handed is the plan's own <c>TrialImageCap</c> and <c>TrialDays</c>, read from the options the
    /// catalogue publishes, never a copy: change them there and the account's plan says so.
    /// </summary>
    [Fact]
    public async Task The_cap_and_the_length_are_read_from_the_catalogue_entry_itself()
    {
        await StartAsync();

        var trial = Catalogue().Plans.Single(plan => plan.Key == "trial");
        trial.TrialImageCap = 7;
        trial.TrialDays = 30;

        var plan = await ResolveAsync(Sam);

        Assert.Equal(7, plan.TrialImageCap);
        Assert.Equal(Created.AddDays(30), plan.TrialEndsAt);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private async Task StartAsync(int trialCredits = 1000)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Billing:DefaultPlan"] = "trial",
        };

        Plan(values, 0, "trial", trialCredits, "Once", trialDays: TrialDays, trialCap: 10);
        Plan(values, 1, "studio", 40000, "Monthly");

        _provider = new ServiceCollection()
            .AddApplicationTime()
            .AddBillingModule(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultTaskEstimate = 10m;
                options.EstimatedCallsPerRun = 1;
                options.ImageRates[Model] = 50m;
            })
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreated();
        }

        await AddAccountAsync(Sam, Created);
    }

    private static void Plan(
        Dictionary<string, string?> values, int index, string key, int credits, string period,
        int? trialDays = null, int? trialCap = null)
    {
        var prefix = $"Billing:Plans:{index}:";
        values[prefix + "Key"] = key;
        values[prefix + "Name"] = key;
        values[prefix + "Credits"] = credits.ToString();
        values[prefix + "DailyImageCap"] = "5";
        values[prefix + "RunningImages"] = "1";
        values[prefix + "Period"] = period;
        values[prefix + "TrialDays"] = trialDays?.ToString();
        values[prefix + "TrialImageCap"] = trialCap?.ToString();
        values[prefix + "QualityClasses:0"] = "standard";
    }

    private BillingOptions Catalogue() => _provider!.GetRequiredService<IOptions<BillingOptions>>().Value;

    private async Task AddAccountAsync(string accountId, DateTimeOffset created)
    {
        using var scope = _provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.Users.Add(new ApplicationUser
        {
            Id = accountId,
            UserName = $"{accountId}@example.test",
            Email = $"{accountId}@example.test",
            DisplayName = accountId,
            CreatedAt = created,
        });

        await db.SaveChangesAsync(Ct);
    }

    /// <remarks>Through Business: an ops change needs an audit, which this is not about.</remarks>
    private async Task PutOnAsync(string accountId, string planKey)
    {
        using var scope = _provider!.CreateScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<IAccountPlanBusiness>()
            .ChangeAsync(accountId, planKey, AccountPlanSource.Ops, null, audit: null, Ct);

        Assert.Equal(AccountPlanChangeOutcome.Changed, outcome.Outcome);
    }

    private async Task<AccountPlanServiceModel> ResolveAsync(string accountId)
    {
        using var scope = _provider!.CreateScope();

        return (await scope.ServiceProvider.GetRequiredService<IAccountPlanFacade>().ResolveAsync(accountId, Ct))!;
    }

    private async Task GrantPackAsync(string accountId, decimal credits)
    {
        using var scope = _provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountCreditGrants.Add(new AccountCreditGrant
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Source = CreditGrantSource.Ops,
            CreditsGranted = credits,
            CreditsRemaining = credits,
            CreditsClawedBack = 0m,
            GrantedAt = _clock.UtcNow,
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task GiveQuotaAsync(string accountId, decimal allowance)
    {
        using var scope = _provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Unit = AiQuotaUnit.Credits,
            Allowance = allowance,
            PeriodLength = AiQuotaPeriodLength.Monthly,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            IsSuspended = false,
            EffectiveFrom = _clock.UtcNow.AddDays(-1),
            LastChangedAt = _clock.UtcNow.AddDays(-1),
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task<AiQuotaAdmissionServiceModel> AdmitTextAsync(string accountId)
    {
        using var scope = _provider!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitAsync(
            new AiQuotaAdmissionRequestServiceModel(
                accountId, Guid.NewGuid(), Guid.NewGuid(), AiTaskType.RecipeConcepts, true,
                _clock.UtcNow + AiPolicy.LeaseDuration),
            Ct);
    }

    private async Task<AiQuotaAdmissionServiceModel> AdmitPictureAsync(
        string accountId, int pictures = 1, int? trialCap = null)
    {
        using var scope = _provider!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitImageAsync(
            new AiImageQuotaAdmissionRequestServiceModel(
                accountId, Guid.NewGuid(), pictures, Model, null, null, _clock.UtcNow.AddHours(6),
                DailyImageCap: null, TrialImageCap: trialCap),
            Ct);
    }

    private async Task<AiQuotaStateServiceModel> PeekAsync(string accountId)
    {
        using var scope = _provider!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
            .PeekAsync(accountId, AiTaskType.RecipeConcepts, true, Ct);
    }

    private async Task<AiImageQuoteServiceModel> QuoteAsync(string accountId)
    {
        using var scope = _provider!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
            .QuoteImageAsync(accountId, Model, null, null, 2, Ct);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Set(DateTimeOffset instant) => UtcNow = instant;
    }
}
