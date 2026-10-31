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
/// A credit pack bought, refunded and disputed against the schema a deployment applies (PRICE-008/010, B-50, AP.6.3):
/// a paid checkout granting its pack's credits once however often its events arrive, and a reversal taking back what
/// is unspent of that grant and no more.
/// </summary>
/// <remarks>
/// <para>
/// SQL Server, because granting once and clawing back once are decided by the unique indexes and the row versions as
/// SQL Server enforces them. The real verifier, signing with <see cref="StripeSigning"/>; what the provider says is
/// scripted, because that is the gateway's contract, which <see cref="StripeBillingGatewayTests"/> holds Stripe to.
/// </para>
/// <para>
/// The catalogue is two packs, as a real one has a few: credits from configuration, never from the money.
/// </para>
/// </remarks>
public sealed class BillingPackPurchaseSqlServerTests : IAsyncLifetime
{
    private const string Sam = "user-sam-8f2e";

    private const string SamCustomer = "cus_NffrFeUfNV2Hib";

    private const string Alex = "user-alex-41c0";

    private const string AlexCustomer = "cus_AlexCustomer1";

    private const string Session = "cs_test_SamPack1";

    private const string Payment = "pi_SamPayment1";

    private const string Charge = "ch_SamCharge1";

    private const string DisputeId = "du_SamDispute1";

    /// <summary>What the small pack grants in this test's catalogue.</summary>
    private const decimal SmallPack = 2500m;

    private static readonly DateTimeOffset Start = new(2036, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly SqlServerTestDatabase _container = new();

    private readonly ScriptedProvider _provider = new();

    private readonly ManualClock _clock = new(Start);

    private ServiceProvider? _services;

    private int _events;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
            billing.Packs =
            [
                new CreditPackOption { Key = "small", Credits = (int)SmallPack, Price = 10m },
                new CreditPackOption { Key = "medium", Credits = 7500, Price = 25m },
            ]);

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

    // ---- buying a pack ---------------------------------------------------------------------------------------

    /// <summary>
    /// The credits are the catalogue's for the price that was charged, the grant answers to the checkout, and the
    /// purchase records the payment a refund will arrive about.
    /// </summary>
    [Fact]
    public async Task A_paid_pack_grants_its_credits_and_records_the_purchase()
    {
        _provider.Set(Checkout());

        var outcome = await DeliverAndProcessAsync("checkout.session.completed", SessionObject());

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.Applied, BillingEventDetails.Granted), outcome);

        var grant = Assert.Single(await GrantsAsync());
        Assert.Equal((Sam, CreditGrantSource.Purchase, SmallPack, SmallPack, Session), (grant.AccountId, grant.Source, grant.CreditsGranted, grant.CreditsRemaining, grant.ExternalReferenceId!));
        Assert.Null(grant.ClawedBackAt);

        var purchase = Assert.Single(await PurchasesAsync());
        Assert.Equal((Sam, Session, Payment, "small", SmallPack, grant.Id), (purchase.AccountId, purchase.ExternalSessionId, purchase.ExternalPaymentReference!, purchase.PackKey, purchase.CreditsGranted, purchase.GrantId));
        Assert.Equal((BillingPackReversal.None, 0m), (purchase.Reversal, purchase.CreditsClawedBack));
        Assert.Null(purchase.ReversedAt);

        // The account's pack balance, which is what admission spends after its allowance (AP.5.2).
        Assert.Equal(SmallPack, await BalanceAsync(Sam));
    }

    /// <summary>The provider's retry, a manual resend, a captured delivery replayed: one event row, one grant.</summary>
    [Fact]
    public async Task A_replayed_delivery_grants_once()
    {
        _provider.Set(Checkout());
        var body = Event("checkout.session.completed", SessionObject(), out _);

        Assert.False((await ReceiveAsync(body)).Value!.Replayed);
        Assert.True((await ReceiveAsync(body)).Value!.Replayed);

        using (var scope = _services!.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchDueAsync(Ct);
        }

        Assert.Equal(1, await CountAsync<BillingProviderEvent>());
        Assert.Equal(SmallPack, Assert.Single(await GrantsAsync()).CreditsGranted);
        Assert.Equal(1, await CountAsync<BillingPackPurchase>());
    }

    /// <summary>At-least-once delivery from the outbox: the second attempt finds its work done and does none.</summary>
    [Fact]
    public async Task The_same_event_processed_twice_grants_once()
    {
        _provider.Set(Checkout());
        var id = await DeliverAsync("checkout.session.completed", SessionObject());

        Assert.Equal(BillingEventDetails.Granted, (await ProcessAsync(id)).Detail);
        Assert.Equal(BillingEventDetails.AlreadyProcessed, (await ProcessAsync(id)).Detail);

        Assert.Single(await GrantsAsync());
        Assert.Equal(1, _provider.CheckoutReads);
    }

    /// <summary>
    /// Two events about one checkout — its completion, and its delayed payment settling — are two event rows and one
    /// pack: the second finds the purchase recorded and asks the provider nothing.
    /// </summary>
    [Fact]
    public async Task Two_events_about_one_checkout_grant_once()
    {
        _provider.Set(Checkout());

        Assert.Equal(BillingEventDetails.Granted, (await DeliverAndProcessAsync("checkout.session.completed", SessionObject())).Detail);
        Assert.Equal(BillingEventDetails.Unchanged, (await DeliverAndProcessAsync("checkout.session.async_payment_succeeded", SessionObject())).Detail);

        Assert.Equal(2, await CountAsync<BillingProviderEvent>());
        Assert.Equal(SmallPack, await BalanceAsync(Sam));
        Assert.Equal(1, _provider.CheckoutReads);
    }

    /// <summary>
    /// A delayed payment method that has not settled grants nothing, and a failed one grants nothing ever. The
    /// payment's own success is what grants.
    /// </summary>
    [Fact]
    public async Task A_checkout_whose_payment_has_not_settled_grants_nothing_until_it_does()
    {
        _provider.Set(Checkout(paid: false));

        var pending = await DeliverAndProcessAsync("checkout.session.completed", SessionObject());
        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.Applied, BillingEventDetails.NotPaid), pending);
        Assert.Empty(await GrantsAsync());
        Assert.Empty(await PurchasesAsync());

        var failed = await DeliverAndProcessAsync("checkout.session.async_payment_failed", SessionObject());
        Assert.Equal(BillingEventDetails.NotPaid, failed.Detail);
        Assert.Empty(await GrantsAsync());

        _provider.Set(Checkout());
        Assert.Equal(BillingEventDetails.Granted, (await DeliverAndProcessAsync("checkout.session.async_payment_succeeded", SessionObject())).Detail);
        Assert.Equal(SmallPack, await BalanceAsync(Sam));
    }

    /// <summary>
    /// Metadata can be edited in the provider's dashboard, so it is never believed alone: the account it names must be
    /// the customer's account.
    /// </summary>
    [Fact]
    public async Task A_session_whose_metadata_names_another_account_grants_nothing()
    {
        _provider.Set(Checkout(accountId: Alex));

        var outcome = await DeliverAndProcessAsync("checkout.session.completed", SessionObject());

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.NeedsAttention, BillingEventDetails.AccountMismatch), outcome);
        Assert.Empty(await GrantsAsync(Sam));
        Assert.Empty(await GrantsAsync(Alex));
    }

    /// <summary>A customer no account is: nothing to grant to, and nothing guessed at from the metadata.</summary>
    [Fact]
    public async Task A_session_for_a_customer_no_account_is_grants_nothing()
    {
        _provider.Set(Checkout(customer: "cus_NobodyHere"));

        var outcome = await DeliverAndProcessAsync("checkout.session.completed", SessionObject());

        Assert.Equal(BillingEventDetails.CustomerUnknown, outcome.Detail);
        Assert.Empty(await GrantsAsync());
    }

    /// <summary>A price no pack sells — one made in the provider's dashboard, or a key since removed — is an operator's.</summary>
    [Fact]
    public async Task A_price_no_pack_sells_asks_for_an_operator_and_grants_nothing()
    {
        _provider.Set(Checkout(packKey: null));

        var outcome = await DeliverAndProcessAsync("checkout.session.completed", SessionObject());

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.NeedsAttention, BillingEventDetails.UnknownPrice), outcome);
        Assert.Empty(await GrantsAsync());
        Assert.Equal(Sam, Assert.Single(await AllAsync<BillingProviderEvent>()).AccountId);
    }

    /// <summary>A session somebody made at the provider is not a pack of this host's, and is not an error either.</summary>
    [Fact]
    public async Task A_session_that_is_not_a_pack_of_this_hosts_is_ignored()
    {
        _provider.Set(Checkout(pack: false));

        var outcome = await DeliverAndProcessAsync("checkout.session.completed", SessionObject());

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.Ignored, BillingEventDetails.NotAPack), outcome);
        Assert.Empty(await GrantsAsync());
    }

    /// <summary>A pack event from the provider's other mode is stored, so a replay of it is still a replay, and left alone.</summary>
    [Fact]
    public async Task A_pack_event_from_the_other_mode_is_stored_and_not_queued()
    {
        var result = await ReceiveAsync(Event("checkout.session.completed", SessionObject(), out _, live: true));

        Assert.True(result.Succeeded);
        var stored = Assert.Single(await AllAsync<BillingProviderEvent>());
        Assert.Equal((BillingProviderEventStatus.Ignored, BillingEventDetails.OtherMode), (stored.Status, stored.Detail));
        Assert.Equal(0, await CountAsync<OutboxMessage>());
    }

    /// <summary>A checkout event naming no session has nothing to read again, and says so rather than failing.</summary>
    [Fact]
    public async Task A_checkout_event_naming_no_session_is_ignored()
    {
        var result = await ReceiveAsync(Event("checkout.session.completed", """{"object":"checkout.session","mode":"payment"}""", out _));

        Assert.True(result.Succeeded);
        var stored = Assert.Single(await AllAsync<BillingProviderEvent>());
        Assert.Equal((BillingProviderEventStatus.Ignored, BillingEventDetails.NoCheckout), (stored.Status, stored.Detail));
        Assert.Equal(0, await CountAsync<OutboxMessage>());
    }

    /// <summary>The provider not answering is this host's to fix, not the event's: the message is retried.</summary>
    [Fact]
    public async Task A_provider_that_cannot_be_read_leaves_the_event_to_be_retried()
    {
        _provider.Fail(BillingPaymentOutcome.Unavailable);
        var id = await DeliverAsync("checkout.session.completed", SessionObject());

        await Assert.ThrowsAsync<BillingWebhookRetryException>(() => ProcessAsync(id));

        Assert.Equal(BillingProviderEventStatus.Received, Assert.Single(await AllAsync<BillingProviderEvent>()).Status);
        Assert.Empty(await GrantsAsync());

        _provider.Set(Checkout());
        Assert.Equal(BillingEventDetails.Granted, (await ProcessAsync(id)).Detail);
    }

    // ---- refunds and disputes --------------------------------------------------------------------------------

    /// <summary>
    /// The unspent part, and only that: credits a run has already spent are not recovered, and nothing comes off the
    /// account's period allowance (B-50).
    /// </summary>
    [Fact]
    public async Task A_refund_claws_back_what_is_unspent_after_a_partial_spend()
    {
        await BoughtAsync();
        await SpendAsync(Sam, 400m);
        Assert.Equal(2100m, await BalanceAsync(Sam));

        var at = _clock.UtcNow;
        var outcome = await ReverseAsync("charge.refunded", Charge);

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.Applied, BillingEventDetails.ClawedBack), outcome);

        var grant = Assert.Single(await GrantsAsync());
        Assert.Equal((0m, 2100m, SmallPack), (grant.CreditsRemaining, grant.CreditsClawedBack, grant.CreditsGranted));
        Assert.Equal(at, grant.ClawedBackAt);
        Assert.Equal(0m, await BalanceAsync(Sam));

        // The grant and its payment's record say the same thing about the same moment.
        var purchase = Assert.Single(await PurchasesAsync());
        Assert.Equal((BillingPackReversal.Refund, 2100m, Charge), (purchase.Reversal, purchase.CreditsClawedBack, purchase.ExternalReversalReference!));
        Assert.Equal(at, purchase.ReversedAt);
    }

    /// <summary>A pack already spent is still recorded as taken back, so nothing can spend it again and a look says why.</summary>
    [Fact]
    public async Task A_fully_spent_pack_is_recorded_as_clawed_back_and_takes_nothing()
    {
        await BoughtAsync();
        await SpendAsync(Sam, SmallPack);

        Assert.Equal(BillingEventDetails.ClawedBack, (await ReverseAsync("charge.refunded", Charge)).Detail);

        var grant = Assert.Single(await GrantsAsync());
        Assert.Equal((0m, 0m), (grant.CreditsRemaining, grant.CreditsClawedBack));
        Assert.NotNull(grant.ClawedBackAt);
        Assert.Equal(0m, Assert.Single(await PurchasesAsync()).CreditsClawedBack);
    }

    /// <summary>A dispute names the dispute, not the charge, and finds the same grant through the payment it holds back.</summary>
    [Fact]
    public async Task A_dispute_claws_back_through_the_payment_it_holds_back()
    {
        await BoughtAsync();

        var outcome = await ReverseAsync("charge.dispute.created", DisputeId, BillingPackReversal.Dispute);

        Assert.Equal(BillingEventDetails.ClawedBack, outcome.Detail);
        Assert.Equal(BillingPackReversal.Dispute, Assert.Single(await PurchasesAsync()).Reversal);
        Assert.Equal(0m, await BalanceAsync(Sam));
    }

    /// <summary>A refund and then a dispute on one payment, or either delivered twice: taken back once.</summary>
    [Fact]
    public async Task A_refund_and_then_a_dispute_claw_back_once()
    {
        await BoughtAsync();

        Assert.Equal(BillingEventDetails.ClawedBack, (await ReverseAsync("charge.refunded", Charge)).Detail);
        var again = await ReverseAsync("charge.dispute.created", DisputeId, BillingPackReversal.Dispute);

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.Applied, BillingEventDetails.Unchanged), again);

        var purchase = Assert.Single(await PurchasesAsync());
        Assert.Equal((BillingPackReversal.Refund, SmallPack, Charge), (purchase.Reversal, purchase.CreditsClawedBack, purchase.ExternalReversalReference!));
    }

    /// <summary>One reversal's message redelivered does its work once.</summary>
    [Fact]
    public async Task A_reversal_processed_twice_claws_back_once()
    {
        await BoughtAsync();
        _provider.Set(Reversal(Charge, BillingPackReversal.Refund));
        var id = await DeliverAsync("charge.refunded", ChargeObject());

        Assert.Equal(BillingEventDetails.ClawedBack, (await ProcessAsync(id)).Detail);
        Assert.Equal(BillingEventDetails.AlreadyProcessed, (await ProcessAsync(id)).Detail);

        Assert.Equal(SmallPack, Assert.Single(await GrantsAsync()).CreditsClawedBack);
    }

    /// <summary>
    /// A subscription invoice's refund reverses a payment that bought no pack: B-54 keeps what was paid for, and no
    /// credits are taken back.
    /// </summary>
    [Fact]
    public async Task A_refund_of_something_that_is_not_a_pack_takes_nothing_back()
    {
        await BoughtAsync();
        _provider.Set(Reversal("ch_InvoiceCharge1", BillingPackReversal.Refund, payment: "pi_InvoicePayment1", pack: false));

        var outcome = await DeliverAndProcessAsync("charge.refunded", ChargeObject("ch_InvoiceCharge1"));

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.Ignored, BillingEventDetails.NotAPack), outcome);
        Assert.Equal(SmallPack, await BalanceAsync(Sam));
    }

    /// <summary>A charge made outside a checkout names no payment to find a pack by, and takes nothing.</summary>
    [Fact]
    public async Task A_reversal_naming_no_payment_takes_nothing_back()
    {
        await BoughtAsync();
        _provider.Set(Reversal(Charge, BillingPackReversal.Refund, payment: null));

        Assert.Equal(BillingEventDetails.NotAPack, (await DeliverAndProcessAsync("charge.refunded", ChargeObject())).Detail);
        Assert.Equal(SmallPack, await BalanceAsync(Sam));
    }

    /// <summary>Read again, the provider does not say the money went back. Nothing is taken on an event alone.</summary>
    [Fact]
    public async Task A_reversal_the_provider_does_not_confirm_takes_nothing_back()
    {
        await BoughtAsync();
        _provider.Set(Reversal(Charge, BillingPackReversal.Refund, reversed: false));

        var outcome = await DeliverAndProcessAsync("charge.refunded", ChargeObject());

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.Applied, BillingEventDetails.NotReversed), outcome);
        Assert.Equal(SmallPack, await BalanceAsync(Sam));
        Assert.Equal(BillingPackReversal.None, Assert.Single(await PurchasesAsync()).Reversal);
    }

    /// <summary>
    /// The two events in flight at once, the refund first: it is not guessed at and not dropped — it is retried, and
    /// takes the credits back as soon as the checkout that bought them is recorded.
    /// </summary>
    [Fact]
    public async Task A_refund_arriving_before_its_checkout_is_retried_and_lands_once_the_pack_is_recorded()
    {
        _provider.Set(Checkout());
        _provider.Set(Reversal(Charge, BillingPackReversal.Refund));
        var refund = await DeliverAsync("charge.refunded", ChargeObject());

        await Assert.ThrowsAsync<BillingWebhookRetryException>(() => ProcessAsync(refund));
        Assert.Equal(BillingProviderEventStatus.Received, (await AllAsync<BillingProviderEvent>()).Single(row => row.Id == refund).Status);

        Assert.Equal(BillingEventDetails.Granted, (await DeliverAndProcessAsync("checkout.session.completed", SessionObject())).Detail);
        Assert.Equal(BillingEventDetails.ClawedBack, (await ProcessAsync(refund)).Detail);

        Assert.Equal(0m, await BalanceAsync(Sam));
        Assert.Equal(SmallPack, Assert.Single(await GrantsAsync()).CreditsClawedBack);
    }

    /// <summary>
    /// Credits a hold had open when the grant was clawed back stay spent if the run spent them; the rest joins the
    /// claw-back, never the balance (AP.5.2, B-50).
    /// </summary>
    [Fact]
    public async Task Credits_held_when_the_grant_was_clawed_back_stay_spent_if_the_run_spent_them()
    {
        await BoughtAsync();
        var hold = await HoldAsync(Sam, 1000m);

        await ReverseAsync("charge.refunded", Charge);

        var clawedBack = Assert.Single(await GrantsAsync()).CreditsClawedBack;
        Assert.Equal(1500m, clawedBack);

        await ResolveAsync(Sam, hold, spent: 600m);

        var grant = Assert.Single(await GrantsAsync());
        Assert.Equal((0m, 1900m), (grant.CreditsRemaining, grant.CreditsClawedBack));
        Assert.Equal(0m, await BalanceAsync(Sam));
    }

    /// <summary>
    /// The two facts again (AP.6.2): a payment whose metadata names an account other than the purchase's is not
    /// believed, because metadata can be edited at the provider and a claw-back is not a thing to guess at.
    /// </summary>
    [Fact]
    public async Task A_payment_whose_metadata_names_another_account_claws_back_nothing()
    {
        await BoughtAsync();
        _provider.Set(Reversal(Charge, BillingPackReversal.Refund, accountId: Alex));

        var outcome = await DeliverAndProcessAsync("charge.refunded", ChargeObject());

        Assert.Equal(new BillingProviderEventOutcome(BillingProviderEventStatus.NeedsAttention, BillingEventDetails.AccountMismatch), outcome);
        Assert.Equal(SmallPack, await BalanceAsync(Sam));
    }

    /// <summary>A reversal of one account's pack never reaches another account's credits.</summary>
    [Fact]
    public async Task One_accounts_refund_never_touches_another_accounts_credits()
    {
        await BoughtAsync();
        await BoughtAsync(Alex, AlexCustomer, "cs_test_AlexPack1", "pi_AlexPayment1");

        await ReverseAsync("charge.refunded", Charge);

        Assert.Equal(0m, await BalanceAsync(Sam));
        Assert.Equal(SmallPack, await BalanceAsync(Alex));
        Assert.Null((await GrantsAsync(Alex)).Single().ClawedBackAt);
    }

    /// <summary>
    /// The real path end to end: a delivery queues a message, the Worker's dispatcher hands it to the handler
    /// registered under its type, and the pack is granted in a scope of its own.
    /// </summary>
    [Fact]
    public async Task The_outbox_dispatcher_delivers_a_pack_event_to_its_handler()
    {
        _provider.Set(Checkout());
        await DeliverAsync("checkout.session.completed", SessionObject());

        using (var scope = _services!.CreateScope())
        {
            var summary = await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchDueAsync(Ct);
            Assert.Equal((1, 1), (summary.Claimed, summary.Completed));
        }

        Assert.Equal(BillingProviderEventStatus.Applied, Assert.Single(await AllAsync<BillingProviderEvent>()).Status);
        Assert.Equal(SmallPack, await BalanceAsync(Sam));
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static BillingPackCheckoutSnapshot Checkout(
        string session = Session,
        string? accountId = Sam,
        string? customer = SamCustomer,
        string? packKey = "small",
        string? payment = Payment,
        bool pack = true,
        bool paid = true) =>
        new(session, customer, accountId, IsLiveMode: false, pack, paid, packKey, payment);

    private static BillingPaymentReversalSnapshot Reversal(
        string reference,
        BillingPackReversal kind,
        string? payment = Payment,
        string? accountId = Sam,
        bool pack = true,
        bool reversed = true) =>
        new(reference, IsLiveMode: false, kind, payment, accountId, pack, reversed);

    private static string SessionObject(string session = Session) =>
        $$"""{"id":"{{session}}","object":"checkout.session","mode":"payment","subscription":null}""";

    private static string ChargeObject(string charge = Charge) =>
        $$"""{"id":"{{charge}}","object":"charge","payment_intent":"{{Payment}}"}""";

    private static string DisputeObject() =>
        $$"""{"id":"{{DisputeId}}","object":"dispute","charge":"{{Charge}}","payment_intent":"{{Payment}}"}""";

    /// <summary>A pack bought and granted, as a creator's completed checkout leaves things.</summary>
    private async Task BoughtAsync(
        string accountId = Sam, string customer = SamCustomer, string session = Session, string payment = Payment)
    {
        _provider.Set(Checkout(session, accountId, customer, payment: payment));
        var outcome = await DeliverAndProcessAsync("checkout.session.completed", SessionObject(session));
        Assert.Equal(BillingEventDetails.Granted, outcome.Detail);
    }

    /// <summary>Delivers and processes a reversal of the pack's payment.</summary>
    private async Task<BillingProviderEventOutcome> ReverseAsync(
        string type, string reference, BillingPackReversal kind = BillingPackReversal.Refund)
    {
        _provider.Set(Reversal(reference, kind));

        return await DeliverAndProcessAsync(
            type, kind == BillingPackReversal.Dispute ? DisputeObject() : ChargeObject(reference));
    }

    /// <summary>Spends credits from the account's grants as an AI run does: held, then settled at what it cost.</summary>
    private async Task SpendAsync(string accountId, decimal credits)
    {
        var hold = await HoldAsync(accountId, credits);
        await ResolveAsync(accountId, hold, credits);
    }

    /// <summary>Takes credits for a hold that stays open, as a run in flight does.</summary>
    private async Task<Guid> HoldAsync(string accountId, decimal credits)
    {
        var hold = Guid.NewGuid();

        using var scope = _services!.CreateScope();
        var spend = scope.ServiceProvider.GetRequiredService<IAccountCreditSpendFacade>();
        Assert.True(await spend.StageDrawAsync(accountId, hold, credits, _clock.UtcNow, Ct));
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().SaveChangesAsync(Ct);

        return hold;
    }

    private async Task ResolveAsync(string accountId, Guid hold, decimal spent)
    {
        using var scope = _services!.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAccountCreditSpendFacade>()
            .StageResolveAsync(accountId, hold, spent, _clock.UtcNow, Ct);
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().SaveChangesAsync(Ct);
    }

    private async Task<decimal> BalanceAsync(string accountId)
    {
        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAccountCreditSpendFacade>().BalanceAsync(accountId, Ct);
    }

    private async Task<List<AccountCreditGrant>> GrantsAsync(string accountId = Sam)
    {
        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().AccountCreditGrants
            .AsNoTracking()
            .Where(grant => grant.AccountId == accountId)
            .OrderBy(grant => grant.GrantedAt)
            .ToListAsync(Ct);
    }

    private Task<List<BillingPackPurchase>> PurchasesAsync() => AllAsync<BillingPackPurchase>();

    /// <summary>A Stripe event of <paramref name="type"/> about <paramref name="item"/>, stamped by this test's clock.</summary>
    private string Event(string type, string item, out string eventId, bool live = false)
    {
        eventId = $"evt_Test{Interlocked.Increment(ref _events):D6}";
        return """{"id":""" + "\"" + eventId + "\"" + ""","object":"event","api_version":"2026-09-30.endive","created":""" + StripeSigning.Seconds(_clock.UtcNow)
            + ""","livemode":""" + (live ? "true" : "false") + ""","pending_webhooks":1,"type":""" + "\"" + type + "\"" + ""","data":{"object":""" + item + "}}";
    }

    private async Task<Domain.Managers.Results.OperationResult<BillingWebhookReceiptServiceModel>> ReceiveAsync(string body)
    {
        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBillingWebhookFacade>().ReceiveAsync(
            StripeSigning.Header(body, _clock.UtcNow), Encoding.UTF8.GetBytes(body), Ct);
    }

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

    private async Task<BillingProviderEventOutcome> DeliverAndProcessAsync(string type, string item)
    {
        var id = await DeliverAsync(type, item);
        var outcome = await ProcessAsync(id);
        _clock.Advance(TimeSpan.FromSeconds(1));
        return outcome;
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

    /// <summary>
    /// The provider's checkout and reversal as the test says they stand now. Every other operation is not this test's:
    /// what the adapter makes of Stripe's own bodies is <see cref="StripeBillingGatewayTests"/>'s.
    /// </summary>
    private sealed class ScriptedProvider : IBillingPaymentGateway
    {
        private readonly Dictionary<string, BillingPackCheckoutSnapshot> _checkouts = new(StringComparer.Ordinal);

        private readonly Dictionary<string, BillingPaymentReversalSnapshot> _reversals = new(StringComparer.Ordinal);

        private BillingPaymentOutcome? _failure;

        private int _checkoutReads;

        public int CheckoutReads => Volatile.Read(ref _checkoutReads);

        public BillingPaymentMode Mode => BillingPaymentMode.Test;

        public void Set(BillingPackCheckoutSnapshot checkout)
        {
            lock (_checkouts)
            {
                _failure = null;
                _checkouts[checkout.ExternalSessionId] = checkout;
            }
        }

        public void Set(BillingPaymentReversalSnapshot reversal)
        {
            lock (_checkouts)
            {
                _failure = null;
                _reversals[reversal.ExternalReference] = reversal;
            }
        }

        public void Fail(BillingPaymentOutcome outcome)
        {
            lock (_checkouts)
            {
                _failure = outcome;
            }
        }

        public Task<BillingPackCheckoutRead> GetPackCheckoutAsync(string externalSessionId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _checkoutReads);

            lock (_checkouts)
            {
                return Task.FromResult(_failure is { } failure
                    ? BillingPackCheckoutRead.Of(failure)
                    : _checkouts.TryGetValue(externalSessionId, out var checkout)
                        ? new BillingPackCheckoutRead(BillingPaymentOutcome.Succeeded, checkout)
                        : BillingPackCheckoutRead.Of(BillingPaymentOutcome.Refused));
            }
        }

        public Task<BillingPaymentReversalRead> GetPaymentReversalAsync(
            string externalReference, BillingPackReversal kind, CancellationToken cancellationToken)
        {
            lock (_checkouts)
            {
                return Task.FromResult(_failure is { } failure
                    ? BillingPaymentReversalRead.Of(failure)
                    : _reversals.TryGetValue(externalReference, out var reversal)
                        ? new BillingPaymentReversalRead(BillingPaymentOutcome.Succeeded, reversal with { Kind = kind })
                        : BillingPaymentReversalRead.Of(BillingPaymentOutcome.Refused));
            }
        }

        public Task<BillingSubscriptionRead> GetSubscriptionAsync(string externalSubscriptionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
