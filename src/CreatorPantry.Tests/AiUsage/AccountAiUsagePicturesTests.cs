using CreatorPantry.Domain.Managers.Limits;
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
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// Today's pictures, and a trial's in all, on the account's own read of its allowance (DLIM-008, DL.9): the caps
/// admission is handed, counted by admission's own query, each figure the server's.
/// </summary>
/// <remarks>
/// SQLite, because nothing here is a race. Trial: 5 a day and 10 in all over 14 days, and the default plan. Creator:
/// 25 a day. That the figures are the quote's, across two workspaces and through the gateway, is
/// <c>ImagesJourneyEndToEndTests.Todays_pictures_are_the_accounts_across_its_workspaces_and_agree_with_the_quote</c>.
/// </remarks>
public sealed class AccountAiUsagePicturesTests : IDisposable
{
    private const string Sam = "user-sam";

    private const string Alex = "user-alex";

    private static readonly DateTimeOffset Now = new(2037, 3, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Midnight = new(2037, 3, 16, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private readonly SettableClock _clock = new(Now);

    private ServiceProvider? _provider;

    private Guid _periodId;

    public AccountAiUsagePicturesTests() => _connection.Open();

    public void Dispose()
    {
        _provider?.Dispose();
        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- when there is no figure -------------------------------------------------------------------------

    /// <summary>Every production host today: no catalogue, so no cap is enforced and none is stated.</summary>
    [Fact]
    public async Task A_host_with_no_plan_catalogue_states_no_picture_cap()
    {
        await StartAsync(plans: false);

        var usage = await CurrentAsync(Sam);

        Assert.Null(usage.PicturesToday);
        Assert.Null(usage.TrialPictures);
    }

    /// <summary>
    /// The plan's day is a limit and switches off with limits (B-56), so it is not stated; a trial's pictures in all
    /// are the trial's own term and are (AP.7.2).
    /// </summary>
    [Fact]
    public async Task With_limits_off_the_day_is_not_stated_and_a_trials_pictures_still_are()
    {
        await StartAsync(limits: false);

        var usage = await CurrentAsync(Sam);

        Assert.Null(usage.PicturesToday);
        Assert.Equal(new AccountAiUsageTrialPicturesServiceModel(10, 0, 10), usage.TrialPictures);
    }

    [Fact]
    public async Task A_suspended_account_is_told_no_picture_figures()
    {
        await StartAsync();
        await SuspendAsync(Sam);

        var usage = await CurrentAsync(Sam);

        Assert.True(usage.IsSuspended);
        Assert.Null(usage.PicturesToday);
        Assert.Null(usage.TrialPictures);
    }

    /// <summary>Admission refuses an ended trial before it counts, so "5 left today" would promise what it refuses.</summary>
    [Fact]
    public async Task A_trial_that_has_ended_is_told_no_picture_figures()
    {
        await StartAsync();
        await AddAccountAsync(Sam, created: Now.AddDays(-20));

        var usage = await CurrentAsync(Sam);

        Assert.Null(usage.PicturesToday);
        Assert.Null(usage.TrialPictures);
    }

    // ---- the figures -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_paid_plan_with_nothing_made_has_its_whole_day_and_no_trial_figure()
    {
        await StartAsync();
        await PutOnAsync(Sam, "creator");

        var usage = await CurrentAsync(Sam);

        Assert.Equal(new AccountAiUsagePicturesTodayServiceModel(25, 0, 25, Midnight), usage.PicturesToday);
        Assert.Null(usage.TrialPictures);
    }

    /// <summary>
    /// Counted as admission counts: a hold still waiting counts every picture asked for, a settled one what was
    /// charged, and one given back nothing. Yesterday's count against the trial and not against today.
    /// </summary>
    [Fact]
    public async Task Held_and_charged_pictures_count_and_given_back_ones_do_not()
    {
        await StartAsync();
        await SeedPicturesAsync(Sam, AiQuotaReservationStatus.Held, requested: 2);
        await SeedPicturesAsync(Sam, AiQuotaReservationStatus.Settled, requested: 2, billed: 1);
        await SeedPicturesAsync(Sam, AiQuotaReservationStatus.Released, requested: 3);
        await SeedPicturesAsync(Sam, AiQuotaReservationStatus.Expired, requested: 3);
        await SeedPicturesAsync(Sam, AiQuotaReservationStatus.Settled, requested: 4, billed: 4, heldAt: Now.AddDays(-1));

        var usage = await CurrentAsync(Sam);

        Assert.Equal(new AccountAiUsagePicturesTodayServiceModel(5, 3, 2, Midnight), usage.PicturesToday);
        Assert.Equal(new AccountAiUsageTrialPicturesServiceModel(10, 7, 3), usage.TrialPictures);
    }

    /// <summary>
    /// A day already past the cap — pictures held under a bigger plan before a downgrade — reads nothing left, never
    /// a negative count for a page to print. Seeded past it directly: how the plan came down is DL.8's.
    /// </summary>
    [Fact]
    public async Task A_day_past_its_cap_has_nothing_left_rather_than_less_than_nothing()
    {
        await StartAsync();
        await SeedPicturesAsync(Sam, AiQuotaReservationStatus.Settled, requested: 8, billed: 8);

        var usage = await CurrentAsync(Sam);

        Assert.Equal(new AccountAiUsagePicturesTodayServiceModel(5, 8, 0, Midnight), usage.PicturesToday);
        Assert.Equal(new AccountAiUsageTrialPicturesServiceModel(10, 8, 2), usage.TrialPictures);
    }

    [Fact]
    public async Task Another_accounts_pictures_are_not_counted()
    {
        await StartAsync();
        await SeedPicturesAsync(Alex, AiQuotaReservationStatus.Settled, requested: 4, billed: 4);

        var usage = await CurrentAsync(Sam);

        Assert.Equal(0, usage.PicturesToday?.Used);
        Assert.Equal(0, usage.TrialPictures?.Used);
        Assert.Equal(4, (await CurrentAsync(Alex)).PicturesToday?.Used);
    }

    /// <summary>The day turns over at UTC midnight, and the read says when.</summary>
    [Fact]
    public async Task The_day_starts_again_at_utc_midnight()
    {
        await StartAsync();
        await PutOnAsync(Sam, "creator");
        await SeedPicturesAsync(Sam, AiQuotaReservationStatus.Settled, requested: 3, billed: 3);

        _clock.Set(Midnight);
        var usage = await CurrentAsync(Sam);

        Assert.Equal(new AccountAiUsagePicturesTodayServiceModel(25, 0, 25, Midnight.AddDays(1)), usage.PicturesToday);
    }

    /// <summary>A GET: reading the figures opens no period.</summary>
    [Fact]
    public async Task Reading_the_figures_opens_no_period()
    {
        await StartAsync(seedPeriod: false);

        _ = await CurrentAsync(Sam);

        using var scope = _provider!.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AccountAiQuotaPeriods.CountAsync(Ct));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private async Task StartAsync(bool plans = true, bool limits = true, bool seedPeriod = true)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (plans)
        {
            values["Billing:DefaultPlan"] = "trial";
            Plan(values, 0, "trial", "Trial", 1000, "Once", dailyCap: 5, trialDays: 14, trialCap: 10);
            Plan(values, 1, "creator", "Creator", 6000, "Monthly", dailyCap: 25);
        }

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddBillingModule(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options => options.DefaultAllowance = 1000m)
            .Configure<LimitsOptions>(options => options.Enabled = limits)
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        if (!seedPeriod)
        {
            return;
        }

        // The rows a hold names. Its figures are not what any test here is about.
        var period = new AccountAiQuotaPeriod
        {
            Id = Guid.NewGuid(),
            AccountId = "user-seed",
            StartsAt = Now.AddDays(-14),
            EndsAt = Now.AddDays(16),
            TimeZoneId = "Etc/UTC",
            LocalStartDate = DateOnly.FromDateTime(Now.AddDays(-14).UtcDateTime),
            Unit = AiQuotaUnit.Credits,
            Allowance = 1000m,
        };

        db.AccountAiQuotaPeriods.Add(period);
        await db.SaveChangesAsync(Ct);
        _periodId = period.Id;
    }

    private static void Plan(
        Dictionary<string, string?> values, int index, string key, string name, int credits, string period,
        int dailyCap, int? trialDays = null, int? trialCap = null)
    {
        var prefix = $"Billing:Plans:{index}:";
        values[prefix + "Key"] = key;
        values[prefix + "Name"] = name;
        values[prefix + "Credits"] = credits.ToString();
        values[prefix + "DailyImageCap"] = dailyCap.ToString();
        values[prefix + "RunningImages"] = "1";
        values[prefix + "Period"] = period;
        values[prefix + "TrialDays"] = trialDays?.ToString();
        values[prefix + "TrialImageCap"] = trialCap?.ToString();
        values[prefix + "QualityClasses:0"] = "standard";
    }

    private async Task<AccountAiUsageServiceModel> CurrentAsync(string accountId)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiUsageReadFacade>().GetCurrentAsync(accountId, Ct);
    }

    /// <summary>A picture hold as admission writes one: <paramref name="requested"/> asked for, <paramref name="billed"/> charged.</summary>
    private async Task SeedPicturesAsync(
        string accountId, AiQuotaReservationStatus status, int requested, int? billed = null, DateTimeOffset? heldAt = null)
    {
        using var scope = _provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var held = heldAt ?? Now.AddHours(-1);
        var open = status is AiQuotaReservationStatus.Held;
        var settled = status is AiQuotaReservationStatus.Settled or AiQuotaReservationStatus.Released;

        db.AccountAiQuotaReservations.Add(new AccountAiQuotaReservation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PeriodId = _periodId,
            AiOperationId = Guid.NewGuid(),
            LeaseToken = Guid.NewGuid(),
            TaskType = AiTaskType.ImageGeneration,
            Unit = AiQuotaUnit.Credits,
            ReservedAmount = requested * 10m,
            ReservedFromAllowance = requested * 10m,
            PerAttemptEstimate = 10m,
            SettledAmount = settled ? (billed ?? 0) * 10m : null,
            ChargedToAllowance = billed * 10m,
            Status = status,
            HeldAt = held,
            ExpiresAt = held.AddMinutes(30),
            SettledAt = settled ? held.AddMinutes(1) : null,
            ReleasedAt = open ? null : held.AddMinutes(1),
            PostedAt = billed is null ? null : held.AddMinutes(1),
            ImagesRequested = requested,
            ImagesBilled = billed,
        });

        await db.SaveChangesAsync(Ct);
    }

    /// <remarks>Through Business, as <see cref="AccountAiUsagePlanAndPackTests"/> does: the audited facade write needs an actor.</remarks>
    private async Task PutOnAsync(string accountId, string planKey)
    {
        using var scope = _provider!.CreateScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<IAccountPlanBusiness>()
            .ChangeAsync(accountId, planKey, AccountPlanSource.Ops, effectiveFrom: null, audit: null, Ct);

        Assert.Equal(AccountPlanChangeOutcome.Changed, outcome.Outcome);
    }

    private async Task SuspendAsync(string accountId)
    {
        using var scope = _provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Unit = AiQuotaUnit.Credits,
            PeriodLength = AiQuotaPeriodLength.Monthly,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            IsSuspended = true,
            EffectiveFrom = Now.AddDays(-30),
            LastChangedAt = Now.AddDays(-30),
        });

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The account row a trial's end is counted from (AP.7.1).</summary>
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

    private sealed class SettableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Set(DateTimeOffset instant) => UtcNow = instant;
    }
}
