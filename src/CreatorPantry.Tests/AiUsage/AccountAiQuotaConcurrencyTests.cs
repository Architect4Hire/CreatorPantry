using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The one property of quota admission that only a real database can demonstrate: two workers claiming work
/// for the same account cannot both be admitted past the last of the allowance (USAGE-004).
/// </summary>
/// <remarks>
/// <para>
/// <strong>SQL Server rather than SQLite, and not as a preference.</strong> The SQLite test model gives a row
/// version a <c>randomblob(8)</c> default on insert and never regenerates it on update, so an optimistic
/// concurrency check there never fires — two concurrent admissions would both succeed whatever the code did,
/// and the test would pass while proving nothing. Everything else about this seam is covered in
/// <see cref="AccountAiQuotaAdmissionTests"/>; this file exists for the races.
/// </para>
/// <para>
/// <c>Migrate()</c> rather than <c>EnsureCreated()</c>, for the reason <c>SqlServerRecipeFixture</c> gives: the
/// schema under test is the one a deployment actually applies, so a migration that disagrees with the model is
/// visible here rather than in production.
/// </para>
/// </remarks>
public sealed class AccountAiQuotaConcurrencyTests : IAsyncLifetime
{
    private const string Account = "user-sam";

    private const decimal PerCall = 10m;

    private const decimal Hold = 20m;

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-22.04").Build();

    private ServiceProvider? _provider;

    private readonly StoppedClock _clock = new();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = new ServiceCollection()
            .AddApplicationTime()
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultTaskEstimate = PerCall;
                options.EstimatedCallsPerRun = 2;

                // One credit an input token, two an output token -- so a settled charge is readable at a
                // glance and, more usefully here, is not the estimate. A test whose measured charge happened
                // to equal the estimate could not tell a settlement from a fallback.
                options.ModelRates["test-model"] = new AiQuotaModelRate(1_000_000m, 2_000_000m);
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
    /// Five workers, an allowance that covers three of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertion is an upper bound rather than an exact count, and deliberately. A worker that loses
    /// <c>AiQuotaPolicy.ContendedWriteAttempts</c> races in a row is told the period is busy and requeues —
    /// which is correct behaviour, not a failure, so pinning the count at three would make this test fail on a
    /// slow machine for the one reason the code is right.
    /// </para>
    /// <para>
    /// What is asserted exactly is the property USAGE-004 actually asks for: nobody is admitted past the last
    /// of the allowance, and the holds that were written are precisely the ones the period says it is holding.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Competing_workers_cannot_both_be_admitted_past_the_last_of_the_allowance()
    {
        await GiveQuotaAsync(allowance: PerCall * 6);

        var admissions = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => AdmitAsync()));

        var admitted = admissions.Count(outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted);

        Assert.InRange(admitted, 1, 3);
        Assert.DoesNotContain(
            admissions,
            outcome => outcome.Outcome is not (AiQuotaAdmissionOutcome.Admitted
                or AiQuotaAdmissionOutcome.Exhausted
                or AiQuotaAdmissionOutcome.Busy));

        var period = await PeriodAsync();
        var outstanding = (await ReservationsAsync())
            .Where(reservation => reservation.ReleasedAt is null)
            .Sum(reservation => reservation.ReservedAmount);

        // The invariant AccountAiQuotaReservation documents: Reserved is the sum of what is outstanding.
        Assert.Equal(outstanding, period.Reserved);
        Assert.Equal(Hold * admitted, period.Reserved);
        Assert.True(
            period.Reserved <= period.Allowance + period.CarriedOver,
            $"{period.Reserved} was held against an allowance of {period.Allowance}.");
    }

    /// <summary>
    /// Opening a period is idempotent under concurrency: several workers finding no period all try to open
    /// one, the unique index on <c>(AccountId, StartsAt)</c> decides, and the losers read the winner's row
    /// instead of splitting the allowance across two.
    /// </summary>
    [Fact]
    public async Task Competing_workers_open_one_period_between_them()
    {
        var admissions = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => AdmitAsync()));
        var admitted = admissions.Count(outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted);

        var period = Assert.Single(await AllPeriodsAsync());

        Assert.Equal(Hold * admitted, period.Reserved);
    }

    /// <summary>
    /// A settlement and the expiry sweep racing for the same hold: the estimate is given back exactly once,
    /// whichever of them reaches the period first. Both are guarded by the same stored timestamp, so the loser
    /// re-reads and declines rather than double-crediting the balance.
    /// </summary>
    [Fact]
    public async Task Posting_and_expiry_racing_for_one_hold_release_it_once()
    {
        var admission = await AdmitAsync();
        var reservationId = admission.ReservationId!.Value;

        await StageSettlementAsync(reservationId, Attempt(inputTokens: 4));

        _clock.Advance(AiPolicy.LeaseDuration + TimeSpan.FromMinutes(1));

        await Task.WhenAll(PostAsync(reservationId), SweepAsync());

        var period = await PeriodAsync();

        Assert.Equal(0m, period.Reserved);
        Assert.Equal(4m, period.Consumed);

        var reservation = (await ReservationsAsync()).Single();
        Assert.Equal(AiQuotaReservationStatus.Settled, reservation.Status);
        Assert.NotNull(reservation.ReleasedAt);
        Assert.NotNull(reservation.PostedAt);
    }

    /// <summary>
    /// Several posts of one settlement, at once. The charge lands once — which is what makes the worker and
    /// the sweep both reaching for it safe rather than merely unlikely to collide.
    /// </summary>
    [Fact]
    public async Task Concurrent_posts_of_one_settlement_charge_once()
    {
        var admission = await AdmitAsync();
        var reservationId = admission.ReservationId!.Value;

        await StageSettlementAsync(reservationId, Attempt(inputTokens: 7));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostAsync(reservationId)));

        var period = await PeriodAsync();

        Assert.Equal(0m, period.Reserved);
        Assert.Equal(7m, period.Consumed);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitAsync(
            new AiQuotaAdmissionRequestServiceModel(
                Account,
                Guid.NewGuid(),
                Guid.NewGuid(),
                AiTaskType.RecipeConcepts,
                true,
                _clock.UtcNow + AiPolicy.LeaseDuration),
            TestContext.Current.CancellationToken);
    }

    private async Task StageSettlementAsync(Guid reservationId, AiUsageAttemptServiceModel attempt)
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
            .StageSettlementAsync(reservationId, [attempt], TestContext.Current.CancellationToken);

        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task PostAsync(Guid reservationId)
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
            .PostAsync(reservationId, TestContext.Current.CancellationToken);
    }

    private async Task SweepAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaMaintenanceFacade>()
            .SweepAsync(TestContext.Current.CancellationToken);
    }

    private async Task GiveQuotaAsync(decimal allowance)
    {
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = Account,
            Unit = AiQuotaUnit.Credits,
            Allowance = allowance,
            PeriodLength = AiQuotaPeriodLength.Monthly,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            EffectiveFrom = _clock.UtcNow.AddDays(-30),
            LastChangedAt = _clock.UtcNow.AddDays(-30),
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static AiUsageAttemptServiceModel Attempt(int inputTokens) => new()
    {
        AttemptNumber = 1,
        OccurredAt = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
        ProviderName = "test-provider",
        ModelName = "test-model",
        InputTokens = inputTokens,
        Outcome = AiUsageOutcome.Succeeded,
        IsBillable = true,
    };

    private async Task<AccountAiQuotaPeriod> PeriodAsync() => (await AllPeriodsAsync()).Single();

    private async Task<List<AccountAiQuotaPeriod>> AllPeriodsAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaPeriods.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<AccountAiQuotaReservation>> ReservationsAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } =
            new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
