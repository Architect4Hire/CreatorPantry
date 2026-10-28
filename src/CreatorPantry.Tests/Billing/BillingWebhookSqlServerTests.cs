using System.Globalization;
using System.Text;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Integration.Stripe;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The payment provider's webhook against the schema a deployment applies (AP.6.2): a delivery verified before it is
/// read, stored once however often it arrives, worked from the outbox, and the plan brought into line with what the
/// provider says now — upgrades at once, downgrades and cancellations at the end of the paid period, a failed
/// renewal holding the plan where it is, and an ended subscription lapsing to the default.
/// </summary>
/// <remarks>
/// <para>
/// SQL Server, because a replay and a race are decided by the unique indexes as SQL Server enforces them. The real
/// verifier, signing with <see cref="StripeSigning"/>; the provider's subscription is scripted, because what it says
/// is the gateway's contract, which <see cref="StripeBillingGatewayTests"/> holds Stripe to.
/// </para>
/// <para>
/// The catalogue is four plans, cheapest first, the trial the default — the shape a real one has.
/// </para>
/// </remarks>
public sealed class BillingWebhookSqlServerTests : IAsyncLifetime
{
    private const string Sam = "user-sam-8f2e";

    private const string SamCustomer = "cus_NffrFeUfNV2Hib";

    private const string Alex = "user-alex-41c0";

    private const string AlexCustomer = "cus_AlexCustomer1";

    private const string SamSubscription = "sub_SamSubscription1";

    private const string AlexSubscription = "sub_AlexSubscription1";

    private static readonly DateTimeOffset Start = new(2036, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly SqlServerTestDatabase _container = new();

    private readonly ScriptedProvider _provider = new();

    private readonly ManualClock _clock = new(Start);

    private ServiceProvider? _services;

    private int _events;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The end of the period being paid for, in whole seconds as Stripe states it.</summary>
    private DateTimeOffset PeriodEnd => Start.AddDays(20);

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock>(_clock)
            .AddBillingModule(new ConfigurationBuilder().Build())
            .AddOutbox()
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()));

        services.PostConfigure<BillingOptions>(billing =>
        {
            billing.DefaultPlan = "trial";
            billing.Plans =
            [
                Plan("trial", 1000, trial: true),
                Plan("creator", 4500),
                Plan("pro", 18000),
                Plan("studio", 36000),
            ];
        });

        services.RemoveAll<IBillingPaymentGateway>();
        services.AddSingleton<IBillingPaymentGateway>(_provider);
        services.RemoveAll<IBillingWebhookVerifier>();
        services.AddSingleton<IBillingWebhookVerifier>(new StripeWebhookVerifier(StripeSigning.Secret, _clock));

        _services = services.BuildServiceProvider(validateScopes: true);

        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await context.Database.MigrateAsync(Ct);

        context.BillingCustomers.AddRange(
            new BillingCustomer { Id = Guid.NewGuid(), AccountId = Sam, ExternalCustomerId = SamCustomer, CreatedAt = Start },
            new BillingCustomer { Id = Guid.NewGuid(), AccountId = Alex, ExternalCustomerId = AlexCustomer, CreatedAt = Start });
        await context.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    // ---- taking a delivery -----------------------------------------------------------------------------------

    /// <summary>Nothing an unverified delivery says is stored or queued — not even its event id.</summary>
    [Fact]
    public async Task A_delivery_that_does_not_verify_stores_and_queues_nothing()
    {
        var body = Event("customer.subscription.updated", SubscriptionObject(SamSubscription), out _);

        var result = await ReceiveAsync(body, StripeSigning.Header(body, _clock.UtcNow, "whsec_not_this_endpoints"));

        Assert.False(result.Succeeded);
        Assert.Equal(BillingWebhookErrors.SignatureInvalid, result.Error!.Code);
        Assert.Equal(0, await CountAsync<BillingProviderEvent>());
        Assert.Equal(0, await CountAsync<OutboxMessage>());
    }

    /// <summary>Stripe's retry, a manual resend, a captured request replayed in the tolerance: one row, one message.</summary>
    [Fact]
    public async Task A_replayed_event_is_stored_and_queued_once()
    {
        var body = Event("customer.subscription.updated", SubscriptionObject(SamSubscription), out var eventId);

        var first = await ReceiveAsync(body);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var second = await ReceiveAsync(body);

        Assert.False(first.Value!.Replayed);
        Assert.True(second.Value!.Replayed);

        var stored = Assert.Single(await AllAsync<BillingProviderEvent>());
        Assert.Equal(eventId, stored.ExternalEventId);
        Assert.Equal(BillingProviderEventStatus.Received, stored.Status);
        Assert.Equal(SamSubscription, stored.SubscriptionReference);
        Assert.Equal(1, await CountAsync<OutboxMessage>());
    }

    /// <summary>Two deliveries of one event at once both read nothing; the index lets one in and the other is a replay.</summary>
    [Fact]
    public async Task Two_deliveries_of_one_event_racing_store_one_row_and_one_message()
    {
        var body = Event("customer.subscription.updated", SubscriptionObject(SamSubscription), out _);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ReceiveAsync(body)));

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Single(results, result => !result.Value!.Replayed);
        Assert.Equal(1, await CountAsync<BillingProviderEvent>());
        Assert.Equal(1, await CountAsync<OutboxMessage>());
    }

    /// <summary>
    /// A save that fails for any reason but a racing delivery is not a replay: it throws, so the route answers 5xx and
    /// Stripe redelivers, rather than 200 for an event nothing stored. Here the schema's own check refuses a type of
    /// spaces the verifier let through.
    /// </summary>
    [Fact]
    public async Task A_save_that_fails_for_another_reason_is_not_answered_as_a_replay()
    {
        var body = Event("   ", SubscriptionObject(SamSubscription), out _);

        await Assert.ThrowsAsync<DbUpdateException>(() => ReceiveAsync(body));

        Assert.Equal(0, await CountAsync<BillingProviderEvent>());
        Assert.Equal(0, await CountAsync<OutboxMessage>());
    }

    /// <summary>
    /// A pack's checkout and a charge are AP.6.3's, an event from the other mode is not this host's, and a type
    /// nobody handles is nobody's: each stored, so a replay of it is still a replay, and none queued.
    /// </summary>
    [Theory]
    [InlineData("checkout.session.completed", "pack", false, BillingProviderEventStatus.Deferred)]
    [InlineData("charge.refunded", "charge", false, BillingProviderEventStatus.Deferred)]
    [InlineData("customer.subscription.updated", "subscription", true, BillingProviderEventStatus.Ignored)]
    [InlineData("customer.updated", "customer", false, BillingProviderEventStatus.Ignored)]
    [InlineData("invoice.paid", "one-off invoice", false, BillingProviderEventStatus.Ignored)]
    public async Task Events_this_prompt_does_not_act_on_are_stored_and_not_queued(
        string type, string shape, bool live, BillingProviderEventStatus expected)
    {
        var item = shape switch
        {
            "pack" => """{"id":"cs_test_pack1","object":"checkout.session","mode":"payment","subscription":null}""",
            "charge" => """{"id":"ch_3MmlLrLkdIwHu7ix0snN0B15","object":"charge"}""",
            "customer" => """{"id":"cus_NffrFeUfNV2Hib","object":"customer"}""",
            "one-off invoice" => """{"id":"in_1OneOff","object":"invoice","parent":null}""",
            _ => SubscriptionObject(SamSubscription),
        };

        var result = await ReceiveAsync(Event(type, item, out _, live));

        Assert.True(result.Succeeded);
        Assert.Equal(expected, Assert.Single(await AllAsync<BillingProviderEvent>()).Status);
        Assert.Equal(0, await CountAsync<OutboxMessage>());
    }

    // ---- the transitions -------------------------------------------------------------------------------------

    /// <summary>A first subscription, from the trial: on the plan bought, now, recorded as the provider's doing.</summary>
    [Fact]
    public async Task A_first_subscription_puts_the_account_on_its_plan_at_once()
    {
        _provider.Set(Subscription(Sam, SamSubscription, "creator"));

        var outcome = await DeliverAndProcessAsync("checkout.session.completed",
            $$"""{"id":"cs_test_sub1","object":"checkout.session","mode":"subscription","subscription":"{{SamSubscription}}"}""");

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.Applied, BillingEventDetails.Changed), outcome);
        var row = Assert.Single(await ChainAsync(Sam));
        Assert.Equal(("creator", AccountPlanSource.Stripe), (row.PlanKey, row.Source));
        Assert.Equal("creator", await InForceAsync(Sam));

        var subscription = Assert.Single(await AllAsync<BillingSubscription>());
        Assert.Equal((Sam, SamSubscription, "si_SamItem1", "creator"), (subscription.AccountId, subscription.ExternalSubscriptionId, subscription.ItemId, subscription.PlanKey));

        var stored = Assert.Single(await AllAsync<BillingProviderEvent>());
        Assert.Equal((BillingProviderEventStatus.Applied, Sam, Start), (stored.Status, stored.AccountId, stored.ProcessedAt));
    }

    /// <summary>B-56: an upgrade lands at once — caps, classes and, at the next admission, the difference in credits.</summary>
    [Fact]
    public async Task An_upgrade_lands_at_once()
    {
        await OnPlanAsync(Sam, "creator");
        _provider.Set(Subscription(Sam, SamSubscription, "pro"));

        await DeliverAndProcessAsync("invoice.paid", InvoiceObject(SamSubscription));

        Assert.Equal(["creator", "pro"], (await ChainAsync(Sam)).Select(row => row.PlanKey));
        Assert.Equal("pro", await InForceAsync(Sam));
    }

    /// <summary>
    /// B-54 through the schedule CreatorPantry makes: the paid plan runs to the period's end, and the lower one is
    /// waiting for it, not in force yet.
    /// </summary>
    [Fact]
    public async Task A_scheduled_downgrade_waits_for_the_end_of_the_paid_period()
    {
        await OnPlanAsync(Sam, "pro");
        _provider.Set(Subscription(Sam, SamSubscription, "pro", next: new BillingScheduledPhase("creator", BillingCycle.Monthly, PeriodEnd)));

        await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        var chain = await ChainAsync(Sam);
        Assert.Equal(["pro", "creator"], chain.Select(row => row.PlanKey));
        Assert.Equal(PeriodEnd, chain[1].EffectiveFrom);
        Assert.Equal("pro", await InForceAsync(Sam));

        // And when the period ends, Stripe moves the price and the row already in force is the answer: nothing new.
        _clock.Advance(TimeSpan.FromDays(21));
        _provider.Set(Subscription(Sam, SamSubscription, "creator", periodEnd: PeriodEnd.AddDays(30)));

        var after = await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        Assert.Equal(BillingEventDetails.Unchanged, after.Detail);
        Assert.Equal("creator", await InForceAsync(Sam));
        Assert.Equal(2, (await ChainAsync(Sam)).Count);
    }

    /// <summary>
    /// A lower price made somewhere CreatorPantry did not offer — link.com, the provider's dashboard — still waits for
    /// the end of the period it was first seen in, so B-54 holds wherever the change was made.
    /// </summary>
    [Fact]
    public async Task A_downgrade_made_elsewhere_still_waits_for_the_end_of_the_paid_period()
    {
        await OnPlanAsync(Sam, "pro");
        _provider.Set(Subscription(Sam, SamSubscription, "creator"));

        await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        var chain = await ChainAsync(Sam);
        Assert.Equal(["pro", "creator"], chain.Select(row => row.PlanKey));
        Assert.Equal(PeriodEnd, chain[1].EffectiveFrom);
        Assert.Equal("pro", await InForceAsync(Sam));
    }

    /// <summary>
    /// The wait keeps what was paid for, so it applies only to a plan the creator paid for: from a trial, an ops plan
    /// or the default — however generous — the plan bought lands at once.
    /// </summary>
    [Fact]
    public async Task A_lower_plan_bought_from_a_plan_nobody_paid_for_lands_at_once()
    {
        await OnPlanAsync(Sam, "studio", AccountPlanSource.Ops);
        _provider.Set(Subscription(Sam, SamSubscription, "creator"));

        await DeliverAndProcessAsync("customer.subscription.created", SubscriptionObject(SamSubscription));

        Assert.Equal("creator", await InForceAsync(Sam));
    }

    /// <summary>A downgrade withdrawn before the period ends takes its scheduled row back off; the paid plan carries on.</summary>
    [Fact]
    public async Task A_withdrawn_downgrade_is_taken_off_the_schedule()
    {
        await OnPlanAsync(Sam, "pro");
        _provider.Set(Subscription(Sam, SamSubscription, "pro", next: new BillingScheduledPhase("creator", BillingCycle.Monthly, PeriodEnd)));
        await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        _clock.Advance(TimeSpan.FromDays(2));
        _provider.Set(Subscription(Sam, SamSubscription, "pro"));
        var outcome = await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        Assert.Equal(BillingEventDetails.Changed, outcome.Detail);
        Assert.Equal(["pro"], (await ChainAsync(Sam)).Select(row => row.PlanKey));
    }

    /// <summary>
    /// An upgrade while a downgrade is waiting: the waiting row goes, and the higher plan lands now — the order a
    /// chain can take, since "now" cannot sit after something scheduled.
    /// </summary>
    [Fact]
    public async Task An_upgrade_while_a_downgrade_is_waiting_replaces_it()
    {
        await OnPlanAsync(Sam, "pro");
        _provider.Set(Subscription(Sam, SamSubscription, "pro", next: new BillingScheduledPhase("creator", BillingCycle.Monthly, PeriodEnd)));
        await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        _clock.Advance(TimeSpan.FromDays(1));
        _provider.Set(Subscription(Sam, SamSubscription, "studio"));
        await DeliverAndProcessAsync("invoice.paid", InvoiceObject(SamSubscription));

        Assert.Equal(["pro", "studio"], (await ChainAsync(Sam)).Select(row => row.PlanKey));
        Assert.Equal("studio", await InForceAsync(Sam));
    }

    /// <summary>Annual to monthly on the same plan is a price change, not a plan change: nothing is scheduled.</summary>
    [Fact]
    public async Task A_change_of_cycle_alone_schedules_nothing()
    {
        await OnPlanAsync(Sam, "creator");
        _provider.Set(Subscription(Sam, SamSubscription, "creator", cycle: BillingCycle.Annual,
            next: new BillingScheduledPhase("creator", BillingCycle.Monthly, PeriodEnd)));

        var outcome = await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        Assert.Equal(BillingEventDetails.Unchanged, outcome.Detail);
        Assert.Single(await ChainAsync(Sam));
    }

    /// <summary>Cancelled at period end: the default is scheduled for then; withdrawn, the schedule goes again.</summary>
    [Fact]
    public async Task A_cancellation_at_period_end_is_scheduled_and_withdrawing_it_unschedules_it()
    {
        await OnPlanAsync(Sam, "creator");
        _provider.Set(Subscription(Sam, SamSubscription, "creator", cancelAt: PeriodEnd));

        await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        var chain = await ChainAsync(Sam);
        Assert.Equal(["creator", null], chain.Select(row => row.PlanKey));
        Assert.Equal(PeriodEnd, chain[1].EffectiveFrom);
        Assert.Equal("creator", await InForceAsync(Sam));

        _clock.Advance(TimeSpan.FromDays(3));
        _provider.Set(Subscription(Sam, SamSubscription, "creator"));
        await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        Assert.Equal(["creator"], (await ChainAsync(Sam)).Select(row => row.PlanKey));
    }

    /// <summary>
    /// The cancellation reaches the period's end and Stripe ends the subscription: the default the schedule put in
    /// place is in force already, and the deletion writes nothing more.
    /// </summary>
    [Fact]
    public async Task A_cancellation_that_reaches_period_end_lapses_the_account_once()
    {
        await OnPlanAsync(Sam, "creator");
        _provider.Set(Subscription(Sam, SamSubscription, "creator", cancelAt: PeriodEnd));
        await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        _clock.Advance(TimeSpan.FromDays(20) + TimeSpan.FromMinutes(1));
        _provider.Set(Subscription(Sam, SamSubscription, "creator", BillingSubscriptionStatus.Canceled, endedAt: PeriodEnd));
        var outcome = await DeliverAndProcessAsync("customer.subscription.deleted", SubscriptionObject(SamSubscription));

        Assert.Equal(BillingEventDetails.Unchanged, outcome.Detail);
        Assert.Equal(["creator", null], (await ChainAsync(Sam)).Select(row => row.PlanKey));
        Assert.Equal("trial", await InForceAsync(Sam));
    }

    /// <summary>An ended subscription — the provider gave up on an unpaid one, or support cancelled it — lapses now.</summary>
    [Theory]
    [InlineData(BillingSubscriptionStatus.Canceled)]
    [InlineData(BillingSubscriptionStatus.Unpaid)]
    [InlineData(BillingSubscriptionStatus.IncompleteExpired)]
    [InlineData(BillingSubscriptionStatus.Paused)]
    public async Task A_subscription_that_has_ended_lapses_the_account_at_once(BillingSubscriptionStatus status)
    {
        await OnPlanAsync(Sam, "pro");
        _provider.Set(Subscription(Sam, SamSubscription, "pro", status, endedAt: _clock.UtcNow));

        await DeliverAndProcessAsync("customer.subscription.deleted", SubscriptionObject(SamSubscription));

        Assert.Equal(["pro", null], (await ChainAsync(Sam)).Select(row => row.PlanKey));
        Assert.Equal("trial", await InForceAsync(Sam));
    }

    /// <summary>
    /// A failed renewal: Stripe retries, the subscription is past due, and the plan holds where it is in either
    /// direction — so an upgrade whose payment failed never lands, and nothing already scheduled moves.
    /// </summary>
    [Fact]
    public async Task A_failed_payment_holds_the_plan_where_it_is()
    {
        await OnPlanAsync(Sam, "creator");
        _provider.Set(Subscription(Sam, SamSubscription, "pro", BillingSubscriptionStatus.PastDue));

        var outcome = await DeliverAndProcessAsync("invoice.payment_failed", InvoiceObject(SamSubscription));

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.Applied, BillingEventDetails.Held), outcome);
        Assert.Equal(["creator"], (await ChainAsync(Sam)).Select(row => row.PlanKey));
        Assert.Equal(BillingSubscriptionStatus.PastDue, Assert.Single(await AllAsync<BillingSubscription>()).Status);
    }

    /// <summary>And when the retried payment succeeds, the plan it was for lands.</summary>
    [Fact]
    public async Task A_failed_payment_later_paid_lands_its_plan()
    {
        await OnPlanAsync(Sam, "creator");
        _provider.Set(Subscription(Sam, SamSubscription, "pro", BillingSubscriptionStatus.PastDue));
        await DeliverAndProcessAsync("invoice.payment_failed", InvoiceObject(SamSubscription));

        _clock.Advance(TimeSpan.FromDays(3));
        _provider.Set(Subscription(Sam, SamSubscription, "pro"));
        await DeliverAndProcessAsync("invoice.paid", InvoiceObject(SamSubscription));

        Assert.Equal("pro", await InForceAsync(Sam));
    }

    /// <summary>A checkout whose first payment has not been taken yet changes nothing until it is.</summary>
    [Fact]
    public async Task A_subscription_awaiting_its_first_payment_changes_nothing()
    {
        _provider.Set(Subscription(Sam, SamSubscription, "creator", BillingSubscriptionStatus.Incomplete));

        var outcome = await DeliverAndProcessAsync("customer.subscription.created", SubscriptionObject(SamSubscription));

        Assert.Equal(BillingEventDetails.Pending, outcome.Detail);
        Assert.Empty(await ChainAsync(Sam));
    }

    // ---- order, replay and ownership -------------------------------------------------------------------------

    /// <summary>
    /// Stripe does not deliver in order. An update made before a cancellation, processed after it, reads the same
    /// cancelled subscription and decides the same thing: the late one changes nothing.
    /// </summary>
    [Fact]
    public async Task An_older_event_processed_after_a_newer_one_changes_nothing()
    {
        await OnPlanAsync(Sam, "creator");
        var older = await DeliverAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));
        var newer = await DeliverAsync("customer.subscription.deleted", SubscriptionObject(SamSubscription));

        // What the provider holds by the time either is processed: the subscription has ended.
        _provider.Set(Subscription(Sam, SamSubscription, "creator", BillingSubscriptionStatus.Canceled, endedAt: _clock.UtcNow));

        var first = await ProcessAsync(newer);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await ProcessAsync(older);

        Assert.Equal(BillingEventDetails.Changed, first.Detail);
        Assert.Equal(BillingEventDetails.Unchanged, second.Detail);
        Assert.Equal(["creator", null], (await ChainAsync(Sam)).Select(row => row.PlanKey));
    }

    /// <summary>
    /// The same the other way round: an upgrade's events arriving before the subscription's own creation event still
    /// end on the plan the provider holds, and the stale one adds no row.
    /// </summary>
    [Fact]
    public async Task Events_arriving_out_of_order_end_on_what_the_provider_holds_now()
    {
        var created = await DeliverAsync("customer.subscription.created", SubscriptionObject(SamSubscription));
        var paid = await DeliverAsync("invoice.paid", InvoiceObject(SamSubscription));

        _provider.Set(Subscription(Sam, SamSubscription, "pro"));

        await ProcessAsync(paid);
        _clock.Advance(TimeSpan.FromSeconds(5));
        var late = await ProcessAsync(created);

        Assert.Equal(BillingEventDetails.Unchanged, late.Detail);
        Assert.Equal(["pro"], (await ChainAsync(Sam)).Select(row => row.PlanKey));
    }

    /// <summary>
    /// The outbox delivers at least once. A message processed twice finds its event settled, reads nothing from the
    /// provider and writes nothing.
    /// </summary>
    [Fact]
    public async Task An_event_processed_twice_does_its_work_once()
    {
        _provider.Set(Subscription(Sam, SamSubscription, "creator"));
        var id = await DeliverAsync("customer.subscription.created", SubscriptionObject(SamSubscription));

        await ProcessAsync(id);
        var reads = _provider.Reads;
        var again = await ProcessAsync(id);

        Assert.Equal(BillingEventDetails.AlreadyProcessed, again.Detail);
        Assert.Equal(reads, _provider.Reads);
        Assert.Single(await ChainAsync(Sam));
    }

    /// <summary>
    /// The account the subscription's metadata names must be the one its customer is. Metadata can be edited in the
    /// provider's dashboard; a mismatch changes nobody's plan and asks for an operator.
    /// </summary>
    [Fact]
    public async Task A_subscription_whose_metadata_names_another_account_changes_nothing()
    {
        _provider.Set(Subscription(Alex, SamSubscription, "studio", customer: SamCustomer));

        var outcome = await DeliverAndProcessAsync("customer.subscription.created", SubscriptionObject(SamSubscription));

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.NeedsAttention, BillingEventDetails.AccountMismatch), outcome);
        Assert.Empty(await ChainAsync(Sam));
        Assert.Empty(await ChainAsync(Alex));
        Assert.Empty(await AllAsync<BillingSubscription>());
    }

    [Fact]
    public async Task A_subscription_for_a_customer_no_account_is_changes_nothing()
    {
        _provider.Set(Subscription(Sam, SamSubscription, "studio", customer: "cus_Stranger1"));

        var outcome = await DeliverAndProcessAsync("customer.subscription.created", SubscriptionObject(SamSubscription));

        Assert.Equal(BillingEventDetails.CustomerUnknown, outcome.Detail);
        Assert.Empty(await ChainAsync(Sam));
    }

    /// <summary>A price no plan sells — made in the provider's dashboard — is never guessed at.</summary>
    [Fact]
    public async Task A_subscription_on_a_price_no_plan_sells_changes_nothing()
    {
        await OnPlanAsync(Sam, "creator");
        _provider.Set(Subscription(Sam, SamSubscription, planKey: null));

        var outcome = await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.NeedsAttention, BillingEventDetails.UnknownPrice), outcome);
        Assert.Equal(["creator"], (await ChainAsync(Sam)).Select(row => row.PlanKey));
    }

    /// <summary>
    /// The provider's events never withdraw an operator's schedule. One in the way is reported, checked before anything
    /// is written, so the chain is exactly as it was.
    /// </summary>
    [Fact]
    public async Task An_ops_scheduled_change_in_the_way_is_left_alone_and_reported()
    {
        await OnPlanAsync(Sam, "creator");
        await OnPlanAsync(Sam, "studio", AccountPlanSource.Ops, at: Start.AddDays(5));
        var before = await ChainAsync(Sam);

        _provider.Set(Subscription(Sam, SamSubscription, "creator", cancelAt: PeriodEnd));
        var outcome = await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.NeedsAttention, BillingEventDetails.OpsChangeScheduled), outcome);
        Assert.Equal(
            before.Select(row => (row.PlanKey, row.EffectiveFrom, row.Source)),
            (await ChainAsync(Sam)).Select(row => (row.PlanKey, row.EffectiveFrom, row.Source)));
    }

    /// <summary>One account's events never touch another's plan, and a second account's subscription is its own record.</summary>
    [Fact]
    public async Task One_accounts_events_never_change_another_accounts_plan()
    {
        await OnPlanAsync(Alex, "pro");
        _provider.Set(Subscription(Alex, AlexSubscription, "pro", customer: AlexCustomer));
        await DeliverAndProcessAsync("customer.subscription.created", SubscriptionObject(AlexSubscription));
        var alexBefore = await ChainAsync(Alex);

        _provider.Set(Subscription(Sam, SamSubscription, "creator"));
        await DeliverAndProcessAsync("customer.subscription.created", SubscriptionObject(SamSubscription));
        _provider.Set(Subscription(Sam, SamSubscription, "creator", BillingSubscriptionStatus.Canceled, endedAt: _clock.UtcNow));
        await DeliverAndProcessAsync("customer.subscription.deleted", SubscriptionObject(SamSubscription));

        Assert.Equal(["creator", null], (await ChainAsync(Sam)).Select(row => row.PlanKey));
        Assert.Equal(alexBefore.Select(row => row.Id), (await ChainAsync(Alex)).Select(row => row.Id));
        Assert.Equal("pro", await InForceAsync(Alex));
    }

    /// <summary>
    /// A second running subscription — two checkouts — is the account's too: a cancelled one's late event cannot lapse
    /// an account the other is still paying for, and the higher plan of the two is the account's.
    /// </summary>
    [Fact]
    public async Task An_account_paying_for_another_subscription_is_not_lapsed_by_one_ending()
    {
        _provider.Set(Subscription(Sam, SamSubscription, "creator"));
        await DeliverAndProcessAsync("customer.subscription.created", SubscriptionObject(SamSubscription));

        const string second = "sub_SamSecond1";
        _provider.Set(Subscription(Sam, second, "pro"));
        await DeliverAndProcessAsync("customer.subscription.created", SubscriptionObject(second));

        // Two running at once: the higher of the two is the account's.
        Assert.Equal("pro", await InForceAsync(Sam));

        _provider.Set(Subscription(Sam, SamSubscription, "creator", BillingSubscriptionStatus.Canceled, endedAt: _clock.UtcNow));
        await DeliverAndProcessAsync("customer.subscription.deleted", SubscriptionObject(SamSubscription));

        Assert.Equal("pro", await InForceAsync(Sam));
        Assert.DoesNotContain(null, (await ChainAsync(Sam)).Select(row => row.PlanKey));
    }

    // ---- when the provider cannot be read ----------------------------------------------------------------------

    /// <summary>
    /// Nothing is decided from an event alone: a provider that is not answering leaves the event waiting and the
    /// message to be retried, and the next attempt applies what the provider says then.
    /// </summary>
    [Fact]
    public async Task A_provider_that_cannot_be_read_leaves_the_event_to_be_retried()
    {
        var id = await DeliverAsync("customer.subscription.created", SubscriptionObject(SamSubscription));
        _provider.Fail(BillingPaymentOutcome.Unavailable);

        await Assert.ThrowsAsync<BillingWebhookRetryException>(() => ProcessAsync(id));

        Assert.Equal(BillingProviderEventStatus.Received, Assert.Single(await AllAsync<BillingProviderEvent>()).Status);
        Assert.Empty(await ChainAsync(Sam));

        _provider.Set(Subscription(Sam, SamSubscription, "creator"));
        Assert.Equal(BillingEventDetails.Changed, (await ProcessAsync(id)).Detail);
    }

    /// <summary>
    /// The real path: the delivery queues a message, the Worker's dispatcher hands it to the handler registered under
    /// its type, and the handler settles the event in a scope of its own.
    /// </summary>
    [Fact]
    public async Task The_outbox_dispatcher_delivers_a_stored_event_to_its_handler()
    {
        _provider.Set(Subscription(Sam, SamSubscription, "pro"));
        await DeliverAsync("customer.subscription.created", SubscriptionObject(SamSubscription));

        using (var scope = _services!.CreateScope())
        {
            var summary = await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchDueAsync(Ct);
            Assert.Equal((1, 1), (summary.Claimed, summary.Completed));
        }

        Assert.Equal(BillingProviderEventStatus.Applied, Assert.Single(await AllAsync<BillingProviderEvent>()).Status);
        Assert.Equal("pro", await InForceAsync(Sam));
    }

    /// <summary>A provider that keeps failing fails the message, so the outbox backs off and finally poisons it in view.</summary>
    [Fact]
    public async Task The_outbox_retries_a_message_whose_provider_is_not_answering()
    {
        await DeliverAsync("customer.subscription.created", SubscriptionObject(SamSubscription));
        _provider.Fail(BillingPaymentOutcome.Throttled);

        using var scope = _services!.CreateScope();
        var summary = await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchDueAsync(Ct);

        Assert.Equal((1, 0, 1), (summary.Claimed, summary.Completed, summary.Retrying));
        var message = Assert.Single(await AllAsync<OutboxMessage>());
        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
        Assert.DoesNotContain(SamCustomer, message.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(Sam, message.PayloadJson, StringComparison.Ordinal);
    }

    // ---- review: the races and the gaps -------------------------------------------------------------------------

    /// <summary>
    /// Two Workers, one subscription. A asks the provider and hears "active"; before A writes, B hears "cancelled",
    /// writes it and lapses the account. A's reading is now the older one: it is not written over B's, and A's retry
    /// reads the provider again — so the cancellation stands rather than the paid plan coming back.
    /// </summary>
    [Fact]
    public async Task A_reading_overtaken_by_another_workers_newer_one_is_not_written_over_it()
    {
        await OnPlanAsync(Sam, "creator");
        _provider.Set(Subscription(Sam, SamSubscription, "creator"));
        var renewal = await DeliverAsync("invoice.paid", InvoiceObject(SamSubscription));
        var deletion = await DeliverAsync("customer.subscription.deleted", SubscriptionObject(SamSubscription));

        _provider.DuringNextRead(async () =>
        {
            _provider.Set(Subscription(Sam, SamSubscription, "creator", BillingSubscriptionStatus.Canceled, endedAt: _clock.UtcNow));
            await ProcessAsync(deletion);
        });

        await Assert.ThrowsAsync<BillingWebhookRetryException>(() => ProcessAsync(renewal));

        Assert.Equal(["creator", null], (await ChainAsync(Sam)).Select(row => row.PlanKey));
        Assert.Equal(BillingSubscriptionStatus.Canceled, Assert.Single(await AllAsync<BillingSubscription>()).Status);

        // The retry reads what the provider says now, and changes nothing.
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(BillingEventDetails.Unchanged, (await ProcessAsync(renewal)).Detail);
        Assert.Equal("trial", await InForceAsync(Sam));
    }

    /// <summary>
    /// An upgrade's proration is invoiced at once. A subscription already on the higher price whose invoice is still
    /// open has not been paid for: the plan holds until <c>invoice.paid</c> says it has been.
    /// </summary>
    [Fact]
    public async Task An_upgrade_whose_invoice_is_still_open_does_not_land_until_it_is_paid()
    {
        await OnPlanAsync(Sam, "creator");
        _provider.Set(Subscription(Sam, SamSubscription, "pro", paid: false));

        await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        Assert.Equal("creator", await InForceAsync(Sam));
        Assert.Single(await ChainAsync(Sam));

        _provider.Set(Subscription(Sam, SamSubscription, "pro"));
        await DeliverAndProcessAsync("invoice.paid", InvoiceObject(SamSubscription));

        Assert.Equal("pro", await InForceAsync(Sam));
    }

    /// <summary>
    /// A subscription takes away only what a subscription gave. An operator's grant made after an old subscription
    /// ended survives that subscription's late deletion.
    /// </summary>
    [Fact]
    public async Task A_late_deletion_does_not_take_away_an_ops_grant_made_since()
    {
        await OnPlanAsync(Sam, "creator");
        await OnPlanAsync(Sam, "studio", AccountPlanSource.Ops);
        _provider.Set(Subscription(Sam, SamSubscription, "creator", BillingSubscriptionStatus.Canceled, endedAt: _clock.UtcNow));

        var outcome = await DeliverAndProcessAsync("customer.subscription.deleted", SubscriptionObject(SamSubscription));

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.Applied, BillingEventDetails.Unchanged), outcome);
        Assert.Equal("studio", await InForceAsync(Sam));
    }

    /// <summary>A status this contract does not know is never guessed at: no record written, no plan changed.</summary>
    [Fact]
    public async Task A_status_this_contract_does_not_know_changes_nothing()
    {
        await OnPlanAsync(Sam, "pro");
        _provider.Set(Subscription(Sam, SamSubscription, "pro", BillingSubscriptionStatus.Unspecified));

        var outcome = await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.NeedsAttention, BillingEventDetails.UnknownStatus), outcome);
        Assert.Equal(["pro"], (await ChainAsync(Sam)).Select(row => row.PlanKey));
        Assert.Empty(await AllAsync<BillingSubscription>());
    }

    /// <summary>
    /// A provider that answers and refuses — no such subscription — will refuse again: settled for an operator, not
    /// retried until the outbox gives up.
    /// </summary>
    [Fact]
    public async Task A_subscription_the_provider_refuses_to_read_is_settled_for_an_operator()
    {
        var outcome = await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject("sub_NobodysSubscription"));

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.NeedsAttention, BillingEventDetails.ProviderRefused), outcome);
        Assert.NotNull(Assert.Single(await AllAsync<BillingProviderEvent>()).ProcessedAt);
    }

    /// <summary>
    /// A downgrade this host scheduled keeps its instant. A later event whose period has moved on — the renewal
    /// anchor changed — does not move the change with it.
    /// </summary>
    [Fact]
    public async Task A_scheduled_downgrade_keeps_its_instant_when_the_period_end_moves()
    {
        await OnPlanAsync(Sam, "pro");
        _provider.Set(Subscription(Sam, SamSubscription, "creator"));
        await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        _provider.Set(Subscription(Sam, SamSubscription, "creator", periodEnd: PeriodEnd.AddDays(30)));
        await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        var chain = await ChainAsync(Sam);
        Assert.Equal(["pro", "creator"], chain.Select(row => row.PlanKey));
        Assert.Equal(PeriodEnd, chain[1].EffectiveFrom);
    }

    /// <summary>Nothing after a subscription's end is its to decide: a phase due after the cancellation is not scheduled.</summary>
    [Fact]
    public async Task A_cancellation_drops_what_the_schedule_had_after_it()
    {
        await OnPlanAsync(Sam, "pro");
        _provider.Set(Subscription(Sam, SamSubscription, "pro", cancelAt: PeriodEnd,
            next: new BillingScheduledPhase("creator", BillingCycle.Monthly, PeriodEnd.AddDays(5))));

        await DeliverAndProcessAsync("customer.subscription.updated", SubscriptionObject(SamSubscription));

        var chain = await ChainAsync(Sam);
        Assert.Equal(["pro", null], chain.Select(row => row.PlanKey));
        Assert.Equal(PeriodEnd, chain[1].EffectiveFrom);
    }

    /// <summary>
    /// A crash after the plan was written and before the event was settled: the redelivered message finds the chain
    /// already as wanted, writes nothing, and settles the event.
    /// </summary>
    [Fact]
    public async Task An_event_whose_settlement_was_lost_is_settled_without_writing_twice()
    {
        _provider.Set(Subscription(Sam, SamSubscription, "pro"));
        var id = await DeliverAsync("customer.subscription.created", SubscriptionObject(SamSubscription));
        await ProcessAsync(id);

        using (var scope = _services!.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var row = await context.BillingProviderEvents.SingleAsync(Ct);
            row.Status = BillingProviderEventStatus.Received;
            row.ProcessedAt = null;
            await context.SaveChangesAsync(Ct);
        }

        _clock.Advance(TimeSpan.FromSeconds(5));
        var again = await ProcessAsync(id);

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.Applied, BillingEventDetails.Unchanged), again);
        Assert.Single(await ChainAsync(Sam));
        Assert.Single(await AllAsync<BillingSubscription>());
    }

    /// <summary>
    /// The route answered 200, so Stripe will not deliver again; a message the outbox gave up on would leave its
    /// event — perhaps the subscription's last — unprocessed for ever. The sweep queues it again, and it lands.
    /// </summary>
    [Fact]
    public async Task An_event_left_unprocessed_is_queued_again_and_lands()
    {
        var id = await DeliverAsync("customer.subscription.created", SubscriptionObject(SamSubscription));

        // Nothing is stranded yet: the first message may still be retrying.
        Assert.Equal((0, 0), await RedriveAsync());

        _clock.Advance(BillingWebhookPolicy.StrandedAfter + TimeSpan.FromSeconds(1));
        Assert.Equal((1, 0), await RedriveAsync());
        Assert.Equal((0, 0), await RedriveAsync());

        var stored = Assert.Single(await AllAsync<BillingProviderEvent>());
        Assert.Equal((1, _clock.UtcNow), (stored.Redrives, stored.RedrivenAt));
        Assert.Equal(2, await CountAsync<OutboxMessage>());

        _provider.Set(Subscription(Sam, SamSubscription, "pro"));
        using (var scope = _services!.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchDueAsync(Ct);
        }

        Assert.Equal(BillingProviderEventStatus.Applied, (await AllAsync<BillingProviderEvent>()).Single(row => row.Id == id).Status);
        Assert.Equal("pro", await InForceAsync(Sam));
    }

    /// <summary>After a day of the provider not answering, the event is left for an operator, said, not dropped.</summary>
    [Fact]
    public async Task An_event_queued_again_for_a_day_is_left_for_an_operator()
    {
        await DeliverAsync("customer.subscription.created", SubscriptionObject(SamSubscription));

        using (var scope = _services!.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            (await context.BillingProviderEvents.SingleAsync(Ct)).Redrives = BillingWebhookPolicy.MaxRedrives;
            await context.SaveChangesAsync(Ct);
        }

        _clock.Advance(BillingWebhookPolicy.StrandedAfter + TimeSpan.FromSeconds(1));

        Assert.Equal((0, 1), await RedriveAsync());
        var stored = Assert.Single(await AllAsync<BillingProviderEvent>());
        Assert.Equal((BillingProviderEventStatus.NeedsAttention, BillingEventDetails.ProviderUnavailable), (stored.Status, stored.Detail));
        Assert.Equal(1, await CountAsync<OutboxMessage>());
    }

    private async Task<(int Requeued, int GivenUp)> RedriveAsync()
    {
        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBillingWebhookFacade>().RedriveStrandedAsync(Ct);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static PlanOption Plan(string key, int credits, bool trial = false) => new()
    {
        Key = key,
        Name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key),
        Credits = credits,
        DailyImageCap = 5,
        RunningImages = 1,
        Period = trial ? PlanAllowancePeriod.Once : PlanAllowancePeriod.Monthly,
        QualityClasses = ["draft", "standard"],
        TrialDays = trial ? 14 : null,
        TrialImageCap = trial ? 10 : null,
    };

    private BillingSubscriptionSnapshot Subscription(
        string accountId,
        string subscriptionId,
        string? planKey,
        BillingSubscriptionStatus status = BillingSubscriptionStatus.Active,
        BillingCycle? cycle = null,
        DateTimeOffset? cancelAt = null,
        DateTimeOffset? endedAt = null,
        BillingScheduledPhase? next = null,
        DateTimeOffset? periodEnd = null,
        string customer = SamCustomer,
        bool paid = true) =>
        new(
            subscriptionId,
            customer,
            accountId,
            IsLiveMode: false,
            status,
            subscriptionId == SamSubscription ? "si_SamItem1" : "si_Other1",
            planKey,
            planKey is null ? BillingCycle.Unspecified : cycle ?? BillingCycle.Monthly,
            periodEnd ?? PeriodEnd,
            cancelAt,
            endedAt,
            next is null ? null : "sub_sched_Sam1",
            next,
            paid);

    private static string SubscriptionObject(string subscriptionId) =>
        $$"""{"id":"{{subscriptionId}}","object":"subscription","status":"active"}""";

    private static string InvoiceObject(string subscriptionId) =>
        """{"id":"in_1MtHbELkdIwHu7ixl4OzzPMv","object":"invoice","parent":{"type":"subscription_details","subscription_details":{"subscription":""" + "\"" + subscriptionId + "\"}}}";

    /// <summary>A Stripe event of <paramref name="type"/> about <paramref name="item"/>, stamped by this test's clock.</summary>
    private string Event(string type, string item, out string eventId, bool live = false)
    {
        eventId = $"evt_Test{Interlocked.Increment(ref _events):D6}";
        return """{"id":""" + "\"" + eventId + "\"" + ""","object":"event","api_version":"2026-09-30.endive","created":""" + StripeSigning.Seconds(_clock.UtcNow)
            + ""","livemode":""" + (live ? "true" : "false") + ""","pending_webhooks":1,"type":""" + "\"" + type + "\"" + ""","data":{"object":""" + item + "}}";
    }

    private async Task<Domain.Managers.Results.OperationResult<BillingWebhookReceiptServiceModel>> ReceiveAsync(
        string body, string? header = null)
    {
        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBillingWebhookFacade>().ReceiveAsync(
            header ?? StripeSigning.Header(body, _clock.UtcNow), Encoding.UTF8.GetBytes(body), Ct);
    }

    /// <summary>Delivers an event and returns its stored row's id.</summary>
    private async Task<Guid> DeliverAsync(string type, string item)
    {
        var result = await ReceiveAsync(Event(type, item, out var eventId));
        Assert.True(result.Succeeded);

        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().BillingProviderEvents
            .Where(row => row.ExternalEventId == eventId)
            .Select(row => row.Id)
            .SingleAsync(Ct);
    }

    private async Task<BillingProviderEventOutcome> ProcessAsync(Guid id)
    {
        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBillingWebhookFacade>().ProcessAsync(id, Ct);
    }

    /// <remarks>
    /// The clock moves a second after, as real time does between deliveries. Two changes on one frozen reading are
    /// a tick apart, so the second would still read as scheduled — true of the clock, never of a running host.
    /// </remarks>
    private async Task<BillingProviderEventOutcome> DeliverAndProcessAsync(string type, string item)
    {
        var id = await DeliverAsync(type, item);
        var outcome = await ProcessAsync(id);
        _clock.Advance(TimeSpan.FromSeconds(1));
        return outcome;
    }

    /// <summary>Puts the account on a plan as an earlier event or an operator would have, a moment before the test's now.</summary>
    private async Task OnPlanAsync(
        string accountId, string planKey, AccountPlanSource source = AccountPlanSource.Stripe, DateTimeOffset? at = null)
    {
        using (var scope = _services!.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IAccountPlanBusiness>()
                .ChangeAsync(accountId, planKey, source, at, audit: null, Ct);
            Assert.Equal(AccountPlanChangeOutcome.Changed, result.Outcome);
        }

        _clock.Advance(TimeSpan.FromSeconds(1));
    }

    private async Task<string> InForceAsync(string accountId)
    {
        using var scope = _services!.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IAccountPlanFacade>().ResolveAsync(accountId, Ct))!.Key;
    }

    private async Task<List<AccountPlan>> ChainAsync(string accountId)
    {
        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AccountPlans
            .AsNoTracking()
            .Where(row => row.AccountId == accountId)
            .OrderBy(row => row.EffectiveFrom)
            .ToListAsync(Ct);
    }

    private async Task<List<T>> AllAsync<T>() where T : class
    {
        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Set<T>().AsNoTracking().ToListAsync(Ct);
    }

    private async Task<int> CountAsync<T>() where T : class
    {
        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Set<T>().CountAsync(Ct);
    }

    private sealed class ManualClock(DateTimeOffset start) : IClock
    {
        private long _ticks = start.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>The provider's subscription as the test says it stands now; every other operation is not this test's.</summary>
    private sealed class ScriptedProvider : IBillingPaymentGateway
    {
        private readonly Dictionary<string, BillingSubscriptionSnapshot> _subscriptions = new(StringComparer.Ordinal);

        private BillingPaymentOutcome? _failure;

        private Func<Task>? _duringNextRead;

        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public BillingPaymentMode Mode => BillingPaymentMode.Test;

        public void Set(BillingSubscriptionSnapshot subscription)
        {
            lock (_subscriptions)
            {
                _failure = null;
                _subscriptions[subscription.ExternalSubscriptionId] = subscription;
            }
        }

        public void Fail(BillingPaymentOutcome outcome)
        {
            lock (_subscriptions)
            {
                _failure = outcome;
            }
        }

        /// <summary>
        /// Runs <paramref name="other"/> after the next read has taken its answer and before it returns it: another
        /// Worker doing its whole work while this one's reading is in flight, so this one returns a stale answer.
        /// </summary>
        public void DuringNextRead(Func<Task> other) => _duringNextRead = other;

        public async Task<BillingSubscriptionRead> GetSubscriptionAsync(string externalSubscriptionId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);

            BillingSubscriptionRead answer;
            lock (_subscriptions)
            {
                answer = _failure is { } failure ? BillingSubscriptionRead.Of(failure)
                    : _subscriptions.TryGetValue(externalSubscriptionId, out var subscription)
                        ? new BillingSubscriptionRead(BillingPaymentOutcome.Succeeded, subscription)
                        : BillingSubscriptionRead.Of(BillingPaymentOutcome.Refused);
            }

            if (Interlocked.Exchange(ref _duringNextRead, null) is { } other)
            {
                await other();
            }

            return answer;
        }

        public Task<BillingCustomerCreation> CreateCustomerAsync(string accountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingRedirect> StartSubscriptionCheckoutAsync(SubscriptionCheckoutRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingRedirect> StartPackCheckoutAsync(PackCheckoutRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingRedirect> OpenPortalAsync(BillingPortalRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingRedirect> StartPlanUpgradeAsync(PlanUpgradeRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingPaymentOutcome> SchedulePlanChangeAsync(ScheduledPlanChangeRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingPaymentOutcome> ReleaseScheduleAsync(
            string externalScheduleId, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
