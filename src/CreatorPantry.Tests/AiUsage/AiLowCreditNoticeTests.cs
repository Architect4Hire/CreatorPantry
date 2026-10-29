using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The low-credit notice (PRICE-011 as amended, <c>ai-pricing.md</c> B-59): raised by an admission that leaves
/// an account low, said once a period, dismissible, and switchable off.
/// </summary>
/// <remarks>
/// <para>
/// One worker at a time. Two admissions crossing the line at the same instant are
/// <see cref="AiLowCreditNoticeConcurrencyTests"/>, on real SQL Server, for the reason
/// <see cref="AccountAiQuotaConcurrencyTests"/> exists: SQLite never regenerates a row version, so a race
/// here would pass whatever the code did.
/// </para>
/// <para>
/// The allowance is 1000 credits and the line is a tenth of it, so an account is low at 100 or less. A run
/// holds twenty, which makes "the admission that crosses it" an exact number rather than a rounding.
/// </para>
/// </remarks>
public sealed class AiLowCreditNoticeTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MembershipInA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private const string Account = "user-sam";
    private const string OtherAccount = "user-alex";

    private const decimal Allowance = 1000m;

    /// <summary>Ten credits a call, two calls a run.</summary>
    private const decimal Hold = 20m;

    private const string ImageModel = "test-image-model";

    private static readonly DateTimeOffset PeriodStart = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));

    public AiLowCreditNoticeTests()
    {
        _connection.Open();
        _provider = BuildProvider(markThrows: false);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.Add(new Workspace { Id = WorkspaceA, Name = "A", Slug = "a", CreatedAt = _clock.UtcNow });
        db.Users.AddRange(User(Account), User(OtherAccount));
        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = MembershipInA,
            WorkspaceId = WorkspaceA,
            UserId = Account,
            Role = WorkspaceRole.Owner,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = _clock.UtcNow,
        });

        db.SaveChanges();
    }

    /// <summary>
    /// The module over the one in-memory database, optionally with a data layer whose mark throws.
    /// </summary>
    /// <remarks>
    /// A decorator rather than a hook in the module: nothing in production has a seam for making a notice
    /// fail, and adding one so a test could reach it would be a worse answer than wrapping the interface the
    /// test already has access to.
    /// </remarks>
    private ServiceProvider BuildProvider(bool markThrows, bool throwOnce = false)
    {
        var services = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddApplicationTime()
            .AddBillingModule()
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultAllowance = Allowance;
                options.DefaultTaskEstimate = 10m;
                options.EstimatedCallsPerRun = 2;

                // A picture costs 25 credits, so two of them cross the line from the same seeded period a
                // written run does: pictures spend the same allowance and bring the same notice.
                options.ImageRates[ImageModel] = 25m;
            })
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>());

        if (markThrows)
        {
            services.AddScoped<AiQuotaDataLayer>();
            var failures = new MarkFailures { Remaining = throwOnce ? 1 : int.MaxValue };

            services.AddScoped<IAiQuotaDataLayer>(provider =>
                new MarkThrowsDataLayer(provider.GetRequiredService<AiQuotaDataLayer>(), failures));
        }

        return services.BuildServiceProvider(validateScopes: true);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- the trigger -----------------------------------------------------------------------------------

    [Fact]
    public async Task An_admission_that_leaves_the_account_above_the_line_raises_nothing()
    {
        await SeedPeriodAsync(consumed: 500m);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync()).Outcome);
        Assert.Null((await PeriodAsync()).LowCreditNoticedAt);
    }

    /// <summary>
    /// A tenth of the allowance or less, counted after the hold: the admission that takes the account under
    /// the line is the one that raises the notice, not the one after it.
    /// </summary>
    [Theory]
    [InlineData(879, false)]
    [InlineData(880, true)]
    [InlineData(950, true)]
    public async Task The_admission_that_crosses_the_line_raises_the_notice(int consumed, bool expected)
    {
        await SeedPeriodAsync(consumed: consumed);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync()).Outcome);
        Assert.Equal(expected, (await PeriodAsync()).LowCreditNoticedAt is not null);
    }

    /// <summary>
    /// The rule B-59 asks for in one test: however many admissions cross the line, the account is told once,
    /// and the instant recorded is the first one's.
    /// </summary>
    [Fact]
    public async Task However_many_admissions_cross_it_the_notice_is_raised_once()
    {
        await SeedPeriodAsync(consumed: 900m);

        await AdmitAsync();
        var first = (await PeriodAsync()).LowCreditNoticedAt;

        _clock.Advance(TimeSpan.FromMinutes(5));
        await AdmitAsync();
        await AdmitAsync();

        Assert.NotNull(first);
        Assert.Equal(first, (await PeriodAsync()).LowCreditNoticedAt);
    }

    /// <summary>
    /// Packs are spendable credits (AP.5.2), so they count. An account with a pack in hand is not running low
    /// however little of the period's allowance is left — which is exactly why this line cannot be
    /// <c>NearlySpentFraction</c>, which counts the allowance alone.
    /// </summary>
    [Fact]
    public async Task A_pack_balance_above_the_line_is_not_running_low()
    {
        await SeedPeriodAsync(consumed: 950m);
        await SeedPackAsync(500m);

        await AdmitAsync();

        Assert.Null((await PeriodAsync()).LowCreditNoticedAt);
    }

    [Fact]
    public async Task A_pack_balance_that_still_leaves_the_account_low_raises_the_notice()
    {
        await SeedPeriodAsync(consumed: 950m);
        await SeedPackAsync(40m);

        await AdmitAsync();

        Assert.NotNull((await PeriodAsync()).LowCreditNoticedAt);
    }

    /// <summary>
    /// A refusal is not a notice. The account is already being told something stronger, and the exhausted
    /// state offers the same pack (B-59).
    /// </summary>
    [Fact]
    public async Task A_refused_admission_raises_nothing()
    {
        await SeedPeriodAsync(consumed: Allowance);

        Assert.Equal(AiQuotaAdmissionOutcome.Exhausted, (await AdmitAsync()).Outcome);
        Assert.Null((await PeriodAsync()).LowCreditNoticedAt);
    }

    /// <summary>A replayed admission finds its own hold and writes nothing — including no notice.</summary>
    [Fact]
    public async Task A_replayed_admission_raises_nothing_new()
    {
        await SeedPeriodAsync(consumed: 900m);

        var operationId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();

        await AdmitAsync(operationId, leaseToken);
        var raised = (await PeriodAsync()).LowCreditNoticedAt;

        _clock.Advance(TimeSpan.FromMinutes(5));
        await AdmitAsync(operationId, leaseToken);

        Assert.Equal(raised, (await PeriodAsync()).LowCreditNoticedAt);
    }

    /// <summary>
    /// Pictures spend the same credits writing does, so they bring the same notice (B-59). The same seeded
    /// period, crossed by a picture instead of a completion.
    /// </summary>
    [Fact]
    public async Task A_picture_that_crosses_the_line_raises_the_notice()
    {
        await SeedPeriodAsync(consumed: 950m);

        var admission = await AdmitImageAsync(pictures: 1);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, admission.Outcome);
        Assert.NotNull((await PeriodAsync()).LowCreditNoticedAt);
    }

    [Fact]
    public async Task A_picture_that_leaves_the_account_above_the_line_raises_nothing()
    {
        await SeedPeriodAsync(consumed: 500m);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitImageAsync(pictures: 1)).Outcome);
        Assert.Null((await PeriodAsync()).LowCreditNoticedAt);
    }

    /// <summary>Pictures and writing share the period's one column, so together they still tell once.</summary>
    [Fact]
    public async Task A_picture_and_a_written_run_crossing_the_line_tell_once()
    {
        await SeedPeriodAsync(consumed: 900m);

        await AdmitImageAsync(pictures: 1);
        var first = (await PeriodAsync()).LowCreditNoticedAt;

        _clock.Advance(TimeSpan.FromMinutes(5));
        await AdmitAsync();

        Assert.NotNull(first);
        Assert.Equal(first, (await PeriodAsync()).LowCreditNoticedAt);
    }

    /// <summary>A picture the daily cap refuses spends nothing, so it tells nobody anything.</summary>
    [Fact]
    public async Task A_picture_refused_by_the_daily_cap_raises_nothing()
    {
        await SeedPeriodAsync(consumed: 950m);

        var admission = await AdmitImageAsync(pictures: 1, dailyImageCap: 0);

        Assert.Equal(AiQuotaAdmissionOutcome.DailyImageCap, admission.Outcome);
        Assert.Null((await PeriodAsync()).LowCreditNoticedAt);
    }

    /// <summary>
    /// An unmeterable run cannot spend anything, so it opens no period and can leave nobody low.
    /// </summary>
    [Fact]
    public async Task An_unmeterable_run_raises_nothing()
    {
        await SeedPeriodAsync(consumed: 950m);

        Assert.Equal(AiQuotaAdmissionOutcome.NotMetered, (await AdmitAsync(meterable: false)).Outcome);
        Assert.Null((await PeriodAsync()).LowCreditNoticedAt);
    }

    /// <summary>
    /// "None for an account with no allowance period" (B-59): a period opened with nothing to spend has no
    /// fraction to fall under, so there is no line to cross.
    /// </summary>
    [Fact]
    public async Task A_period_with_no_allowance_raises_nothing()
    {
        await SeedQuotaAsync(allowance: 0m);
        await SeedPeriodAsync(consumed: 0m, allowance: 0m);

        await AdmitAsync();

        Assert.Null((await PeriodAsync()).LowCreditNoticedAt);
    }

    /// <summary>
    /// <strong>And nor does one whose packs are the only thing it can spend</strong>, which is the same rule
    /// and is worth stating rather than discovering: a tenth of nought is nought, so an account spending a
    /// pack against a zero allowance is never told it is running low. It is still refused at zero and still
    /// told why (USAGE-006). Recorded here as the behaviour B-59's rule produces, not as a happy one.
    /// </summary>
    [Fact]
    public async Task A_period_with_no_allowance_but_packs_to_spend_still_raises_nothing()
    {
        await SeedQuotaAsync(allowance: 0m);
        await SeedPeriodAsync(consumed: 0m, allowance: 0m);
        await SeedPackAsync(30m);

        var admission = await AdmitAsync();

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, admission.Outcome);
        Assert.Null((await PeriodAsync()).LowCreditNoticedAt);
    }

    /// <summary>
    /// A token-denominated period never spends packs and has no pack to be offered, so it is never told to
    /// buy one (AP.5.2).
    /// </summary>
    [Fact]
    public async Task A_period_counted_in_tokens_raises_nothing()
    {
        await SeedQuotaAsync(allowance: Allowance, unit: AiQuotaUnit.Tokens);
        await SeedPeriodAsync(consumed: 950m, unit: AiQuotaUnit.Tokens);

        await AdmitAsync();

        Assert.Null((await PeriodAsync()).LowCreditNoticedAt);
    }

    /// <summary>
    /// The notice is raised after the hold has committed and may never take it back: a mark that throws
    /// leaves the work admitted, and the creator is told at the next admission instead.
    /// </summary>
    [Fact]
    public async Task A_notice_that_cannot_be_written_still_admits_the_work()
    {
        await using var provider = BuildProvider(markThrows: true);

        await SeedPeriodAsync(consumed: 950m, provider: provider);

        var admission = await AdmitAsync(provider: provider);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, admission.Outcome);
        Assert.NotNull(admission.ReservationId);
        Assert.Equal(Allowance - 950m - Hold, admission.Remaining);

        // And the hold is really there, rather than the admission merely saying so.
        using var scope = provider.CreateScope();
        Assert.Equal(
            Hold,
            (await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
                .AccountAiQuotaPeriods.AsNoTracking()
                .SingleAsync(period => period.AccountId == Account, TestContext.Current.CancellationToken))
                .Reserved);
    }

    /// <summary>
    /// What "the creator is told at the next admission instead" actually means: a mark that failed once is
    /// not a notice lost, because the column is still null and the next admission finds the account low.
    /// </summary>
    [Fact]
    public async Task A_notice_that_failed_once_is_raised_by_the_next_admission()
    {
        await using var provider = BuildProvider(markThrows: true, throwOnce: true);

        await SeedPeriodAsync(consumed: 900m, provider: provider);

        await AdmitAsync(provider: provider);

        using (var scope = provider.CreateScope())
        {
            Assert.Null(
                await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
                    .AccountAiQuotaPeriods.AsNoTracking()
                    .Where(period => period.AccountId == Account)
                    .Select(period => period.LowCreditNoticedAt)
                    .SingleAsync(TestContext.Current.CancellationToken));
        }

        await AdmitAsync(provider: provider);

        using var after = provider.CreateScope();
        Assert.NotNull(
            await after.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
                .AccountAiQuotaPeriods.AsNoTracking()
                .Where(period => period.AccountId == Account)
                .Select(period => period.LowCreditNoticedAt)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    // ---- what the account reads ------------------------------------------------------------------------

    [Fact]
    public async Task An_account_that_has_not_been_told_reads_no_notice()
    {
        await SeedPeriodAsync(consumed: 950m);

        var low = (await CurrentAsync()).LowCredit;

        Assert.False(low.IsNoticed);
        Assert.False(low.IsDismissed);
        Assert.True(low.NoticeEnabled);
    }

    /// <summary>
    /// The figure the notice quotes is the allowance left and the packs together, summed by the server so the
    /// notice and the admission that raised it cannot disagree about it.
    /// </summary>
    [Fact]
    public async Task The_read_reports_the_notice_and_what_is_left_to_spend()
    {
        await SeedPeriodAsync(consumed: 950m);
        await SeedPackAsync(30m);
        await AdmitAsync();

        var usage = await CurrentAsync();

        Assert.True(usage.LowCredit.IsNoticed);

        // Literals, not the same sum computed twice: 1000 less 950 spent and 20 held, plus a 30-credit pack.
        Assert.Equal(30m, usage.Period.Remaining);
        Assert.Equal(30m, usage.PackRemaining);
        Assert.Equal(60m, usage.LowCredit.Spendable);
    }

    [Fact]
    public async Task Dismissing_it_is_reported_and_does_not_unraise_it()
    {
        await SeedPeriodAsync(consumed: 950m);
        await AdmitAsync();

        await DismissAsync();

        var low = (await CurrentAsync()).LowCredit;

        Assert.True(low.IsNoticed);
        Assert.True(low.IsDismissed);
    }

    /// <summary>A second dismissal is the state the caller asked for, not an error, and moves nothing.</summary>
    [Fact]
    public async Task Dismissing_twice_records_the_first_instant()
    {
        await SeedPeriodAsync(consumed: 950m);
        await AdmitAsync();
        await DismissAsync();

        var first = (await PeriodAsync()).LowCreditNoticeDismissedAt;

        _clock.Advance(TimeSpan.FromMinutes(5));
        await DismissAsync();

        Assert.Equal(first, (await PeriodAsync()).LowCreditNoticeDismissedAt);
    }

    /// <summary>Nothing to dismiss is not a failure: an account that has never been low may still ask.</summary>
    [Fact]
    public async Task Dismissing_when_there_is_nothing_to_dismiss_writes_nothing()
    {
        await SeedPeriodAsync(consumed: 100m);

        await DismissAsync();

        Assert.Null((await PeriodAsync()).LowCreditNoticeDismissedAt);
    }

    [Fact]
    public async Task Dismissing_with_no_period_open_opens_none()
    {
        await DismissAsync();

        Assert.Empty(await AllPeriodsAsync());
    }

    /// <summary>
    /// A dismissal lasts the period it was about. The next period is a new row, so an account that runs low
    /// again is told again.
    /// </summary>
    [Fact]
    public async Task A_dismissal_does_not_survive_the_roll()
    {
        await SeedPeriodAsync(consumed: 950m);
        await AdmitAsync();
        await DismissAsync();

        _clock.Advance(TimeSpan.FromDays(20));
        await AdmitAsync();

        var rolled = (await AllPeriodsAsync()).Single(period => period.StartsAt == PeriodEnd);

        Assert.Null(rolled.LowCreditNoticedAt);
        Assert.Null(rolled.LowCreditNoticeDismissedAt);
    }

    // ---- the setting -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_notice_is_on_for_an_account_that_has_never_chosen()
    {
        Assert.True((await CurrentAsync()).LowCredit.NoticeEnabled);
        Assert.Empty(await SettingsAsync());
    }

    [Fact]
    public async Task Turning_it_off_is_reported_and_the_mark_is_still_made()
    {
        await SetEnabledAsync(false);
        await SeedPeriodAsync(consumed: 950m);
        await AdmitAsync();

        var usage = await CurrentAsync();

        Assert.False(usage.LowCredit.NoticeEnabled);

        // The mark is a fact about the balance, not about being shown: turning the notice back on mid-period
        // should not need the creator to spend again before they are told.
        Assert.True(usage.LowCredit.IsNoticed);
    }

    [Fact]
    public async Task Turning_it_off_and_on_again_leaves_one_row()
    {
        await SetEnabledAsync(false);
        await SetEnabledAsync(true);

        Assert.True((await CurrentAsync()).LowCredit.NoticeEnabled);
        Assert.Single(await SettingsAsync());
    }

    /// <summary>Setting it to what it already is writes nothing, so no row appears for a no-op.</summary>
    [Fact]
    public async Task Setting_it_to_what_it_already_is_writes_nothing()
    {
        await SetEnabledAsync(true);

        Assert.Empty(await SettingsAsync());
    }

    /// <summary>A body that names no setting is refused rather than read as "off".</summary>
    [Fact]
    public async Task A_body_that_says_nothing_is_refused()
    {
        await using var scope = _provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IAiLowCreditNoticeFacade>()
            .SetEnabledAsync(Account, new LowCreditNoticeViewModel(null), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiLowCreditNoticeErrors.SettingInvalid, result.Error!.Code);
        Assert.Empty(await SettingsAsync());
    }

    /// <summary>
    /// The server keeps a dismissal through a change of setting: a creator who has read this period's notice
    /// has read it, and switching the notice off and on again is not a request to be told twice.
    /// </summary>
    [Fact]
    public async Task Turning_the_notice_off_and_on_again_does_not_undismiss_it()
    {
        await SeedPeriodAsync(consumed: 950m);
        await AdmitAsync();
        await DismissAsync();

        await SetEnabledAsync(false);
        await SetEnabledAsync(true);

        var low = (await CurrentAsync()).LowCredit;

        Assert.True(low.IsNoticed);
        Assert.True(low.IsDismissed);
        Assert.True(low.NoticeEnabled);
    }

    // ---- between accounts ------------------------------------------------------------------------------

    /// <summary>
    /// Two accounts, and nothing of one reaches the other: not the mark, not the dismissal, not the setting.
    /// The account is a parameter from a validated session and never anything a request carries (USAGE-008).
    /// </summary>
    [Fact]
    public async Task One_accounts_notice_is_not_anothers()
    {
        await SeedPeriodAsync(consumed: 950m);
        await SeedPeriodAsync(consumed: 950m, accountId: OtherAccount);

        await AdmitAsync();
        await DismissAsync();
        await SetEnabledAsync(false);

        var other = await CurrentAsync(OtherAccount);

        Assert.False(other.LowCredit.IsNoticed);
        Assert.False(other.LowCredit.IsDismissed);
        Assert.True(other.LowCredit.NoticeEnabled);

        var theirs = (await AllPeriodsAsync()).Single(period => period.AccountId == OtherAccount);
        Assert.Null(theirs.LowCreditNoticedAt);
    }

    [Fact]
    public async Task One_accounts_dismissal_does_not_dismiss_anothers()
    {
        await SeedPeriodAsync(consumed: 950m);
        await SeedPeriodAsync(consumed: 950m, accountId: OtherAccount);

        await AdmitAsync();
        await MarkNoticedAsync(OtherAccount);

        await DismissAsync();

        // The positive control: without it a DismissAsync that did nothing at all would pass this test.
        Assert.NotNull(await PeriodAsync() is { } mine ? mine.LowCreditNoticeDismissedAt : null);

        Assert.Null((await AllPeriodsAsync())
            .Single(period => period.AccountId == OtherAccount).LowCreditNoticeDismissedAt);
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync(
        Guid? operationId = null,
        Guid? leaseToken = null,
        bool meterable = true,
        ServiceProvider? provider = null)
    {
        using var scope = (provider ?? _provider).CreateScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(WorkspaceA, "a", MembershipInA, WorkspaceRole.Owner, Account);

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitAsync(
            new AiQuotaAdmissionRequestServiceModel(
                Account,
                operationId ?? Guid.NewGuid(),
                leaseToken ?? Guid.NewGuid(),
                AiTaskType.RecipeConcepts,
                meterable,
                _clock.UtcNow + AiPolicy.LeaseDuration),
            TestContext.Current.CancellationToken);
    }

    private async Task<AiQuotaAdmissionServiceModel> AdmitImageAsync(int pictures, int? dailyImageCap = null)
    {
        using var scope = _provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(WorkspaceA, "a", MembershipInA, WorkspaceRole.Owner, Account);

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitImageAsync(
            new AiImageQuotaAdmissionRequestServiceModel(
                Account,
                Guid.NewGuid(),
                pictures,
                ImageModel,
                null,
                null,
                _clock.UtcNow + AiPolicy.LeaseDuration,
                dailyImageCap),
            TestContext.Current.CancellationToken);
    }

    private async Task<AccountAiUsageServiceModel> CurrentAsync(string? accountId = null)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiUsageReadFacade>()
            .GetCurrentAsync(accountId ?? Account, TestContext.Current.CancellationToken);
    }

    private async Task DismissAsync(string? accountId = null)
    {
        await using var scope = _provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiLowCreditNoticeFacade>()
            .DismissAsync(accountId ?? Account, TestContext.Current.CancellationToken);
    }

    private async Task SetEnabledAsync(bool enabled, string? accountId = null)
    {
        await using var scope = _provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IAiLowCreditNoticeFacade>()
            .SetEnabledAsync(
                accountId ?? Account,
                new LowCreditNoticeViewModel(enabled),
                TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    /// <summary>Marks another account's period directly: this file is about isolation, not about their work.</summary>
    private async Task MarkNoticedAsync(string accountId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var period = await db.AccountAiQuotaPeriods.SingleAsync(
            row => row.AccountId == accountId, TestContext.Current.CancellationToken);

        period.LowCreditNoticedAt = _clock.UtcNow;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedQuotaAsync(decimal allowance, AiQuotaUnit unit = AiQuotaUnit.Credits)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = Account,
            Unit = unit,
            Allowance = allowance,
            PeriodLength = AiQuotaPeriodLength.Monthly,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            EffectiveFrom = PeriodStart.AddDays(-30),
            LastChangedAt = PeriodStart.AddDays(-30),
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedPeriodAsync(
        decimal consumed,
        decimal? allowance = null,
        AiQuotaUnit unit = AiQuotaUnit.Credits,
        string accountId = Account,
        ServiceProvider? provider = null)
    {
        using var scope = (provider ?? _provider).CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotaPeriods.Add(new AccountAiQuotaPeriod
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            StartsAt = PeriodStart,
            EndsAt = PeriodEnd,
            LocalStartDate = DateOnly.FromDateTime(PeriodStart.UtcDateTime),
            TimeZoneId = "Etc/UTC",
            Unit = unit,
            Allowance = allowance ?? Allowance,
            Consumed = consumed,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A pack the account still holds, which admission spends after the allowance (AP.5.2).</summary>
    private async Task SeedPackAsync(decimal credits)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountCreditGrants.Add(new AccountCreditGrant
        {
            Id = Guid.NewGuid(),
            AccountId = Account,
            Source = CreditGrantSource.Purchase,
            ExternalReferenceId = $"cs_{Guid.NewGuid():N}",
            CreditsGranted = credits,
            CreditsRemaining = credits,
            GrantedAt = PeriodStart,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<AccountAiQuotaPeriod> PeriodAsync() =>
        (await AllPeriodsAsync()).Single(period => period.AccountId == Account);

    private async Task<List<AccountAiQuotaPeriod>> AllPeriodsAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaPeriods.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<AccountAiNoticeSetting>> SettingsAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiNoticeSettings.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private static ApplicationUser User(string id) =>
        new() { Id = id, UserName = $"{id}@example.test", Email = $"{id}@example.test", DisplayName = id };

    /// <summary>How many more marks are to throw. Shared across the scopes one provider hands out.</summary>
    private sealed class MarkFailures
    {
        public int Remaining { get; set; }
    }

    /// <summary>Everything the real data layer does, except that recording a notice throws.</summary>
    private sealed class MarkThrowsDataLayer(AiQuotaDataLayer inner, MarkFailures failures) : IAiQuotaDataLayer
    {
        public Task<AiQuotaWriteOutcome> MarkLowCreditNoticedAsync(
            Guid periodId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (failures.Remaining <= 0)
            {
                return inner.MarkLowCreditNoticedAsync(periodId, now, cancellationToken);
            }

            failures.Remaining--;

            throw new InvalidOperationException("the notice could not be written");
        }

        public Task<AccountAiQuota?> FindCurrentQuotaAsync(
            string accountId, CancellationToken cancellationToken) =>
            inner.FindCurrentQuotaAsync(accountId, cancellationToken);

        public Task<AccountAiQuotaReservation?> FindReservationByIdAsync(
            Guid reservationId, CancellationToken cancellationToken) =>
            inner.FindReservationByIdAsync(reservationId, cancellationToken);

        public Task<AccountAiQuotaReservation?> FindReservationAsync(
            Guid aiOperationId, Guid leaseToken, CancellationToken cancellationToken) =>
            inner.FindReservationAsync(aiOperationId, leaseToken, cancellationToken);

        public Task<AccountAiQuotaPeriod> ResolvePeriodAsync(
            string accountId,
            DateTimeOffset instant,
            Func<AccountAiQuotaPeriod?, decimal, CancellationToken, Task<AccountAiQuotaPeriod>> open,
            CancellationToken cancellationToken) =>
            inner.ResolvePeriodAsync(accountId, instant, open, cancellationToken);

        public Task<AccountAiQuotaPeriod> ProjectPeriodAsync(
            string accountId,
            DateTimeOffset instant,
            Func<AccountAiQuotaPeriod?, decimal, CancellationToken, Task<AccountAiQuotaPeriod>> open,
            CancellationToken cancellationToken) =>
            inner.ProjectPeriodAsync(accountId, instant, open, cancellationToken);

        public Task<(AiQuotaWriteOutcome Outcome, AccountAiQuotaReservation? Reservation, AccountAiQuotaPeriod? Period)>
            ReserveAsync(
                string accountId,
                DateTimeOffset instant,
                Func<AccountAiQuotaPeriod, int, CancellationToken, Task<AccountAiQuotaReservation?>> admit,
                PlatformImageSpendCharge? platform,
                DateTimeOffset? imageDayStart,
                CancellationToken cancellationToken) =>
            inner.ReserveAsync(accountId, instant, admit, platform, imageDayStart, cancellationToken);

        public Task<AccountAiQuotaPeriod?> RaisePeriodAsync(
            Guid periodId, Func<AccountAiQuotaPeriod, bool> raise, CancellationToken cancellationToken) =>
            inner.RaisePeriodAsync(periodId, raise, cancellationToken);

        public Task<bool> HasPeriodUnderPlanAsync(
            string accountId, string planKey, CancellationToken cancellationToken) =>
            inner.HasPeriodUnderPlanAsync(accountId, planKey, cancellationToken);

        public Task<AiQuotaWriteOutcome> ReleaseImageAsync(
            Guid reservationId,
            DateTimeOffset now,
            ResolveGrants resolveGrants,
            CancellationToken cancellationToken) =>
            inner.ReleaseImageAsync(reservationId, now, resolveGrants, cancellationToken);

        public Task<AiQuotaWriteOutcome> StageSettlementAsync(
            Guid reservationId,
            Func<AccountAiQuotaReservation, bool> settle,
            CancellationToken cancellationToken) =>
            inner.StageSettlementAsync(reservationId, settle, cancellationToken);

        public Task<AiQuotaWriteOutcome> PostAsync(
            Guid reservationId,
            DateTimeOffset now,
            ResolveGrants resolveGrants,
            CancellationToken cancellationToken) =>
            inner.PostAsync(reservationId, now, resolveGrants, cancellationToken);

        public Task<AiQuotaWriteOutcome> ExpireAsync(
            Guid reservationId,
            DateTimeOffset now,
            ResolveGrants resolveGrants,
            CancellationToken cancellationToken) =>
            inner.ExpireAsync(reservationId, now, resolveGrants, cancellationToken);

        public Task<AiQuotaWriteOutcome> RenewAsync(
            Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
            inner.RenewAsync(reservationId, expiresAt, cancellationToken);

        public Task<AiQuotaWriteOutcome> FinalizePeriodAsync(
            Guid periodId, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FinalizePeriodAsync(periodId, now, cancellationToken);

        public Task<IReadOnlyList<Guid>> FindLapsedReservationIdsAsync(
            DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FindLapsedReservationIdsAsync(now, cancellationToken);

        public Task<IReadOnlyList<Guid>> FindUnpostedReservationIdsAsync(CancellationToken cancellationToken) =>
            inner.FindUnpostedReservationIdsAsync(cancellationToken);

        public Task<IReadOnlyList<Guid>> FindFinalizablePeriodIdsAsync(
            DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.FindFinalizablePeriodIdsAsync(now, cancellationToken);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
