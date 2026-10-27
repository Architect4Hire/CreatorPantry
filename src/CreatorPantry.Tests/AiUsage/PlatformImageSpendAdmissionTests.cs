using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The platform's daily image spend breaker, one admission at a time (PRICE-005, AP.2.7): what is counted, what
/// is refused, when it trips, what a lift does, and the one give-back the counter allows.
/// </summary>
/// <remarks>
/// <para>
/// SQLite, deliberately: the hosted suites run on it, and the counter is written with conditional updates whose
/// translation has to hold there as well as on SQL Server. The races are
/// <see cref="PlatformImageSpendConcurrencyTests"/>, on SQL Server.
/// </para>
/// <para>
/// Seven cents a picture at list price and fifty credits, so the counter reads in round micro-dollars and the
/// account's allowance is never what refuses anything unless a test says so.
/// </para>
/// </remarks>
public sealed class PlatformImageSpendAdmissionTests : IDisposable
{
    private const string Model = "test-image";

    private const decimal Rate = 50m;

    private const decimal ListPrice = 0.07m;

    private const long PictureMicros = 70_000;

    private static readonly DateTimeOffset Noon = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    private readonly MovableClock _clock = new(Noon);

    private ServiceProvider? _provider;

    public PlatformImageSpendAdmissionTests() => _connection.Open();

    public void Dispose()
    {
        _provider?.Dispose();
        _connection.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- counting ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_admitted_request_counts_its_list_price_against_the_day()
    {
        Start(ceiling: 1m);

        var admission = await AdmitAsync(pictures: 2);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, admission.Outcome);
        Assert.Equal(2 * PictureMicros, (await DayAsync(Noon))!.AdmittedMicros);

        // The hold records what it added, so a give-back returns exactly that.
        Assert.Equal(2 * PictureMicros, (await ReservationAsync(admission.ReservationId!.Value)).PlatformImageSpendMicros);
    }

    /// <summary>At the ceiling to the micro-dollar is under it: a ceiling is the most that may be admitted.</summary>
    [Fact]
    public async Task A_request_that_lands_exactly_on_the_ceiling_is_admitted()
    {
        Start(ceiling: 0.14m);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(pictures: 2)).Outcome);
        Assert.Equal(2 * PictureMicros, (await DayAsync(Noon))!.AdmittedMicros);
    }

    /// <summary>With no ceiling configured nothing is counted, and no row is written for a counter nobody reads.</summary>
    [Fact]
    public async Task With_no_ceiling_a_picture_is_admitted_and_nothing_is_counted()
    {
        Start(ceiling: null);

        var admission = await AdmitAsync(pictures: 3);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, admission.Outcome);
        Assert.Null(await DayAsync(Noon));
        Assert.Null((await ReservationAsync(admission.ReservationId!.Value)).PlatformImageSpendMicros);
    }

    // ---- refusing --------------------------------------------------------------------------------------

    /// <summary>
    /// <strong>Past the ceiling, nothing is written for the request at all</strong>: no hold against the
    /// account, no charge to the day — and the refusal carries no figure, because none of them is the account's.
    /// </summary>
    [Fact]
    public async Task A_request_that_would_pass_the_ceiling_is_refused_and_holds_nothing()
    {
        Start(ceiling: 0.10m);

        var refusal = await AdmitAsync(pictures: 2);

        Assert.Equal(AiQuotaAdmissionOutcome.PlatformCeiling, refusal.Outcome);
        Assert.Null(refusal.ReservationId);
        Assert.Equal(0m, refusal.Reserved);
        Assert.Equal(0m, refusal.Remaining);
        Assert.Null(refusal.PeriodEndsAt);

        Assert.Equal(0, (await DayAsync(Noon))!.AdmittedMicros);
        Assert.Empty(await ReservationsAsync());
        Assert.Equal(0m, (await PeriodAsync()).Reserved);
    }

    /// <summary>
    /// The account's own allowance is judged first. An account that cannot afford the request is refused for
    /// that, and never counts toward — or trips — everybody else's ceiling.
    /// </summary>
    [Fact]
    public async Task An_account_that_cannot_afford_it_is_refused_for_its_own_allowance_and_counts_nothing()
    {
        Start(ceiling: 0.10m, allowance: Rate);

        var refusal = await AdmitAsync(pictures: 2);

        Assert.Equal(AiQuotaAdmissionOutcome.Exhausted, refusal.Outcome);

        var day = await DayAsync(Noon);
        Assert.Equal(0, day?.AdmittedMicros ?? 0);
        Assert.Null(day?.TrippedAt);
        Assert.Empty(await AuditAsync());
    }

    /// <summary>
    /// A picture this host has a credit rate for and no list price cannot be counted, so it is refused as
    /// unpriceable rather than let past the breaker uncounted. Startup refuses such a host; this is the guard
    /// for a table changed after it started.
    /// </summary>
    [Fact]
    public async Task A_picture_with_no_list_price_is_refused_rather_than_let_through_uncounted()
    {
        Start(ceiling: 1m, listPrice: false);

        Assert.Equal(AiQuotaAdmissionOutcome.Unrated, (await AdmitAsync(pictures: 1)).Outcome);
        Assert.Empty(await ReservationsAsync());
    }

    // ---- tripping --------------------------------------------------------------------------------------

    /// <summary>
    /// The first refusal trips the day and writes one platform audit row in the platform's own name; the
    /// second refusal is refused just the same and writes nothing more.
    /// </summary>
    [Fact]
    public async Task The_first_refusal_trips_the_day_once_and_audits_it_as_the_platform()
    {
        Start(ceiling: 0.10m);

        await AdmitAsync(pictures: 1);
        Assert.Equal(AiQuotaAdmissionOutcome.PlatformCeiling, (await AdmitAsync(pictures: 1)).Outcome);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(AiQuotaAdmissionOutcome.PlatformCeiling, (await AdmitAsync(pictures: 1)).Outcome);

        Assert.Equal(Noon, (await DayAsync(Noon))!.TrippedAt);

        var trip = Assert.Single(await AuditAsync());
        Assert.Equal(PlatformAuditActorType.System, trip.ActorType);
        Assert.Equal(PlatformImageSpendPolicy.Breaker.Id, trip.ActorId);
        Assert.Equal(PlatformImageSpendAuditActions.Tripped, trip.Action);
        Assert.Equal(PlatformImageSpendAuditActions.SubjectType, trip.SubjectType);
        Assert.Equal("2026-10-27", trip.SubjectId);
        Assert.False(string.IsNullOrWhiteSpace(trip.Reason));
        Assert.Equal("state=Armed;admitted=0.07;ceiling=0.10", trip.BeforeReference);
        Assert.Equal("state=Tripped;admitted=0.07;ceiling=0.10", trip.AfterReference);

        // Figures and a state, and nothing about whose pictures they were.
        Assert.DoesNotContain("account", trip.AfterReference!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <strong>A breaker, not a cap.</strong> Once the day has tripped, a smaller request that would still fit
    /// under the line is refused too — which is what the alert tells an operator and what lifting it undoes.
    /// </summary>
    [Fact]
    public async Task A_tripped_day_refuses_even_a_request_that_would_still_fit()
    {
        Start(ceiling: 0.25m);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(pictures: 2)).Outcome);

        // 0.14 + 0.14 passes 0.25, and trips the day.
        Assert.Equal(AiQuotaAdmissionOutcome.PlatformCeiling, (await AdmitAsync(pictures: 2)).Outcome);

        // 0.14 + 0.07 would fit — and is refused, because the breaker is open.
        Assert.Equal(AiQuotaAdmissionOutcome.PlatformCeiling, (await AdmitAsync(pictures: 1)).Outcome);
        Assert.Equal(2 * PictureMicros, (await DayAsync(Noon))!.AdmittedMicros);
        Assert.Single(await AuditAsync());

        await LiftAsync();

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(pictures: 1)).Outcome);
    }

    /// <summary>The next UTC day starts armed with nothing counted, whatever the day before did.</summary>
    [Fact]
    public async Task The_next_utc_day_starts_armed_with_an_empty_counter()
    {
        Start(ceiling: 0.10m);

        await AdmitAsync(pictures: 1);
        Assert.Equal(AiQuotaAdmissionOutcome.PlatformCeiling, (await AdmitAsync(pictures: 1)).Outcome);

        _clock.Advance(TimeSpan.FromHours(12));

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(pictures: 1)).Outcome);
        Assert.Equal(PictureMicros, (await DayAsync(_clock.UtcNow))!.AdmittedMicros);
        Assert.Null((await DayAsync(_clock.UtcNow))!.TrippedAt);
    }

    /// <summary>A lifted day admits past the ceiling and keeps counting, so the read still says what it cost.</summary>
    [Fact]
    public async Task A_lifted_day_admits_past_the_ceiling_and_keeps_counting()
    {
        Start(ceiling: 0.10m);

        await AdmitAsync(pictures: 1);
        Assert.Equal(AiQuotaAdmissionOutcome.PlatformCeiling, (await AdmitAsync(pictures: 1)).Outcome);

        await LiftAsync();

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(pictures: 2)).Outcome);
        Assert.Equal(3 * PictureMicros, (await DayAsync(Noon))!.AdmittedMicros);
    }

    // ---- alerting ---------------------------------------------------------------------------------------

    /// <summary>
    /// <strong>One alert a day, not one a refusal.</strong> Three refusals raise exactly one error-level line,
    /// under the event an alert rule keys on, and it names the day and the ceiling — never an account.
    /// </summary>
    [Fact]
    public async Task A_tripped_day_raises_one_alert_however_many_requests_it_refuses()
    {
        var logs = new CapturingLoggerProvider();
        Start(ceiling: 0.10m, configure: services => services.AddLogging(logging => logging.AddProvider(logs)));

        await AdmitAsync(pictures: 1);

        for (var refusal = 0; refusal < 3; refusal++)
        {
            Assert.Equal(AiQuotaAdmissionOutcome.PlatformCeiling, (await AdmitAsync(pictures: 1)).Outcome);
        }

        var alert = Assert.Single(logs.Entries, entry => entry.EventId.Name == "PlatformImageSpendCeilingTripped");
        Assert.Equal(LogLevel.Error, alert.Level);
        Assert.Null(alert.Exception);
        Assert.Contains("2026-10-27", alert.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("user-sam", alert.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>A failure to record the trip never changes the creator's answer.</strong> The refusal is already
    /// decided; the failure is logged at the alert's own level, with the exception, so it still pages.
    /// </summary>
    [Fact]
    public async Task A_trip_that_cannot_be_recorded_still_refuses_and_still_alerts()
    {
        var logs = new CapturingLoggerProvider();
        Start(ceiling: 0.10m, configure: services => services
            .AddLogging(logging => logging.AddProvider(logs))
            .AddScoped<IPlatformImageSpendDataLayer, UnrecordableTrips>());

        await AdmitAsync(pictures: 1);

        Assert.Equal(AiQuotaAdmissionOutcome.PlatformCeiling, (await AdmitAsync(pictures: 1)).Outcome);

        var alert = Assert.Single(logs.Entries, entry => entry.EventId.Name == "PlatformImageSpendCeilingTripped");
        Assert.Equal(LogLevel.Error, alert.Level);
        Assert.IsType<InvalidOperationException>(alert.Exception);
    }

    // ---- retried transactions -------------------------------------------------------------------------

    /// <summary>
    /// <strong>A re-run after a commit that landed finds the hold it made.</strong> A retrying execution strategy
    /// re-runs the whole unit when a fault arrives after the commit; judging again would charge the day twice
    /// for one request and lose to the hold's unique index. This strategy re-runs every charge unconditionally,
    /// which is that fault at its worst.
    /// </summary>
    [Fact]
    public async Task A_charge_re_run_after_its_commit_landed_is_counted_once()
    {
        Start(ceiling: 1m, replayCharges: true);

        var admission = await AdmitAsync(pictures: 1);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, admission.Outcome);
        Assert.Equal(admission.ReservationId, Assert.Single(await ReservationsAsync()).Id);
        Assert.Equal(PictureMicros, (await DayAsync(Noon))!.AdmittedMicros);
    }

    // ---- giving back -----------------------------------------------------------------------------------

    /// <summary>
    /// A hold whose operation was never queued gives back its charge with its allowance — once, however many
    /// times the release runs.
    /// </summary>
    [Fact]
    public async Task Releasing_a_never_queued_hold_gives_its_charge_back_once()
    {
        Start(ceiling: 1m);

        var kept = await AdmitAsync(pictures: 1);
        var released = await AdmitAsync(pictures: 2);

        await ReleaseAsync(released.ReservationId!.Value);
        await ReleaseAsync(released.ReservationId!.Value);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, kept.Outcome);
        Assert.Equal(PictureMicros, (await DayAsync(Noon))!.AdmittedMicros);
    }

    /// <summary>
    /// A give-back goes to the day the charge was counted on: a request that failed across midnight takes
    /// nothing from the new day's room.
    /// </summary>
    [Fact]
    public async Task A_release_after_midnight_gives_back_to_the_day_it_was_counted_on()
    {
        Start(ceiling: 1m);

        var yesterday = await AdmitAsync(pictures: 1);

        _clock.Advance(TimeSpan.FromHours(13));
        await AdmitAsync(pictures: 1);

        await ReleaseAsync(yesterday.ReservationId!.Value);

        Assert.Equal(0, (await DayAsync(Noon))!.AdmittedMicros);
        Assert.Equal(PictureMicros, (await DayAsync(_clock.UtcNow))!.AdmittedMicros);
    }

    /// <summary>
    /// The give-back is only for a hold that never reached a provider. One that was settled — pictures made,
    /// even fewer than asked for — stays counted, so the counter can read high and never low.
    /// </summary>
    [Fact]
    public async Task A_settled_hold_is_not_given_back()
    {
        Start(ceiling: 1m);

        var admission = await AdmitAsync(pictures: 2);
        await SettleAsync(admission.ReservationId!.Value, billed: 1);

        await ReleaseAsync(admission.ReservationId!.Value);

        Assert.Equal(2 * PictureMicros, (await DayAsync(Noon))!.AdmittedMicros);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private void Start(
        decimal? ceiling,
        decimal allowance = 1_000_000m,
        bool listPrice = true,
        bool replayCharges = false,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection()
            .AddAudit()
            .AddApplicationTime()
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultAllowance = allowance;
                options.ImageRates[Model] = Rate;
                options.PlatformImageSpendPerDay = ceiling;
            })
            .Configure<AiImageListPriceOptions>(options =>
            {
                if (listPrice)
                {
                    options.Images[Model] = ListPrice;
                }
            })
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection, sqlite =>
                {
                    if (replayCharges)
                    {
                        sqlite.ExecutionStrategy(dependencies => new ReplayingChargeStrategy(dependencies));
                    }
                })
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>());

        configure?.Invoke(services);
        _provider = services.BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreated();
    }

    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync(int pictures)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitImageAsync(
            new AiImageQuotaAdmissionRequestServiceModel(
                "user-sam", Guid.NewGuid(), pictures, Model, null, null, _clock.UtcNow + TimeSpan.FromDays(2)),
            Ct);
    }

    private async Task ReleaseAsync(Guid reservationId)
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
            .ReleaseImageAsync(reservationId, Ct);
    }

    /// <summary>
    /// Marks the hold settled as a finished generation would, without the ledger: the counter is the question
    /// here, and settlement's own behaviour is <c>GeneratedImageWorkerTests.Quota</c>'s.
    /// </summary>
    private async Task SettleAsync(Guid reservationId, int billed)
    {
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var hold = await db.AccountAiQuotaReservations.SingleAsync(reservation => reservation.Id == reservationId, Ct);
        hold.Status = AiQuotaReservationStatus.Settled;
        hold.SettledAmount = Rate * billed;
        hold.UsageReported = true;
        hold.SettledAt = _clock.UtcNow;

        await db.SaveChangesAsync(Ct);
    }

    private async Task LiftAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var day = PlatformImageSpendPolicy.DayOf(_clock.UtcNow);

        var row = await db.PlatformImageSpendDays.SingleAsync(spend => spend.Day == day, Ct);
        row.LiftedAt = _clock.UtcNow;
        row.LiftedBy = "tests";

        await db.SaveChangesAsync(Ct);
    }

    private async Task<PlatformImageSpendDay?> DayAsync(DateTimeOffset instant)
    {
        await using var scope = _provider!.CreateAsyncScope();
        var day = PlatformImageSpendPolicy.DayOf(instant);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PlatformImageSpendDays.AsNoTracking().SingleOrDefaultAsync(spend => spend.Day == day, Ct);
    }

    private async Task<AccountAiQuotaReservation> ReservationAsync(Guid id) =>
        (await ReservationsAsync()).Single(reservation => reservation.Id == id);

    private async Task<List<AccountAiQuotaReservation>> ReservationsAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations.AsNoTracking().ToListAsync(Ct);
    }

    private async Task<AccountAiQuotaPeriod> PeriodAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaPeriods.AsNoTracking().SingleAsync(Ct);
    }

    private async Task<List<PlatformAuditLog>> AuditAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PlatformAuditLogs.AsNoTracking().ToListAsync(Ct);
    }

    /// <summary>
    /// Runs the charge's unit twice and answers with the second run — what a retrying strategy does when a
    /// transient fault arrives after the first run's commit landed. Every other unit runs once.
    /// </summary>
    private sealed class ReplayingChargeStrategy(ExecutionStrategyDependencies dependencies) : IExecutionStrategy
    {
        public bool RetriesOnFailure => true;

        public TResult Execute<TState, TResult>(
            TState state,
            Func<DbContext, TState, TResult> operation,
            Func<DbContext, TState, ExecutionResult<TResult>>? verifySucceeded) =>
            operation(dependencies.CurrentContext.Context, state);

        public async Task<TResult> ExecuteAsync<TState, TResult>(
            TState state,
            Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
            Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>>? verifySucceeded,
            CancellationToken cancellationToken = default)
        {
            var context = dependencies.CurrentContext.Context;
            var first = await operation(context, state, cancellationToken);

            return typeof(TResult).FullName!.Contains(nameof(AiQuotaWriteOutcome), StringComparison.Ordinal)
                ? await operation(context, state, cancellationToken)
                : first;
        }
    }

    /// <summary>Reads days as the real data layer does, and cannot record a trip.</summary>
    private sealed class UnrecordableTrips(CreatorPantryDbContext context) : IPlatformImageSpendDataLayer
    {
        public Task<PlatformImageSpendDay?> FindDayAsync(DateOnly day, CancellationToken cancellationToken) =>
            context.PlatformImageSpendDays.AsNoTracking().SingleOrDefaultAsync(row => row.Day == day, cancellationToken);

        public Task<bool> TripAsync(
            DateOnly day, DateTimeOffset now, PlatformAuditEntry audit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The audit table is unavailable.");

        public Task<bool> LiftAsync(
            DateOnly day, DateTimeOffset now, string liftedBy, PlatformAuditEntry audit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record CapturedLog(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<CapturedLog> _entries = [];

        public IReadOnlyList<CapturedLog> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries)
                {
                    owner._entries.Add(new CapturedLog(logLevel, eventId, formatter(state, exception), exception));
                }
            }
        }
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
