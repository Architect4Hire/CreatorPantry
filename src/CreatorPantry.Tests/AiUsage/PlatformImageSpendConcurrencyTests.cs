using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The platform's daily image spend ceiling holds across instances (PRICE-005, AP.2.7): two hosts admitting at
/// the same instant cannot both pass it, and the counter never disagrees with the holds it admitted.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two service providers over one database are two instances.</strong> Each has its own container, its
/// own DbContexts and its own connection pool, so nothing in memory is shared between them — the only thing the
/// two have in common is the SQL row, which is the claim under test. One of them runs with a retrying execution
/// strategy, as every Aspire-hosted context does, so the explicit transaction the charge runs in is proved under
/// the strategy that would refuse one it did not own.
/// </para>
/// <para>
/// <strong>SQL Server, not SQLite.</strong> SQLite serialises every writer on the file and cannot take
/// concurrent requests through a host at all, so a race there proves nothing about a race here.
/// <c>PlatformImageSpendAdmissionTests</c> covers the single-writer behaviour on SQLite, which is what the
/// hosted suites run on.
/// </para>
/// </remarks>
public sealed class PlatformImageSpendConcurrencyTests : IAsyncLifetime
{
    private const string Model = "test-image";

    /// <summary>Fifty credits a picture, against an allowance nobody here runs out of unless a test says so.</summary>
    private const decimal Rate = 50m;

    /// <summary>Seven cents a picture at list price: 70,000 micro-dollars.</summary>
    private const decimal ListPrice = 0.07m;

    private const long PictureMicros = 70_000;

    private readonly SqlServerTestDatabase _container = new();

    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

    private readonly List<ServiceProvider> _instances = [];

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var setup = Instance(ceiling: 1m, retrying: false);
        await using var scope = setup.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.MigrateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _instances)
        {
            await instance.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The prompt's test: room for one picture under the ceiling, one picture asked for on each of two
    /// instances at once — exactly one is admitted, every time.
    /// </summary>
    /// <remarks>
    /// Twenty rounds, each on a fresh UTC day so each starts from an empty counter. A race lost once in twenty
    /// is the bug this exists to catch, and one round could pass by luck of scheduling.
    /// </remarks>
    [Fact]
    public async Task Two_instances_cannot_both_pass_the_ceiling()
    {
        var first = Instance(ceiling: 0.10m, retrying: false);
        var second = Instance(ceiling: 0.10m, retrying: true);

        for (var round = 0; round < 20; round++)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var racing = new[]
            {
                AdmitAsync(first, $"account-a-{round}", gate.Task),
                AdmitAsync(second, $"account-b-{round}", gate.Task),
            };

            gate.SetResult();
            var outcomes = await Task.WhenAll(racing);

            Assert.Single(outcomes, outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted);
            Assert.Single(outcomes, outcome => outcome.Outcome is AiQuotaAdmissionOutcome.PlatformCeiling);

            var day = await DayAsync(_clock.UtcNow);
            Assert.Equal(PictureMicros, day.AdmittedMicros);
            Assert.NotNull(day.TrippedAt);

            _clock.Advance(TimeSpan.FromDays(1));
        }
    }

    /// <summary>
    /// Twenty requests from two instances at once against room for exactly ten: ten are admitted, the counter
    /// stops on the ceiling to the micro-dollar, and every refusal between them raises one trip.
    /// </summary>
    [Fact]
    public async Task A_burst_from_two_instances_stops_exactly_at_the_ceiling_and_trips_once()
    {
        var first = Instance(ceiling: 0.70m, retrying: false);
        var second = Instance(ceiling: 0.70m, retrying: true);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var racing = Enumerable.Range(0, 20)
            .Select(index => AdmitAsync(index % 2 == 0 ? first : second, $"account-{index}", gate.Task))
            .ToArray();

        gate.SetResult();
        var outcomes = await Task.WhenAll(racing);

        Assert.Equal(10, outcomes.Count(outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted));
        Assert.Equal(10, outcomes.Count(outcome => outcome.Outcome is AiQuotaAdmissionOutcome.PlatformCeiling));

        var day = await DayAsync(_clock.UtcNow);
        Assert.Equal(10 * PictureMicros, day.AdmittedMicros);
        Assert.Equal(day.AdmittedMicros, await CountedByHoldsAsync());

        // However many refusals raced for it, one of them tripped the day and wrote its audit row.
        Assert.Equal(1, await TripRowsAsync());
    }

    /// <summary>
    /// <strong>The counter and the holds cannot disagree</strong>, even when one account's own period is what
    /// the requests are fighting over. Six requests for one account from two instances, room in its allowance
    /// for four: an attempt that loses the period's row version rolls back with the platform charge it had
    /// already taken, so what the day counted is exactly what the holds say it admitted.
    /// </summary>
    [Fact]
    public async Task A_charge_rolled_back_with_a_lost_period_is_never_counted()
    {
        var first = Instance(ceiling: 10m, retrying: false, allowance: Rate * 4);
        var second = Instance(ceiling: 10m, retrying: true, allowance: Rate * 4);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var racing = Enumerable.Range(0, 6)
            .Select(index => AdmitAsync(index % 2 == 0 ? first : second, "one-account", gate.Task))
            .ToArray();

        gate.SetResult();
        var outcomes = await Task.WhenAll(racing);
        var admitted = outcomes.Count(outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted);

        Assert.InRange(admitted, 1, 4);
        Assert.DoesNotContain(outcomes, outcome => outcome.Outcome is AiQuotaAdmissionOutcome.PlatformCeiling);

        var day = await DayAsync(_clock.UtcNow);
        Assert.Equal(admitted * PictureMicros, day.AdmittedMicros);
        Assert.Equal(day.AdmittedMicros, await CountedByHoldsAsync());
    }

    /// <summary>
    /// Two operators lifting the same day at the same instant, on two instances: the guarded write lets one
    /// win, and the day carries one lift and one audit row between them.
    /// </summary>
    [Fact]
    public async Task Two_lifts_at_once_lift_the_day_once_and_audit_it_once()
    {
        var first = Instance(ceiling: 0.10m, retrying: false);
        var second = Instance(ceiling: 0.10m, retrying: true);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var today = PlatformImageSpendPolicy.DayOf(_clock.UtcNow);

        var racing = new[] { LiftAsync(first, "desk-a", gate.Task), LiftAsync(second, "desk-b", gate.Task) };

        gate.SetResult();
        var won = await Task.WhenAll(racing);

        Assert.Single(won, lifted => lifted);
        Assert.NotNull((await DayAsync(_clock.UtcNow)).LiftedAt);

        await using var scope = _instances[0].CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PlatformAuditLogs.AsNoTracking()
            .CountAsync(log => log.Action == PlatformImageSpendAuditActions.Lifted, Ct));

        async Task<bool> LiftAsync(ServiceProvider instance, string desk, Task start)
        {
            await start;
            await using var lifting = instance.CreateAsyncScope();

            return await lifting.ServiceProvider.GetRequiredService<IPlatformImageSpendDataLayer>().LiftAsync(
                today,
                _clock.UtcNow,
                desk,
                new PlatformAuditEntry(
                    PlatformAuditActorType.OpsClient, desk, desk, PlatformImageSpendAuditActions.Lifted,
                    PlatformImageSpendAuditActions.SubjectType, today.ToString("yyyy-MM-dd"), Guid.NewGuid(),
                    "Launch day."),
                Ct);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private ServiceProvider Instance(decimal ceiling, bool retrying, decimal allowance = 1_000_000m)
    {
        var instance = new ServiceCollection()
            .AddLogging()
            .AddApplicationTime()
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultAllowance = allowance;
                options.ImageRates[Model] = Rate;
                options.PlatformImageSpendPerDay = ceiling;
            })
            .Configure<AiImageListPriceOptions>(options => options.Images[Model] = ListPrice)
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(
                _container.GetConnectionString(),
                sql =>
                {
                    if (retrying)
                    {
                        sql.EnableRetryOnFailure();
                    }
                }))
            .BuildServiceProvider(validateScopes: true);

        _instances.Add(instance);

        return instance;
    }

    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync(
        ServiceProvider instance, string account, Task gate)
    {
        await gate;
        await using var scope = instance.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitImageAsync(
            new AiImageQuotaAdmissionRequestServiceModel(
                account, Guid.NewGuid(), 1, Model, null, null, _clock.UtcNow + TimeSpan.FromHours(6)),
            Ct);
    }

    private async Task<PlatformImageSpendDay> DayAsync(DateTimeOffset instant)
    {
        await using var scope = _instances[0].CreateAsyncScope();
        var day = PlatformImageSpendPolicy.DayOf(instant);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PlatformImageSpendDays.AsNoTracking().SingleAsync(row => row.Day == day, Ct);
    }

    private async Task<long> CountedByHoldsAsync()
    {
        await using var scope = _instances[0].CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations.AsNoTracking()
            .SumAsync(reservation => reservation.PlatformImageSpendMicros ?? 0, Ct);
    }

    private async Task<int> TripRowsAsync()
    {
        await using var scope = _instances[0].CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PlatformAuditLogs.AsNoTracking()
            .CountAsync(log => log.Action == PlatformImageSpendAuditActions.Tripped, Ct);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
