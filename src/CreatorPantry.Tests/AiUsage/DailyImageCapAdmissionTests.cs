using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// A plan's daily picture cap, judged where the account's picture hold is taken (AP.4.2): per account, per UTC
/// day, all of a request or none of it, before the balance — and counting what was charged rather than what was
/// asked once a generation is finished.
/// </summary>
/// <remarks>
/// <para>
/// SQLite, one admission at a time. Two admissions racing for the last pictures of the day is
/// <see cref="DailyImageCapConcurrencyTests"/>, on SQL Server, where the period's row version is real.
/// </para>
/// <para>
/// The cap is passed in, as the Media module passes the plan's: which plan an account is on is Billing's and is
/// <c>PlanAllowanceTests</c>' and <c>GeneratedImagePlanLimitTests</c>' to show.
/// </para>
/// </remarks>
public sealed class DailyImageCapAdmissionTests : IDisposable
{
    private const string Sam = "user-sam";

    private const string Alex = "user-alex";

    private const string Model = "test-image";

    private const decimal Rate = 50m;

    private const int Cap = 5;

    private static readonly DateTimeOffset Noon = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private readonly MovableClock _clock = new(Noon);

    private ServiceProvider? _provider;

    public DailyImageCapAdmissionTests() => _connection.Open();

    public void Dispose()
    {
        _provider?.Dispose();
        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exactly_the_cap_is_admitted_and_one_more_is_refused_with_the_accounts_own_figures()
    {
        Start();

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 3)).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 2)).Outcome);

        var refusal = await AdmitAsync(Sam, 1);

        Assert.Equal(AiQuotaAdmissionOutcome.DailyImageCap, refusal.Outcome);
        Assert.Equal(Cap, refusal.DailyImageCap);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero), refusal.DailyImageCapResetsAt);

        // Nothing held and no balance reported: the balance is not why.
        Assert.Null(refusal.ReservationId);
        Assert.Equal(0m, refusal.Reserved);
        Assert.Equal(0m, refusal.Remaining);
        Assert.Null(refusal.PeriodEndsAt);
        Assert.Equal(2, await HoldCountAsync());
    }

    /// <summary>Four with three left is refused whole, rather than queued to be cut short; three is then admitted.</summary>
    [Fact]
    public async Task A_request_past_the_cap_is_refused_whole()
    {
        Start();
        await AdmitAsync(Sam, 2);

        Assert.Equal(AiQuotaAdmissionOutcome.DailyImageCap, (await AdmitAsync(Sam, 4)).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 3)).Outcome);
    }

    /// <summary>The cap is the account's; another account's pictures are not counted against it.</summary>
    [Fact]
    public async Task Two_accounts_do_not_share_a_cap()
    {
        Start();
        await AdmitAsync(Alex, Cap);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, Cap)).Outcome);
    }

    /// <summary>The day is UTC, as every daily limit's is: yesterday's pictures are not today's.</summary>
    [Fact]
    public async Task The_cap_resets_at_utc_midnight()
    {
        Start();
        await AdmitAsync(Sam, Cap);

        _clock.Set(new DateTimeOffset(2026, 10, 28, 0, 0, 1, TimeSpan.Zero));

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, Cap)).Outcome);
    }

    /// <summary>
    /// The cap tells the creator when to come back, which an empty balance does not, so it is the answer when both
    /// would refuse.
    /// </summary>
    [Fact]
    public async Task The_cap_is_judged_before_the_balance()
    {
        Start(allowance: 100m);
        await AdmitAsync(Sam, 2);

        Assert.Equal(AiQuotaAdmissionOutcome.DailyImageCap, (await AdmitAsync(Sam, 4)).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.Exhausted, (await AdmitAsync(Sam, 1)).Outcome);
    }

    /// <summary>With no plan there is no account cap, and only the balance decides.</summary>
    [Fact]
    public async Task With_no_cap_a_request_is_not_counted_against_a_day()
    {
        Start();
        await AdmitAsync(Sam, 4, cap: null);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 4, cap: null)).Outcome);
    }

    /// <summary>
    /// Pictures asked for without a cap still count once one applies, so a plan that arrives during the day sees
    /// what was already asked for.
    /// </summary>
    [Fact]
    public async Task Pictures_asked_for_without_a_cap_still_count_against_one()
    {
        Start();
        await AdmitAsync(Sam, 4, cap: null);

        Assert.Equal(AiQuotaAdmissionOutcome.DailyImageCap, (await AdmitAsync(Sam, 2)).Outcome);
    }

    /// <summary>
    /// Once a generation is finished, what was charged counts and nothing else: a partial run of two of four keeps
    /// two, a run nothing was billed for keeps none, and a hold given back keeps none.
    /// </summary>
    [Fact]
    public async Task A_finished_generation_counts_what_it_was_charged()
    {
        Start();

        var partial = await AdmitAsync(Sam, 4);
        await SettleAsync(partial.ReservationId!.Value, staged: 1, unusable: 1);

        // Two charged, so three are left of five.
        Assert.Equal(AiQuotaAdmissionOutcome.DailyImageCap, (await AdmitAsync(Sam, 4)).Outcome);

        var nothing = await AdmitAsync(Sam, 3);
        await SettleAsync(nothing.ReservationId!.Value, staged: 0, unusable: 0);

        var released = await AdmitAsync(Sam, 3);
        await ReleaseAsync(released.ReservationId!.Value);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 3)).Outcome);

        var holds = await HoldsAsync();
        Assert.Equal(4, holds.Single(hold => hold.Id == partial.ReservationId).ImagesRequested);
        Assert.Equal(2, holds.Single(hold => hold.Id == partial.ReservationId).ImagesBilled);
        Assert.Equal(0, holds.Single(hold => hold.Id == nothing.ReservationId).ImagesBilled);
    }

    /// <summary>
    /// A variant a crashed pass had billed twice is one picture the creator asked for, so the day counts it
    /// once <em>and</em> the charge stops at the hold (owner, 2026-10-30, DL.11).
    /// </summary>
    /// <remarks>
    /// Three billed against a request for two: a variant billed but never staged is generated again on resume,
    /// and the second go is the platform's, not a picture the creator chose to spend on. The day's count was
    /// capped here from the start; the charge was not, so a resumed pass could spend past its own hold — which
    /// is what DL.5 forbids.
    /// </remarks>
    [Fact]
    public async Task A_variant_billed_twice_counts_once_against_the_day_and_is_charged_once()
    {
        Start();

        var twice = await AdmitAsync(Sam, 2);
        await SettleAsync(twice.ReservationId!.Value, staged: 2, unusable: 1);

        var settled = (await HoldsAsync()).Single(hold => hold.Id == twice.ReservationId);

        Assert.Equal(2, settled.ImagesBilled);

        // Two pictures at the per-picture rate, never the three the provider was paid for.
        Assert.Equal(2 * Rate, settled.SettledAmount);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, 3)).Outcome);
    }

    /// <summary>A hold whose lease lapsed with nothing settled was never charged, and stops counting.</summary>
    [Fact]
    public async Task An_expired_hold_stops_counting()
    {
        Start();
        await AdmitAsync(Sam, Cap, expiresIn: TimeSpan.FromMinutes(5));

        _clock.Set(Noon.AddMinutes(10));
        await SweepAsync();

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam, Cap)).Outcome);
    }

    /// <summary>A picture hold records how many it asked for, and nothing billed until it settles.</summary>
    [Fact]
    public async Task A_picture_hold_records_what_it_asked_for_until_it_settles()
    {
        Start();
        var picture = await AdmitAsync(Sam, 2);

        var holds = await HoldsAsync();
        Assert.Equal(2, holds.Single(hold => hold.Id == picture.ReservationId).ImagesRequested);
        Assert.Null(holds.Single(hold => hold.Id == picture.ReservationId).ImagesBilled);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private void Start(decimal allowance = 1_000_000m)
    {
        _provider = new ServiceCollection()
            .AddApplicationTime()
            .AddBillingModule()
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultAllowance = allowance;
                options.ImageRates[Model] = Rate;
            })
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreated();
    }

    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync(
        string accountId, int pictures, int? cap = Cap, TimeSpan? expiresIn = null)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitImageAsync(
            new AiImageQuotaAdmissionRequestServiceModel(
                accountId,
                Guid.NewGuid(),
                pictures,
                Model,
                null,
                null,
                _clock.UtcNow + (expiresIn ?? TimeSpan.FromDays(2)),
                cap),
            Ct);
    }

    /// <summary>Through the facade a finished generation calls, so the counts are the ones settlement writes.</summary>
    private async Task SettleAsync(Guid reservationId, int staged, int unusable)
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().StageImageUsageAsync(
            new AiImageUsageServiceModel(
                reservationId, Guid.NewGuid(), Guid.NewGuid(), staged, unusable, 0, _clock.UtcNow, "venice", Model, null),
            Ct);

        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().SaveChangesAsync(Ct);
    }

    private async Task ReleaseAsync(Guid reservationId)
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().ReleaseImageAsync(reservationId, Ct);
    }

    private async Task SweepAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaMaintenanceFacade>().SweepAsync(Ct);
    }

    private async Task<List<AccountAiQuotaReservation>> HoldsAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AccountAiQuotaReservations
            .AsNoTracking()
            .ToListAsync(Ct);
    }

    private async Task<int> HoldCountAsync() => (await HoldsAsync()).Count;

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Set(DateTimeOffset instant) => UtcNow = instant;
    }
}
