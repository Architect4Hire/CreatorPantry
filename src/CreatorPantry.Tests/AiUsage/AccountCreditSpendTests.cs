using System.Data.Common;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// Admission spends the period's allowance first and then the account's grants, oldest first, and settlement,
/// release and expiry return credits to where they came from (PRICE-008/009, B-46, B-50, AP.5.2).
/// </summary>
/// <remarks>
/// <para>
/// SQL Server rather than SQLite, for the reason <see cref="AccountAiQuotaConcurrencyTests"/> gives: under SQLite a
/// row version never moves, so the races this seam exists to survive — two admissions on the last credits, a
/// claw-back during a hold, a settlement against a lapse — could not lose, and the tests would pass proving nothing.
/// <c>Migrate()</c>, so the schema is the one a deployment applies, backfill included.
/// </para>
/// <para>
/// Every hold here is 100 credits: an estimate of 50 a call, held for two calls. A settled charge is one credit an
/// input token, so a test says what a run cost by how many tokens it reports.
/// </para>
/// </remarks>
public sealed class AccountCreditSpendTests : IAsyncLifetime
{
    private const string Sam = "user-sam";

    private const string Alex = "user-alex";

    private const decimal Hold = 100m;

    private const string ImageModel = "test-image";

    private const decimal PerPicture = 50m;

    private static readonly DateTimeOffset Start = new(2037, 3, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly PlatformActor Operator = new(PlatformAuditActorType.OpsClient, "ops-client-1", "acme-support");

    private readonly SqlServerTestDatabase _container = new();

    private readonly MovableClock _clock = new(Start);

    private readonly FailingDrawSave _failure = new();

    private ServiceProvider? _provider;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = Instance(imageSpendCeiling: null);

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.MigrateAsync(Ct);
    }

    /// <summary>A host over the shared database, with a platform image ceiling when one is given (AP.2.7).</summary>
    private ServiceProvider Instance(decimal? imageSpendCeiling)
    {
        // The ops claw-back facade takes an idempotency executor, whose fingerprint key must be real.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Idempotency:FingerprintKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddApplicationTime()
            .AddBillingModule()
            .AddAiUsageModule()
            .AddIdempotency(configuration)
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options =>
            {
                // An account with no quota row of its own has nothing: every allowance here is given on purpose.
                options.DefaultAllowance = 0m;
                options.DefaultTaskEstimate = Hold / 2;
                options.EstimatedCallsPerRun = 2;
                options.ModelRates["test-model"] = new AiQuotaModelRate(1_000_000m, 2_000_000m);
                options.ImageRates[ImageModel] = PerPicture;
                options.PlatformImageSpendPerDay = imageSpendCeiling;
            })
            .Configure<AiImageListPriceOptions>(options => options.Images[ImageModel] = 0.07m)
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlServer(_container.GetConnectionString())
                .AddInterceptors(_failure))
            .BuildServiceProvider(validateScopes: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    // ---- the split -------------------------------------------------------------------------------------------

    /// <summary>
    /// 40 left in the allowance and three grants: the allowance goes first, then the oldest grant, then the next —
    /// whatever order they were inserted in — and the newest is not touched.
    /// </summary>
    [Fact]
    public async Task A_hold_the_allowance_cannot_cover_takes_the_rest_from_grants_oldest_first()
    {
        await GiveAllowanceAsync(Sam, 40);
        var newest = await GrantAsync(Sam, 50, Start.AddDays(-1));
        var oldest = await GrantAsync(Sam, 30, Start.AddDays(-20));
        var middle = await GrantAsync(Sam, 50, Start.AddDays(-10));

        var admission = await AdmitAsync(Sam);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, admission.Outcome);
        Assert.Equal(Hold, admission.Reserved);
        Assert.Equal(0m, admission.Remaining);
        Assert.Equal(70m, admission.PackRemaining);

        var hold = await ReservationAsync(admission.ReservationId!.Value);
        Assert.Equal(Hold, hold.ReservedAmount);
        Assert.Equal(40m, hold.ReservedFromAllowance);

        // The period counts the allowance's part, and only notes what grants paid.
        var period = await PeriodAsync(Sam);
        Assert.Equal(40m, period.Reserved);
        Assert.Equal(60m, period.GrantReserved);

        Assert.Equal(0m, (await GrantRowAsync(oldest)).CreditsRemaining);
        Assert.Equal(20m, (await GrantRowAsync(middle)).CreditsRemaining);
        Assert.Equal(50m, (await GrantRowAsync(newest)).CreditsRemaining);

        var draws = await DrawsAsync();
        Assert.Equal(2, draws.Count);
        Assert.All(draws, draw => Assert.Equal(hold.Id, draw.HoldId));
        Assert.Equal(30m, draws.Single(draw => draw.GrantId == oldest).CreditsHeld);
        Assert.Equal(30m, draws.Single(draw => draw.GrantId == middle).CreditsHeld);
    }

    /// <summary>
    /// The same hold settled at 70: the allowance keeps its 40, the oldest grant keeps its 30, and the 30 the middle
    /// grant gave comes back to that grant — not to whichever is oldest by then.
    /// </summary>
    [Fact]
    public async Task A_settled_charge_keeps_the_allowance_first_and_returns_the_rest_to_the_grant_it_came_from()
    {
        await GiveAllowanceAsync(Sam, 40);
        var oldest = await GrantAsync(Sam, 30, Start.AddDays(-20));
        var middle = await GrantAsync(Sam, 50, Start.AddDays(-10));

        var reservationId = (await AdmitAsync(Sam)).ReservationId!.Value;
        await SettleAsync(reservationId, inputTokens: 70);
        await PostAsync(reservationId);

        var period = await PeriodAsync(Sam);
        Assert.Equal(40m, period.Consumed);
        Assert.Equal(0m, period.Reserved);
        Assert.Equal(0m, period.GrantReserved);

        var hold = await ReservationAsync(reservationId);
        Assert.Equal(70m, hold.SettledAmount);
        Assert.Equal(40m, hold.ChargedToAllowance);

        Assert.Equal(0m, (await GrantRowAsync(oldest)).CreditsRemaining);
        Assert.Equal(50m, (await GrantRowAsync(middle)).CreditsRemaining);

        var draws = await DrawsAsync();
        Assert.All(draws, draw => Assert.NotNull(draw.ResolvedAt));
        Assert.Equal(30m, draws.Single(draw => draw.GrantId == oldest).CreditsSpent);
        Assert.Equal(0m, draws.Single(draw => draw.GrantId == middle).CreditsSpent);
        Assert.All(draws, draw => Assert.Equal(0m, draw.CreditsClawedBack));

        // Posted again — the worker and the sweep both reach for it — nothing moves a second time.
        await PostAsync(reservationId);
        Assert.Equal(50m, (await GrantRowAsync(middle)).CreditsRemaining);
        Assert.Equal(40m, (await PeriodAsync(Sam)).Consumed);
    }

    /// <summary>
    /// A run that cost more than its hold: the grants give what admission took from them and not a credit more, and
    /// the overshoot lands on the allowance, as it did before grants. The next request finds both spent.
    /// </summary>
    [Fact]
    public async Task An_overshoot_lands_on_the_allowance_and_never_on_a_grant()
    {
        await GiveAllowanceAsync(Sam, 40);
        var grant = await GrantAsync(Sam, 60, Start.AddDays(-3));

        var reservationId = (await AdmitAsync(Sam)).ReservationId!.Value;
        await SettleAsync(reservationId, inputTokens: 130);
        await PostAsync(reservationId);

        var hold = await ReservationAsync(reservationId);
        Assert.Equal(130m, hold.SettledAmount);
        Assert.Equal(70m, hold.ChargedToAllowance);
        Assert.Equal(70m, (await PeriodAsync(Sam)).Consumed);

        var stored = await GrantRowAsync(grant);
        Assert.Equal(0m, stored.CreditsRemaining);
        Assert.Equal(60m, Assert.Single(await DrawsAsync()).CreditsSpent);

        var next = await AdmitAsync(Sam);
        Assert.Equal(AiQuotaAdmissionOutcome.Exhausted, next.Outcome);
        Assert.Equal(0m, next.Remaining);
        Assert.Equal(0m, next.PackRemaining);
    }

    /// <summary>
    /// A split hold commits as one: when the save fails after the draw is written — inside the transaction, with the
    /// period and the grant already updated — none of it is kept.
    /// </summary>
    [Fact]
    public async Task A_split_hold_commits_atomically()
    {
        await GiveAllowanceAsync(Sam, 40);
        var grant = await GrantAsync(Sam, 100, Start.AddDays(-3));

        // The period opens in a save of its own, before the hold's; only the hold's save writes a draw, so that is
        // the one that fails.
        _failure.Armed = true;
        var failed = await Assert.ThrowsAnyAsync<Exception>(() => AdmitAsync(Sam));
        _failure.Armed = false;

        // The injected failure and nothing else: a fixture that broke some other way must not pass for this.
        Assert.Contains(Chain(failed), message => message == FailingDrawSave.Message);

        Assert.DoesNotContain(await ReservationsAsync(), hold => hold.AccountId == Sam);
        Assert.Empty(await DrawsAsync());
        Assert.Equal(100m, (await GrantRowAsync(grant)).CreditsRemaining);

        var period = await PeriodAsync(Sam);
        Assert.Equal(0m, period.Reserved);
        Assert.Equal(0m, period.GrantReserved);

        // And the account is not left stuck: the same request goes through once the fault is gone.
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(Sam)).Outcome);
        Assert.Equal(40m, (await GrantRowAsync(grant)).CreditsRemaining);
    }

    /// <summary>All or none: what the allowance and grants together cannot cover is refused, and takes nothing.</summary>
    [Fact]
    public async Task A_request_the_two_together_cannot_cover_is_refused_and_takes_nothing()
    {
        await GiveAllowanceAsync(Sam, 40);
        var grant = await GrantAsync(Sam, 59.9999m, Start.AddDays(-3));

        var refusal = await AdmitAsync(Sam);

        Assert.Equal(AiQuotaAdmissionOutcome.Exhausted, refusal.Outcome);
        Assert.Null(refusal.ReservationId);
        Assert.Equal(40m, refusal.Remaining);
        Assert.Equal(59.9999m, refusal.PackRemaining);

        Assert.Empty(await DrawsAsync());
        Assert.Empty(await ReservationsAsync());
        Assert.Equal(59.9999m, (await GrantRowAsync(grant)).CreditsRemaining);
        Assert.Equal(0m, (await PeriodAsync(Sam)).Reserved);
    }

    /// <summary>
    /// The refusal at the edge (USAGE-006/007) judges the same two sources and reports them apart: the allowance,
    /// which resets, and the pack balance, which does not.
    /// </summary>
    [Fact]
    public async Task Peek_counts_the_pack_balance_and_reports_it_apart_from_the_allowance()
    {
        await GrantAsync(Sam, 100, Start.AddDays(-3));
        await GrantAsync(Alex, 50, Start.AddDays(-3));

        var sam = await PeekAsync(Sam);
        Assert.Equal(AiQuotaStateOutcome.Available, sam.Outcome);
        Assert.Equal(0m, sam.Remaining);
        Assert.Equal(100m, sam.PackRemaining);
        Assert.Equal(Hold, sam.Required);

        var alex = await PeekAsync(Alex);
        Assert.Equal(AiQuotaStateOutcome.Exhausted, alex.Outcome);
        Assert.Equal(0m, alex.Remaining);
        Assert.Equal(50m, alex.PackRemaining);
        Assert.NotNull(alex.ResetsAt);
    }

    /// <summary>
    /// The picture quote splits as admission would: what each source would have left, and how many pictures the two
    /// together cover.
    /// </summary>
    [Fact]
    public async Task The_picture_quote_says_what_each_source_would_have_left()
    {
        await GiveAllowanceAsync(Sam, 30);
        await GrantAsync(Sam, 400, Start.AddDays(-3));
        await GiveAllowanceAsync(Alex, 30);
        await GrantAsync(Alex, 40, Start.AddDays(-3));

        var affordable = await QuoteAsync(Sam, pictures: 2);
        Assert.Equal(AiImageQuoteOutcome.Affordable, affordable.Outcome);
        Assert.Equal(100m, affordable.Total);
        Assert.Equal(0m, affordable.RemainingAfter);
        Assert.Equal(400m, affordable.PackRemaining);
        Assert.Equal(330m, affordable.PackRemainingAfter);
        Assert.Equal(2, affordable.AffordableVariants);

        var exhausted = await QuoteAsync(Alex, pictures: 2);
        Assert.Equal(AiImageQuoteOutcome.Exhausted, exhausted.Outcome);
        Assert.Null(exhausted.RemainingAfter);
        Assert.Null(exhausted.PackRemainingAfter);
        Assert.Equal(40m, exhausted.PackRemaining);
        Assert.Equal(1, exhausted.AffordableVariants);
    }

    // ---- a claw-back during a hold ---------------------------------------------------------------------------

    /// <summary>
    /// A refund claws back a grant while a hold has 100 of it. The claw-back takes the 50 left; the run then settles
    /// at 70, which stays spent; the 30 it did not spend joins the claw-back, never the balance (B-50).
    /// </summary>
    [Fact]
    public async Task A_claw_back_during_a_hold_keeps_what_the_run_spent_and_claws_back_the_rest()
    {
        var grant = await GrantAsync(Sam, 150, Start.AddDays(-3));
        var reservationId = (await AdmitAsync(Sam)).ReservationId!.Value;

        var clawBack = await ClawBackAsync(Sam, grant);
        Assert.True(clawBack.Succeeded);
        Assert.Equal(50m, clawBack.Value!.Grant.CreditsClawedBack);

        await SettleAsync(reservationId, inputTokens: 70);
        await PostAsync(reservationId);

        var stored = await GrantRowAsync(grant);
        Assert.Equal(0m, stored.CreditsRemaining);
        Assert.Equal(80m, stored.CreditsClawedBack);

        // Granted = spent + remaining + clawed back, with nothing negative anywhere.
        var draw = Assert.Single(await DrawsAsync());
        Assert.Equal(70m, draw.CreditsSpent);
        Assert.Equal(30m, draw.CreditsClawedBack);
        Assert.Equal(stored.CreditsGranted, draw.CreditsSpent + stored.CreditsRemaining + stored.CreditsClawedBack);

        Assert.Equal(0m, (await PeriodAsync(Sam)).GrantReserved);
    }

    /// <summary>The same claw-back, and then the run's lease lapses: nothing was spent, so all of it is clawed back.</summary>
    [Fact]
    public async Task A_claw_back_during_a_hold_that_then_lapses_claws_back_the_whole_draw()
    {
        var grant = await GrantAsync(Sam, 150, Start.AddDays(-3));
        var reservationId = (await AdmitAsync(Sam)).ReservationId!.Value;
        await ClawBackAsync(Sam, grant);

        _clock.Advance(AiPolicy.LeaseDuration + TimeSpan.FromMinutes(1));
        await SweepAsync();

        var stored = await GrantRowAsync(grant);
        Assert.Equal(0m, stored.CreditsRemaining);
        Assert.Equal(150m, stored.CreditsClawedBack);

        var draw = Assert.Single(await DrawsAsync());
        Assert.Equal(0m, draw.CreditsSpent);
        Assert.Equal(Hold, draw.CreditsClawedBack);
        Assert.Equal(AiQuotaReservationStatus.Expired, (await ReservationAsync(reservationId)).Status);
    }

    /// <summary>
    /// A claw-back and an admission at once on one grant: whichever wins, the grant's credits are each in exactly one
    /// place — still there, held, or clawed back — and none is negative.
    /// </summary>
    [Fact]
    public async Task A_claw_back_racing_an_admission_never_loses_or_doubles_a_credit()
    {
        var grant = await GrantAsync(Sam, Hold, Start.AddDays(-3));

        var admission = AdmitAsync(Sam);
        var clawBack = ClawBackAsync(Sam, grant);
        await Task.WhenAll(admission, clawBack);

        var stored = await GrantRowAsync(grant);
        var held = (await DrawsAsync()).Sum(draw => draw.CreditsHeld);

        Assert.True(stored.CreditsRemaining >= 0m);
        Assert.Equal(Hold, stored.CreditsRemaining + stored.CreditsClawedBack + held);

        if (admission.Result.Outcome is AiQuotaAdmissionOutcome.Admitted)
        {
            Assert.Equal(Hold, held);
        }
        else
        {
            Assert.Equal(0m, held);
        }
    }

    // ---- release after abandonment ---------------------------------------------------------------------------

    /// <summary>
    /// A worker that never came back: the sweep expires its hold, and the allowance and each grant get back exactly
    /// what they gave.
    /// </summary>
    [Fact]
    public async Task An_abandoned_hold_releases_to_the_allowance_and_to_each_grant_it_drew_on()
    {
        await GiveAllowanceAsync(Sam, 40);
        var first = await GrantAsync(Sam, 30, Start.AddDays(-20));
        var second = await GrantAsync(Sam, 50, Start.AddDays(-10));
        var reservationId = (await AdmitAsync(Sam)).ReservationId!.Value;

        _clock.Advance(AiPolicy.LeaseDuration + TimeSpan.FromMinutes(1));
        await SweepAsync();

        Assert.Equal(AiQuotaReservationStatus.Expired, (await ReservationAsync(reservationId)).Status);

        var period = await PeriodAsync(Sam);
        Assert.Equal(0m, period.Reserved);
        Assert.Equal(0m, period.GrantReserved);
        Assert.Equal(0m, period.Consumed);

        Assert.Equal(30m, (await GrantRowAsync(first)).CreditsRemaining);
        Assert.Equal(50m, (await GrantRowAsync(second)).CreditsRemaining);
        Assert.All(await DrawsAsync(), draw =>
        {
            Assert.Equal(0m, draw.CreditsSpent);
            Assert.NotNull(draw.ResolvedAt);
        });

        // A second sweep finds nothing to give back twice.
        await SweepAsync();
        Assert.Equal(30m, (await GrantRowAsync(first)).CreditsRemaining);
    }

    /// <summary>A picture request whose operation was never queued gives its grants back at once.</summary>
    [Fact]
    public async Task A_picture_hold_released_before_it_was_queued_returns_its_grants()
    {
        var grant = await GrantAsync(Sam, 120, Start.AddDays(-3));

        var admission = await AdmitImageAsync(Sam, pictures: 2);
        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, admission.Outcome);
        Assert.Equal(20m, (await GrantRowAsync(grant)).CreditsRemaining);

        await using (var scope = _provider!.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
                .ReleaseImageAsync(admission.ReservationId!.Value, Ct);
        }

        Assert.Equal(120m, (await GrantRowAsync(grant)).CreditsRemaining);
        Assert.Equal(0m, Assert.Single(await DrawsAsync()).CreditsSpent);
        Assert.Equal(0m, (await PeriodAsync(Sam)).GrantReserved);
    }

    /// <summary>
    /// Two pictures held as 30 from the allowance and 70 from a grant; the provider made one. The allowance keeps its
    /// 30, the grant keeps 20, and the 50 not spent goes back to the grant.
    /// </summary>
    [Fact]
    public async Task A_picture_billed_for_fewer_than_asked_returns_the_rest_to_the_grant()
    {
        await GiveAllowanceAsync(Sam, 30);
        var grant = await GrantAsync(Sam, 100, Start.AddDays(-3));

        var admission = await AdmitImageAsync(Sam, pictures: 2);
        var reservationId = admission.ReservationId!.Value;

        await using (var scope = _provider!.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().StageImageUsageAsync(
                new AiImageUsageServiceModel(
                    reservationId, Guid.NewGuid(), Guid.NewGuid(), 1, 0, 0, _clock.UtcNow, "venice", ImageModel, null),
                Ct);
            await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().SaveChangesAsync(Ct);
        }

        await PostAsync(reservationId);

        var hold = await ReservationAsync(reservationId);
        Assert.Equal(PerPicture, hold.SettledAmount);
        Assert.Equal(30m, hold.ChargedToAllowance);
        Assert.Equal(80m, (await GrantRowAsync(grant)).CreditsRemaining);
        Assert.Equal(20m, Assert.Single(await DrawsAsync()).CreditsSpent);
    }

    // ---- races on the last credits ---------------------------------------------------------------------------

    /// <summary>Five admissions at once, and credits for one of them, all of it in a grant: exactly one is admitted.</summary>
    [Fact]
    public async Task Two_admissions_on_the_last_grant_credits_cannot_both_be_admitted()
    {
        var grant = await GrantAsync(Sam, Hold, Start.AddDays(-3));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => AdmitAsync(Sam)));

        // The first save to land always wins its round, so one is admitted; the rest re-read and are refused, or
        // ran out of retries and requeue — never admitted.
        Assert.Equal(1, outcomes.Count(outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted));
        Assert.All(outcomes, outcome => Assert.Contains(
            outcome.Outcome,
            new[] { AiQuotaAdmissionOutcome.Admitted, AiQuotaAdmissionOutcome.Exhausted, AiQuotaAdmissionOutcome.Busy }));

        Assert.Equal(0m, (await GrantRowAsync(grant)).CreditsRemaining);
        Assert.Equal(Hold, (await DrawsAsync()).Sum(draw => draw.CreditsHeld));
        Assert.Equal(Hold, (await PeriodAsync(Sam)).GrantReserved);
    }

    /// <summary>The same, with the last credits split between the allowance and a grant.</summary>
    [Fact]
    public async Task Two_admissions_on_the_last_split_credits_cannot_both_be_admitted()
    {
        await GiveAllowanceAsync(Sam, 50);
        var grant = await GrantAsync(Sam, 50, Start.AddDays(-3));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => AdmitAsync(Sam)));

        Assert.Equal(1, outcomes.Count(outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted));
        Assert.Equal(0m, (await GrantRowAsync(grant)).CreditsRemaining);

        var period = await PeriodAsync(Sam);
        Assert.Equal(50m, period.Reserved);
        Assert.Equal(50m, period.GrantReserved);
        Assert.Equal(period.Reserved, (await ReservationsAsync()).Sum(hold => hold.ReservedFromAllowance));
    }

    /// <summary>
    /// A settlement and the expiry sweep racing for a hold paid wholly from a grant. Neither moves the allowance, but
    /// both move the period's grant counter, so its row version decides: the grant gets back what was not spent, once.
    /// </summary>
    [Fact]
    public async Task Posting_and_expiry_racing_for_a_hold_paid_from_a_grant_return_it_once()
    {
        var grant = await GrantAsync(Sam, Hold, Start.AddDays(-3));
        var reservationId = (await AdmitAsync(Sam)).ReservationId!.Value;
        await SettleAsync(reservationId, inputTokens: 60);

        _clock.Advance(AiPolicy.LeaseDuration + TimeSpan.FromMinutes(1));
        await Task.WhenAll(PostAsync(reservationId), SweepAsync());

        Assert.Equal(40m, (await GrantRowAsync(grant)).CreditsRemaining);
        Assert.Equal(60m, Assert.Single(await DrawsAsync()).CreditsSpent);

        var period = await PeriodAsync(Sam);
        Assert.Equal(0m, period.GrantReserved);
        Assert.Equal(0m, period.Consumed);
        Assert.Equal(AiQuotaReservationStatus.Settled, (await ReservationAsync(reservationId)).Status);
    }

    /// <summary>
    /// The plan's daily picture cap still holds when grants pay: every hold moves the period, so admissions paid from
    /// grants are serialized against each other as allowance-paid ones are (AP.4.2).
    /// </summary>
    [Fact]
    public async Task The_daily_picture_cap_holds_when_grants_pay()
    {
        await GrantAsync(Sam, 10_000, Start.AddDays(-3));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AdmitImageAsync(Sam, pictures: 1, cap: 2)));
        var admitted = outcomes.Count(outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted);

        Assert.InRange(admitted, 1, 2);
        Assert.Equal(admitted, (await ReservationsAsync()).Sum(hold => hold.ImagesRequested ?? 0));
    }

    /// <summary>
    /// A picture the account can afford, refused by the platform's ceiling after its grants were judged: nothing is
    /// taken from them, including by a later save in the same scope. The second refusal is the one that matters —
    /// the day is already tripped, so nothing else clears what the split staged.
    /// </summary>
    [Fact]
    public async Task A_picture_refused_by_the_platform_ceiling_takes_nothing_from_grants()
    {
        var grant = await GrantAsync(Sam, 100, Start.AddDays(-3));

        await using var ceiling = Instance(imageSpendCeiling: 0.01m);
        await using var scope = ceiling.CreateAsyncScope();
        var quota = scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>();

        for (var request = 0; request < 2; request++)
        {
            var refusal = await quota.AdmitImageAsync(
                new AiImageQuotaAdmissionRequestServiceModel(
                    Sam, Guid.NewGuid(), 1, ImageModel, null, null, _clock.UtcNow + TimeSpan.FromHours(6)),
                Ct);

            Assert.Equal(AiQuotaAdmissionOutcome.PlatformCeiling, refusal.Outcome);
        }

        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().SaveChangesAsync(Ct);

        Assert.Equal(100m, (await GrantRowAsync(grant)).CreditsRemaining);
        Assert.Empty(await DrawsAsync());
        Assert.Empty(await ReservationsAsync());
        Assert.Equal(0m, (await PeriodAsync(Sam)).GrantReserved);
    }

    /// <summary>
    /// A period counted in tokens spends its allowance only: grants are credits, and the two units do not mix. It
    /// neither draws on them nor reports them as something it could.
    /// </summary>
    [Fact]
    public async Task A_period_counted_in_tokens_never_draws_on_grants()
    {
        await GiveAllowanceAsync(Sam, 0, AiQuotaUnit.Tokens);
        var grant = await GrantAsync(Sam, 1_000_000, Start.AddDays(-3));

        var refusal = await AdmitAsync(Sam);
        Assert.Equal(AiQuotaAdmissionOutcome.Exhausted, refusal.Outcome);
        Assert.Equal(AiQuotaUnit.Tokens, refusal.Unit);
        Assert.Equal(0m, refusal.PackRemaining);

        var peek = await PeekAsync(Sam);
        Assert.Equal(AiQuotaStateOutcome.Exhausted, peek.Outcome);
        Assert.Equal(0m, peek.PackRemaining);

        Assert.Equal(1_000_000m, (await GrantRowAsync(grant)).CreditsRemaining);
        Assert.Empty(await DrawsAsync());
    }

    // ---- isolation -------------------------------------------------------------------------------------------

    /// <summary>One account's grants are never spent, counted or reported for another.</summary>
    [Fact]
    public async Task Another_accounts_grants_are_never_drawn_on()
    {
        var alexs = await GrantAsync(Alex, 500, Start.AddDays(-3));

        var sam = await AdmitAsync(Sam);
        Assert.Equal(AiQuotaAdmissionOutcome.Exhausted, sam.Outcome);
        Assert.Equal(0m, sam.PackRemaining);
        Assert.Equal(0m, (await PeekAsync(Sam)).PackRemaining);

        Assert.Equal(500m, (await GrantRowAsync(alexs)).CreditsRemaining);
        Assert.Empty(await DrawsAsync());
    }

    // ---- the draw ledger's schema ----------------------------------------------------------------------------

    public static TheoryData<string, Action<AccountCreditGrantDraw>> InvalidDraws() => new()
    {
        { "a draw of nought", draw => draw.CreditsHeld = 0 },
        { "more spent than held", draw => { draw.CreditsSpent = 30.0001m; draw.ResolvedAt = Start; } },
        { "a negative spend", draw => { draw.CreditsSpent = -1; draw.ResolvedAt = Start; } },
        { "a claw-back from a draw still open", draw => draw.CreditsClawedBack = 5 },
        { "more clawed back than held", draw => { draw.CreditsSpent = 0; draw.CreditsClawedBack = 31; draw.ResolvedAt = Start; } },
        { "resolved with no spend recorded", draw => draw.ResolvedAt = Start },
        { "a spend with no resolution", draw => draw.CreditsSpent = 10 },
        { "resolved before it was held", draw => { draw.CreditsSpent = 10; draw.ResolvedAt = Start.AddTicks(-1); } },
    };

    /// <summary>Each way of recording a draw that does not add up is refused by SQL Server itself.</summary>
    [Theory]
    [MemberData(nameof(InvalidDraws))]
    public async Task The_schema_refuses_a_draw_that_does_not_add_up(string why, Action<AccountCreditGrantDraw> change)
    {
        var grant = await GrantAsync(Sam, 100, Start.AddDays(-3));
        var draw = new AccountCreditGrantDraw
        {
            Id = Guid.NewGuid(),
            GrantId = grant,
            AccountId = Sam,
            HoldId = Guid.NewGuid(),
            CreditsHeld = 30,
            HeldAt = Start,
        };
        change(draw);

        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.AccountCreditGrantDraws.Add(draw);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    /// <summary>
    /// The control for the theory above: the shapes a draw really takes — open, resolved with part returned, and
    /// resolved into a claw-back — are all stored, so the refusals are about the cases and not a schema that
    /// refuses everything.
    /// </summary>
    [Fact]
    public async Task The_schema_stores_the_draws_the_seam_writes()
    {
        var grant = await GrantAsync(Sam, 100, Start.AddDays(-3));

        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountCreditGrantDraws.AddRange(
            new AccountCreditGrantDraw
            {
                Id = Guid.NewGuid(), GrantId = grant, AccountId = Sam, HoldId = Guid.NewGuid(), CreditsHeld = 30, HeldAt = Start,
            },
            new AccountCreditGrantDraw
            {
                Id = Guid.NewGuid(), GrantId = grant, AccountId = Sam, HoldId = Guid.NewGuid(), CreditsHeld = 30, HeldAt = Start,
                CreditsSpent = 12.5m, ResolvedAt = Start,
            },
            new AccountCreditGrantDraw
            {
                Id = Guid.NewGuid(), GrantId = grant, AccountId = Sam, HoldId = Guid.NewGuid(), CreditsHeld = 30, HeldAt = Start,
                CreditsSpent = 10, CreditsClawedBack = 20, ResolvedAt = Start.AddMinutes(5),
            });

        await db.SaveChangesAsync(Ct);

        Assert.Equal(3, (await DrawsAsync()).Count);
    }

    /// <summary>One draw per grant per hold: a hold that took from a grant twice would be two answers to one question.</summary>
    [Fact]
    public async Task A_hold_draws_on_a_grant_once()
    {
        var grant = await GrantAsync(Sam, 100, Start.AddDays(-3));
        var holdId = Guid.NewGuid();

        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        foreach (var _ in Enumerable.Range(0, 2))
        {
            db.AccountCreditGrantDraws.Add(new AccountCreditGrantDraw
            {
                Id = Guid.NewGuid(), GrantId = grant, AccountId = Sam, HoldId = holdId, CreditsHeld = 10, HeldAt = Start,
            });
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync(string account)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitAsync(
            new AiQuotaAdmissionRequestServiceModel(
                account, Guid.NewGuid(), Guid.NewGuid(), AiTaskType.RecipeConcepts, true, _clock.UtcNow + AiPolicy.LeaseDuration),
            Ct);
    }

    private async Task<AiQuotaAdmissionServiceModel> AdmitImageAsync(string account, int pictures, int? cap = null)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitImageAsync(
            new AiImageQuotaAdmissionRequestServiceModel(
                account, Guid.NewGuid(), pictures, ImageModel, null, null, _clock.UtcNow + TimeSpan.FromHours(6), cap),
            Ct);
    }

    private async Task<AiQuotaStateServiceModel> PeekAsync(string account)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
            .PeekAsync(account, AiTaskType.RecipeConcepts, true, Ct);
    }

    private async Task<AiImageQuoteServiceModel> QuoteAsync(string account, int pictures)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>()
            .QuoteImageAsync(account, ImageModel, null, null, pictures, Ct);
    }

    private async Task SettleAsync(Guid reservationId, int inputTokens)
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().StageSettlementAsync(
            reservationId,
            [
                new AiUsageAttemptServiceModel
                {
                    AttemptNumber = 1,
                    OccurredAt = _clock.UtcNow,
                    ProviderName = "test-provider",
                    ModelName = "test-model",
                    InputTokens = inputTokens,
                    OutputTokens = 0,
                    Outcome = AiUsageOutcome.Succeeded,
                    IsBillable = true,
                },
            ],
            Ct);

        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().SaveChangesAsync(Ct);
    }

    private async Task PostAsync(Guid reservationId)
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().PostAsync(reservationId, Ct);
    }

    private async Task SweepAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaMaintenanceFacade>().SweepAsync(Ct);
    }

    private async Task<Domain.Managers.Results.OperationResult<AccountCreditGrantChangeServiceModel>> ClawBackAsync(
        string account, Guid grantId)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAccountCreditGrantFacade>().ClawBackAsync(
            account, grantId, new ClawBackAccountCreditGrantViewModel("Refunded through the bank."), Operator, Ct);
    }

    private async Task GiveAllowanceAsync(string account, decimal allowance, AiQuotaUnit unit = AiQuotaUnit.Credits)
    {
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = account,
            Unit = unit,
            Allowance = allowance,
            PeriodLength = AiQuotaPeriodLength.Monthly,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            EffectiveFrom = Start.AddDays(-30),
            LastChangedAt = Start.AddDays(-30),
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> GrantAsync(string account, decimal credits, DateTimeOffset grantedAt)
    {
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var grant = new AccountCreditGrant
        {
            Id = Guid.NewGuid(),
            AccountId = account,
            Source = CreditGrantSource.Ops,
            CreditsGranted = credits,
            CreditsRemaining = credits,
            GrantedAt = grantedAt,
        };

        db.AccountCreditGrants.Add(grant);
        await db.SaveChangesAsync(Ct);

        return grant.Id;
    }

    private async Task<AccountCreditGrant> GrantRowAsync(Guid grantId)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountCreditGrants.AsNoTracking().SingleAsync(grant => grant.Id == grantId, Ct);
    }

    private async Task<List<AccountCreditGrantDraw>> DrawsAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountCreditGrantDraws.AsNoTracking().ToListAsync(Ct);
    }

    private async Task<AccountAiQuotaPeriod> PeriodAsync(string account)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaPeriods.AsNoTracking().SingleAsync(period => period.AccountId == account, Ct);
    }

    private async Task<AccountAiQuotaReservation> ReservationAsync(Guid reservationId)
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations.AsNoTracking().SingleAsync(hold => hold.Id == reservationId, Ct);
    }

    private async Task<List<AccountAiQuotaReservation>> ReservationsAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations.AsNoTracking().ToListAsync(Ct);
    }

    /// <summary>An exception's message and every inner one's, since EF may wrap what a save threw.</summary>
    private static IEnumerable<string> Chain(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            yield return exception.Message;
        }
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>
    /// Fails a save after the statement that writes a draw has run — inside the transaction, with the period and the
    /// grant already updated in it — so a test can see whether any of the hold survives.
    /// </summary>
    private sealed class FailingDrawSave : DbCommandInterceptor
    {
        public const string Message = "The save failed after the draw was written.";

        public bool Armed { get; set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (WritesDraw(command))
            {
                await result.DisposeAsync();

                throw new InvalidOperationException(Message);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (WritesDraw(command))
            {
                throw new InvalidOperationException(Message);
            }

            return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        private bool WritesDraw(DbCommand command) =>
            Armed
            && command.CommandText.Contains("[AccountCreditGrantDraws]", StringComparison.Ordinal)
            && !command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
    }
}
