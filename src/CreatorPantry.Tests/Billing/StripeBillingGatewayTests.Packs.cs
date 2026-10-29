using System.Net;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Tests.Auth;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The adapter's half of a pack (AP.6.3): reading the checkout that bought one, and the refund or dispute that
/// reverses its payment — each read again rather than taken from an event, and each said in the application's terms.
/// </summary>
/// <remarks>
/// The session, charge and dispute bodies are authored to Stripe's objects at the pinned version, using the pack
/// price the stub configures (<c>Fixtures/Stripe/README.md</c>); nothing here calls Stripe.
/// </remarks>
public sealed partial class StripeBillingGatewayTests
{
    private const string PackSession = "cs_test_b22ZZufWQzNY63zpQ6QSNRQhkUpVph4WRmzW0zWJO2znZKdVujZ0N0S22u";

    private const string PackPayment = "pi_3MtwBwLkdIwHu7ix28a3tqPa";

    private const string RefundedCharge = "ch_3MmlLrLkdIwHu7ix0snN0B15";

    private const string Dispute = "du_1MtJUT2eZvKYlo2CNaw2HvEv";

    // ---- reading a pack's checkout -------------------------------------------------------------------------

    /// <summary>
    /// The line items are expanded because a session does not carry them, and the price they name is what says which
    /// pack was paid for.
    /// </summary>
    [Fact]
    public async Task A_packs_checkout_is_read_with_its_line_items_expanded_and_mapped_to_the_catalogue()
    {
        var stripe = new StubStripe(Ok("checkout-session-pack"));

        var read = await stripe.Gateway().GetPackCheckoutAsync(PackSession, Ct);

        var sent = Assert.Single(stripe.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"https://api.stripe.example/v1/checkout/sessions/{PackSession}?expand%5B%5D=line_items", sent.Uri);
        Assert.Equal("2026-09-30.endive", sent.Version);
        Assert.Null(sent.IdempotencyKey);
        Assert.Empty(sent.Form);

        Assert.Equal(BillingPaymentOutcome.Succeeded, read.Outcome);
        Assert.Equal(
            new BillingPackCheckoutSnapshot(
                PackSession,
                Customer,
                Account,
                IsLiveMode: false,
                IsPackPurchase: true,
                IsPaid: true,
                PackKey: "small",
                ExternalPaymentReference: PackPayment),
            read.Checkout);
    }

    /// <summary>
    /// A delayed payment method still settling, or one that failed: nothing is granted on either. Nor on a session
    /// that needed no payment (AP.7.2): no money came in, and a pack is credits paid for.
    /// </summary>
    [Theory]
    [InlineData("paid", true)]
    [InlineData("unpaid", false)]
    [InlineData("no_payment_required", false)]
    public async Task Whether_the_provider_has_the_money_is_read_from_the_payment_status(string status, bool paid)
    {
        var body = Fixture("checkout-session-pack")
            .Replace("\"payment_status\": \"paid\"", $"\"payment_status\": \"{status}\"", StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body));

        Assert.Equal(paid, (await stripe.Gateway().GetPackCheckoutAsync(PackSession, Ct)).Checkout!.IsPaid);
    }

    /// <summary>A price no pack sells is said as none, never guessed at; the webhook then asks for an operator.</summary>
    [Fact]
    public async Task A_price_this_host_does_not_sell_is_read_as_no_pack()
    {
        var body = Fixture("checkout-session-pack")
            .Replace("price_pack_small", "price_made_in_the_dashboard", StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body));

        Assert.Null((await stripe.Gateway().GetPackCheckoutAsync(PackSession, Ct)).Checkout!.PackKey);
    }

    /// <summary>
    /// The metadata's own pack key is not what is granted: a key edited in the provider's dashboard, with the price
    /// left alone, still reads as the pack that was charged for.
    /// </summary>
    [Fact]
    public async Task The_pack_comes_from_the_price_and_not_from_the_metadata()
    {
        var body = Fixture("checkout-session-pack")
            .Replace("\"creatorpantry_pack_key\": \"small\"", "\"creatorpantry_pack_key\": \"large\"", StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body));

        Assert.Equal("small", (await stripe.Gateway().GetPackCheckoutAsync(PackSession, Ct)).Checkout!.PackKey);
    }

    /// <summary>
    /// A subscription's session, and one made in the provider's dashboard with no marker, are not packs: the webhook
    /// grants nothing for either.
    /// </summary>
    [Theory]
    [InlineData("\"mode\": \"payment\"", "\"mode\": \"subscription\"")]
    [InlineData("\"creatorpantry_purchase\": \"pack\"", "\"creatorpantry_purchase\": \"subscription\"")]
    public async Task A_session_that_is_not_a_pack_of_this_hosts_is_read_as_none(string from, string to)
    {
        var body = Fixture("checkout-session-pack");
        Assert.Contains(from, body, StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body.Replace(from, to, StringComparison.Ordinal)));

        Assert.False((await stripe.Gateway().GetPackCheckoutAsync(PackSession, Ct)).Checkout!.IsPackPurchase);
    }

    /// <summary>
    /// Line items that did not come back are read as no pack rather than as some pack: the session the checkout call
    /// itself returns has none, and granting from its metadata alone is exactly what this never does.
    /// </summary>
    [Fact]
    public async Task A_session_whose_line_items_are_not_expanded_names_no_pack()
    {
        var stripe = new StubStripe(Ok("checkout-session"));

        // The session the checkout call itself returns, which is a different session from this file's pack.
        var checkout = (await stripe.Gateway()
            .GetPackCheckoutAsync("cs_test_a11YYufWQzNY63zpQ6QSNRQhkUpVph4WRmzW0zWJO2znZKdVujZ0N0S22u", Ct)).Checkout!;

        Assert.Null(checkout.PackKey);
        Assert.False(checkout.IsPackPurchase);
    }

    /// <summary>Another session's answer, or another mode's, is not an answer to this request.</summary>
    [Theory]
    [InlineData("\"id\": \"cs_test_b22ZZufWQzNY63zpQ6QSNRQhkUpVph4WRmzW0zWJO2znZKdVujZ0N0S22u\"", "\"id\": \"cs_test_SomebodyElses\"")]
    [InlineData("\"livemode\": false,\n  \"metadata\"", "\"livemode\": true,\n  \"metadata\"")]
    [InlineData("\"payment_status\": \"paid\",", "")]
    public async Task A_session_this_contract_cannot_read_is_unreadable(string from, string to)
    {
        var body = Fixture("checkout-session-pack");
        Assert.Contains(from, body, StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body.Replace(from, to, StringComparison.Ordinal)));

        var read = await stripe.Gateway().GetPackCheckoutAsync(PackSession, Ct);

        Assert.Equal(BillingPaymentOutcome.Unreadable, read.Outcome);
        Assert.Null(read.Checkout);
    }

    // ---- reading a reversal --------------------------------------------------------------------------------

    /// <summary>
    /// A refund is read from its charge, with the payment expanded, because the pack marker and the account travel on
    /// the payment rather than on the charge.
    /// </summary>
    [Fact]
    public async Task A_refund_is_read_from_its_charge_with_the_payment_expanded()
    {
        var stripe = new StubStripe(Ok("charge-refunded"));

        var read = await stripe.Gateway().GetPaymentReversalAsync(RefundedCharge, BillingPackReversal.Refund, Ct);

        var sent = Assert.Single(stripe.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"https://api.stripe.example/v1/charges/{RefundedCharge}?expand%5B%5D=payment_intent", sent.Uri);
        Assert.Null(sent.IdempotencyKey);

        Assert.Equal(
            new BillingPaymentReversalSnapshot(
                RefundedCharge,
                IsLiveMode: false,
                BillingPackReversal.Refund,
                PackPayment,
                Account,
                IsPackPurchase: true,
                IsReversed: true),
            read.Reversal);
    }

    /// <summary>A dispute's event names the dispute, not the charge, so the dispute is what is read again.</summary>
    [Fact]
    public async Task A_dispute_is_read_from_the_dispute_and_names_the_payment_it_holds_back()
    {
        var stripe = new StubStripe(Ok("dispute-created"));

        var read = await stripe.Gateway().GetPaymentReversalAsync(Dispute, BillingPackReversal.Dispute, Ct);

        Assert.Equal($"https://api.stripe.example/v1/disputes/{Dispute}?expand%5B%5D=payment_intent", Assert.Single(stripe.Requests).Uri);
        Assert.Equal(
            new BillingPaymentReversalSnapshot(
                Dispute,
                IsLiveMode: false,
                BillingPackReversal.Dispute,
                PackPayment,
                Account,
                IsPackPurchase: true,
                IsReversed: true),
            read.Reversal);
    }

    /// <summary>A partial refund is a reversal: the claw-back takes the whole unspent balance either way (AP.6.3).</summary>
    [Fact]
    public async Task A_partly_refunded_charge_is_read_as_reversed()
    {
        var body = Fixture("charge-refunded")
            .Replace("\"amount_refunded\": 1000", "\"amount_refunded\": 400", StringComparison.Ordinal)
            .Replace("\"refunded\": true", "\"refunded\": false", StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body));

        Assert.True((await stripe.Gateway().GetPaymentReversalAsync(RefundedCharge, BillingPackReversal.Refund, Ct)).Reversal!.IsReversed);
    }

    /// <summary>A charge with nothing refunded takes nothing back, whatever an event said about it.</summary>
    [Fact]
    public async Task A_charge_with_nothing_refunded_is_read_as_not_reversed()
    {
        var body = Fixture("charge-refunded")
            .Replace("\"amount_refunded\": 1000", "\"amount_refunded\": 0", StringComparison.Ordinal)
            .Replace("\"refunded\": true", "\"refunded\": false", StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body));

        Assert.False((await stripe.Gateway().GetPaymentReversalAsync(RefundedCharge, BillingPackReversal.Refund, Ct)).Reversal!.IsReversed);
    }

    /// <summary>
    /// A payment that was not expanded, or one carrying another host's metadata, says no pack: the webhook then reads
    /// it as something else the provider charged for rather than as a pack whose record is missing.
    /// </summary>
    [Fact]
    public async Task A_payment_with_no_pack_marker_is_read_as_no_pack()
    {
        var body = Fixture("charge-refunded").Replace(
            "\"creatorpantry_purchase\": \"pack\"", "\"creatorpantry_purchase\": \"something_else\"", StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body));

        var reversal = (await stripe.Gateway().GetPaymentReversalAsync(RefundedCharge, BillingPackReversal.Refund, Ct)).Reversal!;

        Assert.False(reversal.IsPackPurchase);
        Assert.Equal(PackPayment, reversal.ExternalPaymentReference);
    }

    /// <summary>A charge made outside Checkout has no payment to find a purchase by, and is said as none.</summary>
    [Fact]
    public async Task A_charge_with_no_payment_names_none()
    {
        var body = Fixture("charge-refunded").Replace("\"payment_intent\": {", "\"unused\": {", StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body));

        var reversal = (await stripe.Gateway().GetPaymentReversalAsync(RefundedCharge, BillingPackReversal.Refund, Ct)).Reversal!;

        Assert.Null(reversal.ExternalPaymentReference);
        Assert.False(reversal.IsPackPurchase);
    }

    /// <summary>Another charge's answer, or another mode's, is not an answer to this request.</summary>
    [Theory]
    [InlineData("\"id\": \"ch_3MmlLrLkdIwHu7ix0snN0B15\"", "\"id\": \"ch_SomebodyElses\"")]
    [InlineData("\"livemode\": false", "\"livemode\": true")]
    public async Task A_reversal_this_contract_cannot_read_is_unreadable(string from, string to)
    {
        var body = Fixture("charge-refunded");
        Assert.Contains(from, body, StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body.Replace(from, to, StringComparison.Ordinal)));

        var read = await stripe.Gateway().GetPaymentReversalAsync(RefundedCharge, BillingPackReversal.Refund, Ct);

        Assert.Equal(BillingPaymentOutcome.Unreadable, read.Outcome);
        Assert.Null(read.Reversal);
    }

    /// <summary>Every way the provider can decline a read maps as it does for a subscription (AP.6.2).</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, "error-resource-missing", BillingPaymentOutcome.Refused)]
    [InlineData(HttpStatusCode.Unauthorized, "error-authentication", BillingPaymentOutcome.CredentialRejected)]
    public async Task A_declined_read_maps_to_its_outcome(HttpStatusCode status, string fixture, BillingPaymentOutcome expected)
    {
        var stripe = new StubStripe(Error(status, fixture), Error(status, fixture));

        Assert.Equal(expected, (await stripe.Gateway().GetPackCheckoutAsync(PackSession, Ct)).Outcome);
        Assert.Equal(
            expected,
            (await stripe.Gateway().GetPaymentReversalAsync(RefundedCharge, BillingPackReversal.Refund, Ct)).Outcome);
    }

    // ---- what the caller may ask for ----------------------------------------------------------------------

    /// <summary>A reference that is not an id of the kind asked for never reaches a request path.</summary>
    [Theory]
    [InlineData("sub_1MowQVLkdIwHu7ixeRlqHVzs")]
    [InlineData("cs_")]
    [InlineData("cs_with spaces")]
    public async Task A_session_reference_that_is_not_one_is_refused_before_anything_is_sent(string reference)
    {
        var stripe = new StubStripe();

        await Assert.ThrowsAsync<ArgumentException>(() => stripe.Gateway().GetPackCheckoutAsync(reference, Ct));
        Assert.Empty(stripe.Requests);
    }

    /// <summary>A refund is a charge and a dispute is a dispute; neither takes the other's id.</summary>
    [Theory]
    [InlineData("du_1MtJUT2eZvKYlo2CNaw2HvEv", BillingPackReversal.Refund)]
    [InlineData("ch_3MmlLrLkdIwHu7ix0snN0B15", BillingPackReversal.Dispute)]
    public async Task A_reversal_reference_of_the_other_kind_is_refused(string reference, BillingPackReversal kind)
    {
        var stripe = new StubStripe();

        await Assert.ThrowsAsync<ArgumentException>(() => stripe.Gateway().GetPaymentReversalAsync(reference, kind, Ct));
        Assert.Empty(stripe.Requests);
    }

    /// <summary>A dispute's id is <c>du_</c> today and was <c>dp_</c>; both are ids, and neither is interpreted.</summary>
    [Fact]
    public async Task Either_of_the_providers_dispute_prefixes_is_an_id()
    {
        var body = Fixture("dispute-created").Replace(Dispute, "dp_1MtJUT2eZvKYlo2COlder", StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body));

        var read = await stripe.Gateway().GetPaymentReversalAsync("dp_1MtJUT2eZvKYlo2COlder", BillingPackReversal.Dispute, Ct);

        Assert.Equal(BillingPaymentOutcome.Succeeded, read.Outcome);
    }

    /// <summary>There is no third kind of reversal to ask for.</summary>
    [Fact]
    public async Task A_reversal_must_say_which_kind_it_is()
    {
        var stripe = new StubStripe();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => stripe.Gateway().GetPaymentReversalAsync(RefundedCharge, BillingPackReversal.None, Ct));
        Assert.Empty(stripe.Requests);
    }

    /// <summary>Nothing of the creator, the pack or the credential reaches a log from either read.</summary>
    [Fact]
    public async Task Logs_of_a_pack_read_carry_nothing_of_the_creator_or_the_credential()
    {
        var logs = new CapturingLoggerProvider();
        var stripe = new StubStripe(
            Ok("checkout-session-pack"),
            Ok("charge-refunded"),
            Error(HttpStatusCode.NotFound, "error-resource-missing"));
        var gateway = stripe.Gateway(logs: logs);

        await gateway.GetPackCheckoutAsync(PackSession, Ct);
        await gateway.GetPaymentReversalAsync(RefundedCharge, BillingPackReversal.Refund, Ct);
        await gateway.GetPaymentReversalAsync(Dispute, BillingPackReversal.Dispute, Ct);

        var all = string.Join("\n", logs.Entries);
        foreach (var secret in new[] { SecretKey, Account, Customer, PackSession, PackPayment, "price_pack_small", "small" })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }
    }
}
