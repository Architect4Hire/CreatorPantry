using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The one property of the low-credit notice that only a real database can demonstrate: however many
/// admissions cross the line at the same instant, the account is told once (B-59).
/// </summary>
/// <remarks>
/// SQL Server rather than SQLite, for the reason <see cref="AccountAiQuotaConcurrencyTests"/> gives: the
/// SQLite test model never regenerates a row version, so the mark's losing attempt would never happen and
/// this file would pass while proving nothing. Everything else about the notice is in
/// <see cref="AiLowCreditNoticeTests"/>.
/// </remarks>
public sealed class AiLowCreditNoticeConcurrencyTests : IAsyncLifetime
{
    private const string Account = "user-sam";
    private const string OtherAccount = "user-alex";

    private const decimal PerCall = 10m;

    /// <summary>Ten credits a call, two calls a run.</summary>
    private const decimal Hold = 20m;

    private const decimal Allowance = 1000m;

    /// <summary>
    /// Already spent to within 110 credits of the allowance, where the line is 100: every one of the five
    /// concurrent admissions below leaves the account under it, so every one of them tries to raise the
    /// notice. Seeded rather than spent, so the race is the only thing the test is measuring.
    /// </summary>
    private const decimal NearlySpent = 890m;

    private static readonly DateTimeOffset PeriodStart = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqlServerTestDatabase _container = new();

    private ServiceProvider? _provider;

    private readonly StoppedClock _clock = new();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = new ServiceCollection()
            .AddApplicationTime()
            .AddBillingModule()
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultTaskEstimate = PerCall;
                options.EstimatedCallsPerRun = 2;
            })
            .AddDbContext<CreatorPantryDbContext>(options =>
                options.UseSqlServer(_container.GetConnectionString()))
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

    /// <summary>
    /// Five admissions at once against an allowance that leaves every one of them under the line. Each marks
    /// the period it has just written to; the row version decides, and the losers re-read, find the column
    /// set and decline.
    /// </summary>
    [Fact]
    public async Task Concurrent_admissions_crossing_the_line_raise_one_notice()
    {
        await GiveQuotaAsync();
        await SeedPeriodAsync(NearlySpent);

        var admissions = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => AdmitAsync(Account)));

        Assert.Contains(admissions, outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted);

        var period = Assert.Single(await PeriodsAsync(Account));

        // One notice, and it is really there: the mark is not merely "not written twice".
        Assert.NotNull(period.LowCreditNoticedAt);
        Assert.Equal(_clock.UtcNow, period.LowCreditNoticedAt);
    }

    /// <summary>
    /// The notice never costs an admission. Whatever the marks did to each other, the holds that were written
    /// are exactly the ones the period says it is holding.
    /// </summary>
    [Fact]
    public async Task The_notice_does_not_disturb_what_the_period_is_holding()
    {
        await GiveQuotaAsync();
        await SeedPeriodAsync(NearlySpent);

        var admissions = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => AdmitAsync(Account)));
        var admitted = admissions.Count(outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted);

        var period = Assert.Single(await PeriodsAsync(Account));

        Assert.Equal(Hold * admitted, period.Reserved);
        Assert.True(
            period.Reserved <= period.Allowance + period.CarriedOver,
            $"{period.Reserved} was held against an allowance of {period.Allowance}.");
    }

    /// <summary>
    /// Two accounts running low at the same moment are two notices on two rows, and neither suppresses the
    /// other: the mark is per period, and a period belongs to one account.
    /// </summary>
    [Fact]
    public async Task Two_accounts_crossing_the_line_together_are_each_told()
    {
        await GiveQuotaAsync();
        await GiveQuotaAsync(OtherAccount);
        await SeedPeriodAsync(NearlySpent);
        await SeedPeriodAsync(NearlySpent, OtherAccount);

        await Task.WhenAll(
            Enumerable.Range(0, 3).SelectMany(_ => new[] { AdmitAsync(Account), AdmitAsync(OtherAccount) }));

        Assert.NotNull(Assert.Single(await PeriodsAsync(Account)).LowCreditNoticedAt);
        Assert.NotNull(Assert.Single(await PeriodsAsync(OtherAccount)).LowCreditNoticedAt);
    }

    /// <summary>
    /// A dismissal racing the admissions it is about: it loses the row version to them, re-reads and lands,
    /// rather than being swallowed or taking a hold back with it.
    /// </summary>
    [Fact]
    public async Task A_dismissal_racing_admissions_still_lands()
    {
        await GiveQuotaAsync();
        await SeedPeriodAsync(NearlySpent);

        // One admission first, so there is a notice to dismiss at all.
        await AdmitAsync(Account);

        await Task.WhenAll(
            DismissAsync(),
            AdmitAsync(Account),
            AdmitAsync(Account),
            AdmitAsync(Account));

        Assert.NotNull(Assert.Single(await PeriodsAsync(Account)).LowCreditNoticeDismissedAt);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync(string accountId)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitAsync(
            new AiQuotaAdmissionRequestServiceModel(
                accountId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                AiTaskType.RecipeConcepts,
                true,
                _clock.UtcNow + AiPolicy.LeaseDuration),
            TestContext.Current.CancellationToken);
    }

    private async Task DismissAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiLowCreditNoticeFacade>()
            .DismissAsync(Account, TestContext.Current.CancellationToken);
    }

    private async Task GiveQuotaAsync(string accountId = Account)
    {
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Unit = AiQuotaUnit.Credits,
            Allowance = Allowance,
            PeriodLength = AiQuotaPeriodLength.Monthly,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            EffectiveFrom = _clock.UtcNow.AddDays(-30),
            LastChangedAt = _clock.UtcNow.AddDays(-30),
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedPeriodAsync(decimal consumed, string accountId = Account)
    {
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotaPeriods.Add(new AccountAiQuotaPeriod
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            StartsAt = PeriodStart,
            EndsAt = PeriodEnd,
            LocalStartDate = DateOnly.FromDateTime(PeriodStart.UtcDateTime),
            TimeZoneId = "Etc/UTC",
            Unit = AiQuotaUnit.Credits,
            Allowance = Allowance,
            Consumed = consumed,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<AccountAiQuotaPeriod>> PeriodsAsync(string accountId)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaPeriods.AsNoTracking()
            .Where(period => period.AccountId == accountId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    }
}
