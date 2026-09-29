using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// Admitting a run against an account's AI allowance and settling what it spent (USAGE-004/006): what is held,
/// what is charged, what is given back, and when.
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs against one worker at a time. The one property this file structurally cannot prove is
/// the contended one — SQLite's row version is a default on insert and is never regenerated on update, so two
/// admissions racing would both succeed here whatever the code did. That is
/// <see cref="AccountAiQuotaConcurrencyTests"/>, on real SQL Server, and it is the reason that file exists.
/// </para>
/// <para>
/// Rates are set so the arithmetic is readable: one credit per input token, two per output token, a ten-credit
/// per-call estimate and two calls per run — so an admitted run holds twenty.
/// </para>
/// </remarks>
public sealed class AccountAiQuotaAdmissionTests : IDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid MembershipInA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid MembershipInB = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    /// <summary>One person. Two memberships above, one of these — the whole point of USAGE-001.</summary>
    private const string Account = "user-sam";

    private const string Model = "test-model";

    /// <summary>Ten credits a call, two calls a run: an admitted run holds twenty.</summary>
    private const decimal PerCall = 10m;

    private const decimal Hold = 20m;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;

    /// <summary>Mid-month, so the period opened around it has room on both sides.</summary>
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));

    public AccountAiQuotaAdmissionTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddApplicationTime()
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<AiOperationClaimRepository>()
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultAllowance = 1000m;
                options.DefaultTaskEstimate = PerCall;
                options.EstimatedCallsPerRun = 2;
                options.ModelRates[Model] = new AiQuotaModelRate(1_000_000m, 2_000_000m);
            })
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "a", CreatedAt = _clock.UtcNow },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "b", CreatedAt = _clock.UtcNow });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    // ---- admission -------------------------------------------------------------------------------------

    [Fact]
    public async Task An_admitted_run_holds_its_estimate_against_a_period_opened_for_it()
    {
        var admission = await AdmitAsync();

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, admission.Outcome);
        Assert.NotNull(admission.ReservationId);
        Assert.Equal(Hold, admission.Reserved);
        Assert.Equal(1000m - Hold, admission.Remaining);
        Assert.Equal(AiQuotaUnit.Credits, admission.Unit);

        var period = await PeriodAsync();

        // Monthly, anchored to the first, in the platform's default zone -- half-open, so the next period
        // begins exactly where this one ends.
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), period.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), period.EndsAt);
        Assert.Equal(period.EndsAt, admission.PeriodEndsAt);
        Assert.Equal(1000m, period.Allowance);
        Assert.Equal(Hold, period.Reserved);
        Assert.Equal(0m, period.Consumed);

        var reservation = await ReservationAsync(admission.ReservationId!.Value);

        Assert.Equal(AiQuotaReservationStatus.Held, reservation.Status);
        Assert.Equal(Account, reservation.AccountId);
        Assert.Equal(Hold, reservation.ReservedAmount);
        Assert.Equal(PerCall, reservation.PerAttemptEstimate);
        Assert.Null(reservation.SettledAmount);
        Assert.Null(reservation.ReleasedAt);
        Assert.Null(reservation.PostedAt);
    }

    /// <summary>
    /// A diagnostic run never reaches a provider, so it cannot spend allowance — and making it queue behind one
    /// would be a refusal nothing earned. Nothing is held and no period is even opened.
    /// </summary>
    [Fact]
    public async Task An_unmeterable_run_is_admitted_without_a_period_being_read()
    {
        var admission = await AdmitAsync(meterable: false, taskType: AiTaskType.Diagnostic);

        Assert.Equal(AiQuotaAdmissionOutcome.NotMetered, admission.Outcome);
        Assert.Null(admission.ReservationId);
        Assert.Equal(0m, admission.Reserved);

        Assert.Empty(await AllPeriodsAsync());
        Assert.Empty(await AllReservationsAsync());
    }

    /// <summary>
    /// One claim gets one hold. A redelivered admission finds the one it already took rather than taking a
    /// second, which is what stops a retried message spending an account's allowance twice.
    /// </summary>
    [Fact]
    public async Task A_replayed_admission_finds_the_hold_it_already_took()
    {
        var operationId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();

        var first = await AdmitAsync(operationId: operationId, leaseToken: leaseToken);
        var second = await AdmitAsync(operationId: operationId, leaseToken: leaseToken);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, second.Outcome);
        Assert.Equal(first.ReservationId, second.ReservationId);
        Assert.Single(await AllReservationsAsync());
        Assert.Equal(Hold, (await PeriodAsync()).Reserved);
    }

    /// <summary>
    /// The same operation claimed again after its lease lapsed is a fresh request for allowance. The abandoned
    /// hold must not pay for it, so the pair — not the operation — is the identity.
    /// </summary>
    [Fact]
    public async Task A_second_claim_of_one_operation_takes_its_own_hold()
    {
        var operationId = Guid.NewGuid();

        var first = await AdmitAsync(operationId: operationId, leaseToken: Guid.NewGuid());
        var second = await AdmitAsync(operationId: operationId, leaseToken: Guid.NewGuid());

        Assert.NotEqual(first.ReservationId, second.ReservationId);
        Assert.Equal(2, (await AllReservationsAsync()).Count);
        Assert.Equal(Hold * 2, (await PeriodAsync()).Reserved);
    }

    [Fact]
    public async Task An_account_whose_allowance_is_spent_is_refused_and_told_when_it_resets()
    {
        await GiveQuotaAsync(allowance: 30m);
        await AdmitAsync();

        var admission = await AdmitAsync();

        Assert.Equal(AiQuotaAdmissionOutcome.Exhausted, admission.Outcome);
        Assert.Null(admission.ReservationId);
        Assert.Equal(0m, admission.Reserved);
        Assert.Equal(10m, admission.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), admission.PeriodEndsAt);

        // Nothing was written at all -- a refusal spends nothing, not even the attempt to spend.
        Assert.Single(await AllReservationsAsync());
        Assert.Equal(Hold, (await PeriodAsync()).Reserved);
    }

    /// <summary>
    /// Two runs cannot both be admitted past the last of the allowance. Sequentially here; the same property
    /// under genuine concurrency is <see cref="AccountAiQuotaConcurrencyTests"/>.
    /// </summary>
    [Fact]
    public async Task The_allowance_admits_exactly_as_many_runs_as_it_covers()
    {
        await GiveQuotaAsync(allowance: 50m);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync()).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync()).Outcome);
        Assert.Equal(AiQuotaAdmissionOutcome.Exhausted, (await AdmitAsync()).Outcome);

        Assert.Equal(40m, (await PeriodAsync()).Reserved);
    }

    /// <summary>
    /// Suspension is a fact about today, read from the terms in force rather than from the running period's
    /// frozen copy — and it offers no reset time, because there is nothing to wait for (USAGE-007).
    /// </summary>
    [Fact]
    public async Task A_suspended_account_is_refused_with_no_reset_to_offer()
    {
        await GiveQuotaAsync(allowance: 1000m, suspended: true);

        var admission = await AdmitAsync();

        Assert.Equal(AiQuotaAdmissionOutcome.Suspended, admission.Outcome);
        Assert.Null(admission.PeriodEndsAt);
        Assert.Empty(await AllPeriodsAsync());
        Assert.Empty(await AllReservationsAsync());
    }

    /// <summary>
    /// An unpriced capability is refused rather than admitted for nothing. Reserving zero would make it
    /// unmetered — the failure <c>ConfiguredAiCostEstimator</c> avoids by returning null instead of zero.
    /// </summary>
    [Fact]
    public async Task A_capability_with_no_configured_estimate_is_refused_rather_than_admitted_free()
    {
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOptions<AiQuotaOptions>>().Value.DefaultTaskEstimate = 0m;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitAsync(
                Request(Guid.NewGuid(), Guid.NewGuid(), AiTaskType.RecipeConcepts, true),
                TestContext.Current.CancellationToken));
    }

    // ---- settlement ------------------------------------------------------------------------------------

    /// <summary>
    /// Over-reserve: the run used less than the estimate, and the difference goes straight back to the balance.
    /// </summary>
    [Fact]
    public async Task A_run_that_used_less_than_its_estimate_gives_the_difference_back()
    {
        var admission = await AdmitAsync();

        await SettleAsync(admission.ReservationId!.Value, Attempt(inputTokens: 5));

        var reservation = await ReservationAsync(admission.ReservationId.Value);
        Assert.Equal(AiQuotaReservationStatus.Settled, reservation.Status);
        Assert.True(reservation.UsageReported);
        Assert.Equal(5m, reservation.SettledAmount);

        var period = await PeriodAsync();
        Assert.Equal(0m, period.Reserved);
        Assert.Equal(5m, period.Consumed);
    }

    /// <summary>
    /// Under-reserve: the provider used more than the estimate held for it. Recorded in full — refusing to
    /// store the overshoot would lose the attempt rather than win the argument.
    /// </summary>
    [Fact]
    public async Task A_run_that_used_more_than_its_estimate_is_charged_what_it_used()
    {
        var admission = await AdmitAsync();

        await SettleAsync(admission.ReservationId!.Value, Attempt(inputTokens: 100, outputTokens: 30));

        var period = await PeriodAsync();
        Assert.Equal(0m, period.Reserved);

        // One credit an input token, two an output token.
        Assert.Equal(160m, period.Consumed);
    }

    /// <summary>
    /// The overshoot may push a period past its own allowance, which the table deliberately does not constrain
    /// — and the next admission is what refuses, rather than the settlement.
    /// </summary>
    [Fact]
    public async Task An_overspent_period_is_recorded_and_then_refuses_the_next_run()
    {
        await GiveQuotaAsync(allowance: 30m);

        var admission = await AdmitAsync();
        await SettleAsync(admission.ReservationId!.Value, Attempt(inputTokens: 500));

        var period = await PeriodAsync();
        Assert.Equal(500m, period.Consumed);
        Assert.True(period.Consumed > period.Allowance);

        Assert.Equal(AiQuotaAdmissionOutcome.Exhausted, (await AdmitAsync()).Outcome);
    }

    /// <summary>
    /// A call whose cost the provider declined to report is not a call that was free (USAGE-005). It is charged
    /// the estimate it was admitted under, and the row says the charge was not a measurement.
    /// </summary>
    [Fact]
    public async Task An_attempt_the_provider_reported_nothing_for_is_charged_the_estimate()
    {
        var admission = await AdmitAsync();

        await SettleAsync(admission.ReservationId!.Value, Attempt());

        var reservation = await ReservationAsync(admission.ReservationId.Value);
        Assert.Equal(AiQuotaReservationStatus.Settled, reservation.Status);
        Assert.False(reservation.UsageReported);
        Assert.Equal(PerCall, reservation.SettledAmount);
        Assert.Equal(PerCall, (await PeriodAsync()).Consumed);
    }

    /// <summary>
    /// A model with no configured rate cannot be converted, which is the same unknown as an unreported count
    /// and is charged the same way rather than as nothing.
    /// </summary>
    [Fact]
    public async Task An_attempt_on_an_unpriced_model_is_charged_the_estimate()
    {
        var admission = await AdmitAsync();

        await SettleAsync(
            admission.ReservationId!.Value, Attempt(inputTokens: 900, modelName: "a-model-nobody-priced"));

        var reservation = await ReservationAsync(admission.ReservationId.Value);
        Assert.False(reservation.UsageReported);
        Assert.Equal(PerCall, reservation.SettledAmount);
    }

    [Fact]
    public async Task A_run_with_one_reported_and_one_unreported_attempt_charges_both()
    {
        var admission = await AdmitAsync();

        await SettleAsync(admission.ReservationId!.Value, Attempt(inputTokens: 7), Attempt());

        var reservation = await ReservationAsync(admission.ReservationId.Value);

        // The measured one, plus the estimate for the one nothing was said about.
        Assert.Equal(7m + PerCall, reservation.SettledAmount);
        Assert.False(reservation.UsageReported);
    }

    /// <summary>
    /// A known zero rather than an unknown one, and a different row from an expiry: a worker got here and said
    /// the provider was never reached.
    /// </summary>
    [Fact]
    public async Task A_run_that_reached_no_provider_gives_its_whole_hold_back()
    {
        var admission = await AdmitAsync();

        await SettleAsync(admission.ReservationId!.Value, Attempt(billable: false));

        var reservation = await ReservationAsync(admission.ReservationId.Value);
        Assert.Equal(AiQuotaReservationStatus.Released, reservation.Status);
        Assert.Equal(0m, reservation.SettledAmount);

        var period = await PeriodAsync();
        Assert.Equal(0m, period.Reserved);
        Assert.Equal(0m, period.Consumed);
    }

    [Fact]
    public async Task A_run_with_no_attempts_at_all_gives_its_whole_hold_back()
    {
        var admission = await AdmitAsync();

        await SettleAsync(admission.ReservationId!.Value);

        Assert.Equal(
            AiQuotaReservationStatus.Released, (await ReservationAsync(admission.ReservationId.Value)).Status);
        Assert.Equal(0m, (await PeriodAsync()).Reserved);
    }

    /// <summary>A redelivered settlement settles once (USAGE-004).</summary>
    [Fact]
    public async Task A_replayed_settlement_settles_once()
    {
        var admission = await AdmitAsync();

        await SettleAsync(admission.ReservationId!.Value, Attempt(inputTokens: 5));
        await SettleAsync(admission.ReservationId.Value, Attempt(inputTokens: 900));

        Assert.Equal(5m, (await ReservationAsync(admission.ReservationId.Value)).SettledAmount);
        Assert.Equal(5m, (await PeriodAsync()).Consumed);
    }

    /// <summary>
    /// Posting is idempotent per column, not per call, which is what lets the worker and the sweep both reach
    /// for it without either having to know whether the other did.
    /// </summary>
    [Fact]
    public async Task Posting_a_settlement_twice_applies_it_once()
    {
        var admission = await AdmitAsync();
        await SettleOnlyAsync(admission.ReservationId!.Value, Attempt(inputTokens: 5));

        await PostAsync(admission.ReservationId.Value);
        await PostAsync(admission.ReservationId.Value);

        var period = await PeriodAsync();
        Assert.Equal(0m, period.Reserved);
        Assert.Equal(5m, period.Consumed);
    }

    // ---- expiry and the sweep --------------------------------------------------------------------------

    /// <summary>
    /// A hold whose lease lapsed is given back rather than stranding the balance — and it is an unknown, not a
    /// zero, so it is not the same row as a release.
    /// </summary>
    [Fact]
    public async Task A_hold_whose_lease_lapsed_is_released_by_the_sweep()
    {
        var admission = await AdmitAsync();

        _clock.Advance(AiPolicy.LeaseDuration + TimeSpan.FromMinutes(1));
        var summary = await SweepAsync();

        Assert.Equal(1, summary.Expired);

        var reservation = await ReservationAsync(admission.ReservationId!.Value);
        Assert.Equal(AiQuotaReservationStatus.Expired, reservation.Status);
        Assert.NotNull(reservation.ReleasedAt);
        Assert.Null(reservation.SettledAmount);

        var period = await PeriodAsync();
        Assert.Equal(0m, period.Reserved);
        Assert.Equal(0m, period.Consumed);
    }

    [Fact]
    public async Task A_hold_inside_its_lease_is_left_alone()
    {
        await AdmitAsync();

        _clock.Advance(AiPolicy.LeaseDuration - TimeSpan.FromMinutes(1));

        Assert.Equal(0, (await SweepAsync()).Expired);
        Assert.Equal(Hold, (await PeriodAsync()).Reserved);
    }

    /// <summary>
    /// Renewing the lease renews the hold, so a run that legitimately takes longer than one lease does not have
    /// the allowance it is spending handed to somebody else underneath it.
    /// </summary>
    [Fact]
    public async Task Renewing_a_hold_keeps_it_past_its_original_lease()
    {
        var admission = await AdmitAsync();

        _clock.Advance(AiPolicy.LeaseDuration - TimeSpan.FromMinutes(1));
        await RenewAsync(admission.ReservationId!.Value, _clock.UtcNow + AiPolicy.LeaseDuration);

        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(0, (await SweepAsync()).Expired);
        Assert.Equal(
            AiQuotaReservationStatus.Held, (await ReservationAsync(admission.ReservationId.Value)).Status);
    }

    /// <summary>
    /// The backstop for a worker that settled and then died: the charge is already durably on the reservation,
    /// and the sweep is what moves it onto the period.
    /// </summary>
    [Fact]
    public async Task A_settled_hold_nobody_posted_is_posted_by_the_sweep()
    {
        var admission = await AdmitAsync();
        await SettleOnlyAsync(admission.ReservationId!.Value, Attempt(inputTokens: 9));

        var before = await PeriodAsync();
        Assert.Equal(Hold, before.Reserved);
        Assert.Equal(0m, before.Consumed);

        Assert.Equal(1, (await SweepAsync()).Posted);

        var after = await PeriodAsync();
        Assert.Equal(0m, after.Reserved);
        Assert.Equal(9m, after.Consumed);
    }

    /// <summary>
    /// The sweep must not expire something that already settled: doing so would overwrite a decision with an
    /// absence of one, and the charge would be lost.
    /// </summary>
    [Fact]
    public async Task The_sweep_does_not_expire_a_hold_that_already_settled()
    {
        var admission = await AdmitAsync();
        await SettleOnlyAsync(admission.ReservationId!.Value, Attempt(inputTokens: 9));

        _clock.Advance(AiPolicy.LeaseDuration + TimeSpan.FromMinutes(1));
        var summary = await SweepAsync();

        Assert.Equal(1, summary.Posted);
        Assert.Equal(0, summary.Expired);

        Assert.Equal(
            AiQuotaReservationStatus.Settled, (await ReservationAsync(admission.ReservationId.Value)).Status);
        Assert.Equal(9m, (await PeriodAsync()).Consumed);
    }

    // ---- periods ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_period_is_opened_once_however_many_runs_ask_for_it()
    {
        await AdmitAsync();
        await AdmitAsync();

        Assert.Single(await AllPeriodsAsync());
    }

    /// <summary>
    /// A hold taken just before a boundary settles into the period it named. It cannot drift into the next one,
    /// because the reservation names the row — which is why a period's totals may still move after it has
    /// ended, and why <c>SettledAt</c> rather than the clock is what says they are final.
    /// </summary>
    [Fact]
    public async Task A_hold_taken_before_a_boundary_settles_into_the_period_it_named()
    {
        var admission = await AdmitAsync();
        var openedIn = (await PeriodAsync()).Id;

        // Into the next month, which is a period this account has never opened.
        _clock.Advance(TimeSpan.FromDays(20));

        await SettleAsync(admission.ReservationId!.Value, Attempt(inputTokens: 12));
        await AdmitAsync();

        var periods = await AllPeriodsAsync();
        Assert.Equal(2, periods.Count);

        var first = periods.Single(period => period.Id == openedIn);
        Assert.Equal(12m, first.Consumed);
        Assert.Equal(0m, first.Reserved);

        var second = periods.Single(period => period.Id != openedIn);
        Assert.Equal(first.EndsAt, second.StartsAt);
        Assert.Equal(0m, second.Consumed);
        Assert.Equal(Hold, second.Reserved);
    }

    [Fact]
    public async Task An_ended_period_is_finalized_once_nothing_can_move_its_totals()
    {
        var admission = await AdmitAsync();
        await SettleAsync(admission.ReservationId!.Value, Attempt(inputTokens: 3));

        // Still running: the totals are settled but the period is not over, so nothing is final yet.
        Assert.Equal(0, (await SweepAsync()).Finalized);

        _clock.Advance(TimeSpan.FromDays(20));

        Assert.Equal(1, (await SweepAsync()).Finalized);

        var period = await PeriodAsync();
        Assert.NotNull(period.SettledAt);
        Assert.True(period.SettledAt >= period.EndsAt);
    }

    /// <summary>
    /// An ended period with a hold still outstanding is not final — that hold can still settle into it, which
    /// is exactly why <c>SettledAt</c> is not "the clock passed <c>EndsAt</c>".
    /// </summary>
    [Fact]
    public async Task An_ended_period_with_an_undecided_hold_is_not_finalized()
    {
        var admission = await AdmitAsync();

        // Far enough past the end to be finalizable, but not far enough for the lease to have lapsed, so the
        // hold is still genuinely live.
        _clock.Advance(TimeSpan.FromDays(20));

        using (var scope = _provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().RenewAsync(
                admission.ReservationId!.Value,
                _clock.UtcNow + AiPolicy.LeaseDuration,
                TestContext.Current.CancellationToken);
        }

        var summary = await SweepAsync();

        Assert.Equal(0, summary.Expired);
        Assert.Equal(0, summary.Finalized);
        Assert.Null((await PeriodAsync()).SettledAt);
    }

    [Fact]
    public async Task A_running_period_is_never_finalized()
    {
        var admission = await AdmitAsync();
        await SettleAsync(admission.ReservationId!.Value, Attempt(inputTokens: 3));

        Assert.Equal(0, (await SweepAsync()).Finalized);
        Assert.Null((await PeriodAsync()).SettledAt);
    }

    /// <summary>
    /// Unused allowance rolls into an abutting period, capped — and the amount is recorded on the new period
    /// rather than re-derived, so the roll is readable after the fact.
    /// </summary>
    [Fact]
    public async Task Unused_allowance_carries_into_an_abutting_period_up_to_the_cap()
    {
        await GiveQuotaAsync(allowance: 100m, carryOver: AiQuotaCarryOver.Unused, carryOverCap: 30m);

        var admission = await AdmitAsync();
        await SettleAsync(admission.ReservationId!.Value, Attempt(inputTokens: 40));

        _clock.Advance(TimeSpan.FromDays(20));
        await AdmitAsync();

        var opened = (await AllPeriodsAsync()).Single(period => period.CarriedOver > 0m);

        // Sixty were left, and the cap is what an account that spends nothing is not allowed to accumulate.
        Assert.Equal(30m, opened.CarriedOver);
        Assert.Equal(100m + 30m - Hold, Remaining(opened));
    }

    /// <summary>
    /// Only the current period is opened, never the ones an idle account skipped — so carry-over comes from a
    /// period that genuinely abuts this one, and is zero across a gap. Stated here rather than left implicit,
    /// because it is the visible consequence of not materializing empty periods.
    /// </summary>
    [Fact]
    public async Task Nothing_carries_across_a_gap()
    {
        await GiveQuotaAsync(allowance: 100m, carryOver: AiQuotaCarryOver.Unused, carryOverCap: 100m);

        var admission = await AdmitAsync();
        await SettleAsync(admission.ReservationId!.Value, Attempt(inputTokens: 1));

        // Two whole months later: the period in between was never opened, so there is nothing abutting.
        _clock.Advance(TimeSpan.FromDays(75));
        await AdmitAsync();

        var periods = await AllPeriodsAsync();
        Assert.Equal(2, periods.Count);
        Assert.All(periods, period => Assert.Equal(0m, period.CarriedOver));
    }

    [Fact]
    public async Task Nothing_carries_when_the_terms_say_it_does_not()
    {
        await GiveQuotaAsync(allowance: 100m);

        await AdmitAsync();
        _clock.Advance(TimeSpan.FromDays(20));
        await AdmitAsync();

        Assert.All(await AllPeriodsAsync(), period => Assert.Equal(0m, period.CarriedOver));
    }

    // ---- one account, two workspaces -------------------------------------------------------------------

    /// <summary>
    /// One person does not earn a fresh allowance by joining another workspace. Both runs spend the same
    /// period, which is only possible because nothing in this module is workspace-owned.
    /// </summary>
    [Fact]
    public async Task One_account_working_in_two_workspaces_spends_one_allowance()
    {
        await AdmitAsync(workspaceId: WorkspaceA, membershipId: MembershipInA);
        await AdmitAsync(workspaceId: WorkspaceB, membershipId: MembershipInB);

        var period = Assert.Single(await AllPeriodsAsync());
        Assert.Equal(Hold * 2, period.Reserved);
        Assert.All(await AllReservationsAsync(), row => Assert.Equal(Account, row.AccountId));
    }

    // ---- the AI seam -----------------------------------------------------------------------------------

    /// <summary>
    /// The property the whole two-transaction split exists to keep: the charge against the allowance commits
    /// with the attempt record and the ledger entry it was computed from, so a settled attempt whose
    /// reservation was never settled is unreachable.
    /// </summary>
    [Fact]
    public async Task A_settled_attempt_settles_its_allowance_in_the_same_write()
    {
        var operation = await ClaimedAsync();
        var admission = await AdmitAsync(operationId: operation.OperationId, leaseToken: operation.LeaseToken);

        using (var scope = _provider.CreateScope())
        {
            Resolve(scope, WorkspaceA, MembershipInA);

            var outcome = await scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>()
                .StoreProposalAsync(
                    operation.OperationId,
                    operation.LeaseToken,
                    Proposal(operation.OperationId),
                    [AttemptRecord(inputTokens: 6)],
                    admission.ReservationId,
                    TestContext.Current.CancellationToken);

            Assert.Equal(AiOperationWriteOutcome.Applied, outcome);
        }

        var reservation = await ReservationAsync(admission.ReservationId!.Value);
        Assert.Equal(AiQuotaReservationStatus.Settled, reservation.Status);
        Assert.Equal(6m, reservation.SettledAmount);

        Assert.Single(await LedgerAsync());
    }

    /// <summary>
    /// A run that failed before reaching a provider has nothing to bill and still holds allowance. Zero
    /// attempts is the release case, not the skip case — leaving it to the sweep would strand the hold for the
    /// length of a lease.
    /// </summary>
    [Fact]
    public async Task A_failure_with_no_attempts_still_releases_its_hold()
    {
        var operation = await ClaimedAsync();
        var admission = await AdmitAsync(operationId: operation.OperationId, leaseToken: operation.LeaseToken);

        using (var scope = _provider.CreateScope())
        {
            Resolve(scope, WorkspaceA, MembershipInA);

            await scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>().FailAsync(
                operation.OperationId,
                operation.LeaseToken,
                AiFailureCategory.TemplateUnavailable,
                "no template",
                [],
                admission.ReservationId,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            AiQuotaReservationStatus.Released, (await ReservationAsync(admission.ReservationId!.Value)).Status);
        Assert.Empty(await LedgerAsync());
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static decimal Remaining(AccountAiQuotaPeriod period) =>
        period.Allowance + period.CarriedOver - period.Consumed - period.Reserved;

    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync(
        Guid? operationId = null,
        Guid? leaseToken = null,
        AiTaskType taskType = AiTaskType.RecipeConcepts,
        bool meterable = true,
        Guid? workspaceId = null,
        Guid? membershipId = null)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId ?? WorkspaceA, membershipId ?? MembershipInA);

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitAsync(
            Request(operationId ?? Guid.NewGuid(), leaseToken ?? Guid.NewGuid(), taskType, meterable),
            TestContext.Current.CancellationToken);
    }

    private AiQuotaAdmissionRequestServiceModel Request(
        Guid operationId, Guid leaseToken, AiTaskType taskType, bool meterable) =>
        new(Account, operationId, leaseToken, taskType, meterable, _clock.UtcNow + AiPolicy.LeaseDuration);

    /// <summary>Settles and then posts, which is the order the worker does it in.</summary>
    private async Task SettleAsync(Guid reservationId, params AiUsageAttemptServiceModel[] attempts)
    {
        await SettleOnlyAsync(reservationId, attempts);
        await PostAsync(reservationId);
    }

    /// <summary>
    /// Stops at the settlement, for the tests about what the sweep finishes when a worker does not.
    /// </summary>
    private async Task SettleOnlyAsync(Guid reservationId, params AiUsageAttemptServiceModel[] attempts)
    {
        using var scope = _provider.CreateScope();
        var quota = scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>();

        await quota.StageSettlementAsync(reservationId, attempts, TestContext.Current.CancellationToken);

        // The caller's save is what commits it: this facade stages onto the caller's unit of work exactly as
        // the ledger's does, so a test that forgot this would prove nothing.
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task PostAsync(Guid reservationId)
    {
        using var scope = _provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
            .PostAsync(reservationId, TestContext.Current.CancellationToken);
    }

    private async Task RenewAsync(Guid reservationId, DateTimeOffset expiresAt)
    {
        using var scope = _provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
            .RenewAsync(reservationId, expiresAt, TestContext.Current.CancellationToken);
    }

    private async Task<AiQuotaMaintenanceSummary> SweepAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaMaintenanceFacade>()
            .SweepAsync(TestContext.Current.CancellationToken);
    }

    private async Task GiveQuotaAsync(
        decimal allowance,
        bool suspended = false,
        AiQuotaCarryOver carryOver = AiQuotaCarryOver.None,
        decimal? carryOverCap = null)
    {
        using var scope = _provider.CreateScope();
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
            CarryOver = carryOver,
            CarryOverCap = carryOverCap,
            IsSuspended = suspended,
            EffectiveFrom = _clock.UtcNow.AddDays(-30),
            LastChangedAt = _clock.UtcNow.AddDays(-30),
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static AiUsageAttemptServiceModel Attempt(
        int? inputTokens = null,
        int? outputTokens = null,
        bool billable = true,
        string modelName = Model) => new()
        {
            AttemptNumber = 1,
            OccurredAt = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
            ProviderName = "test-provider",
            ModelName = modelName,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Outcome = AiUsageOutcome.Succeeded,
            IsBillable = billable,
        };

    private AiAttemptRecord AttemptRecord(int? inputTokens = null) => new()
    {
        AttemptNumber = 1,
        ProviderName = "test-provider",
        ModelName = Model,
        PromptTemplateId = "fixture.concepts",
        PromptTemplateVersion = "1.0.0",
        StartedAt = _clock.UtcNow,
        CompletedAt = _clock.UtcNow,
        LatencyMilliseconds = 10,
        InputTokens = inputTokens,
        CorrelationId = Guid.NewGuid(),
    };

    private sealed record ClaimedOperation(Guid OperationId, Guid LeaseToken);

    private async Task<ClaimedOperation> ClaimedAsync()
    {
        Guid operationId;

        using (var scope = _provider.CreateScope())
        {
            Resolve(scope, WorkspaceA, MembershipInA);

            var request = await scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>().RequestAsync(
                new AiOperation
                {
                    WorkspaceId = WorkspaceA,
                    TaskType = AiTaskType.RecipeConcepts,
                    Scope = AiOperationScope.WholeRecipe,
                    Status = AiOperationStatus.Requested,
                    IdempotencyKey = Guid.NewGuid().ToString(),
                    RequestedByMembershipId = MembershipInA,
                    RequestedAt = _clock.UtcNow,
                    StatusChangedAt = _clock.UtcNow,
                    AvailableAt = _clock.UtcNow,
                },
                TestContext.Current.CancellationToken);

            operationId = request.Operation!.Id;
        }

        using var claiming = _provider.CreateScope();
        var claim = await claiming.ServiceProvider.GetRequiredService<AiOperationClaimRepository>()
            .ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);

        Assert.NotNull(claim);

        return new ClaimedOperation(claim.OperationId, claim.LeaseToken);
    }

    private AiProposal Proposal(Guid operationId) => new()
    {
        WorkspaceId = WorkspaceA,
        AiOperationId = operationId,
        OutputSchemaVersion = "fixture.v1",
        PromptTemplateId = "fixture.concepts",
        PromptTemplateVersion = "1.0.0",
        PromptTemplateBodyChecksum = "sha256:abc",
        ProviderName = "test-provider",
        ModelName = Model,
        CreatedAt = _clock.UtcNow,
    };

    private async Task<AccountAiQuotaPeriod> PeriodAsync() => (await AllPeriodsAsync()).Single();

    private async Task<List<AccountAiQuotaPeriod>> AllPeriodsAsync()
    {
        using var scope = _provider.CreateScope();

        // No resolved workspace and no IgnoreQueryFilters: nothing in this module is workspace-owned, so it is
        // readable before a workspace exists. That is the property, not a convenience.
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaPeriods.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<AccountAiQuotaReservation> ReservationAsync(Guid id) =>
        (await AllReservationsAsync()).Single(reservation => reservation.Id == id);

    private async Task<List<AccountAiQuotaReservation>> AllReservationsAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<AccountAiUsageEntry>> LedgerAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiUsageEntries.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId, Guid membershipId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "a" : "b",
            membershipId,
            WorkspaceRole.Owner,
            Account);

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
