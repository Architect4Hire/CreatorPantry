using System.Text;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Integration.Stripe;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// Stripe's webhook signature, checked over the raw bytes before anything in them is read (AP.6.2): what verifies,
/// every way a delivery fails to, and the few fields a verified event is reduced to.
/// </summary>
/// <remarks>
/// Signed with <see cref="StripeSigning"/>, which follows Stripe's documented scheme independently of the verifier.
/// The event bodies are authored to Stripe's event object at the pinned version (<c>Fixtures/Stripe/README.md</c>).
/// </remarks>
public sealed class StripeWebhookVerifierTests
{
    private static readonly DateTimeOffset Now = new(2036, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static StripeWebhookVerifier Verifier(DateTimeOffset? now = null) =>
        new(StripeSigning.Secret, new FixedClock(now ?? Now));

    private static BillingWebhookVerificationResult Verify(string body, string? header, DateTimeOffset? now = null) =>
        Verifier(now).Verify(header, Encoding.UTF8.GetBytes(body));

    // ---- what verifies -----------------------------------------------------------------------------------

    [Fact]
    public void A_delivery_signed_with_the_endpoints_secret_verifies_and_names_its_subscription()
    {
        var body = StripeSigning.Fixture("event-subscription-updated");

        var result = Verify(body, StripeSigning.Header(body, Now));

        Assert.Equal(BillingWebhookVerification.Verified, result.Outcome);
        Assert.Equal(
            new BillingProviderEventNotice(
                "evt_1NG8Du2eZvKYlo2CUI79vXWy",
                "customer.subscription.updated",
                BillingEventKind.SubscriptionChanged,
                IsLiveMode: false,
                DateTimeOffset.FromUnixTimeSeconds(2104848000),
                "sub_1MowQVLkdIwHu7ixeRlqHVzs",
                "sub_1MowQVLkdIwHu7ixeRlqHVzs"),
            result.Notice);
    }

    /// <summary>
    /// While a secret is being rolled Stripe signs once per active secret, so the header carries two <c>v1</c>s and
    /// either may be this host's. A <c>v0</c> beside them is Stripe's test-only scheme and is ignored.
    /// </summary>
    [Fact]
    public void Any_one_v1_signature_of_several_may_match()
    {
        var body = StripeSigning.Fixture("event-subscription-updated");
        var header = $"t={StripeSigning.Seconds(Now)},"
            + $"v1={StripeSigning.V1(body, Now, "whsec_the_old_secret_still_active")},"
            + $"v1={StripeSigning.V1(body, Now)},"
            + $"v0={StripeSigning.V1(body, Now, "whsec_anything")}";

        Assert.Equal(BillingWebhookVerification.Verified, Verify(body, header).Outcome);
    }

    [Fact]
    public void A_signature_inside_the_tolerance_either_side_of_this_hosts_clock_verifies()
    {
        var body = StripeSigning.Fixture("event-subscription-updated");

        Assert.Equal(BillingWebhookVerification.Verified, Verify(body, StripeSigning.Header(body, Now.AddMinutes(-5))).Outcome);
        Assert.Equal(BillingWebhookVerification.Verified, Verify(body, StripeSigning.Header(body, Now.AddMinutes(5))).Outcome);
    }

    // ---- what does not -----------------------------------------------------------------------------------

    public static TheoryData<string?> MalformedHeaders => new()
    {
        null,
        "",
        "   ",
        "v1=" + new string('a', 64),                                   // no timestamp
        "t=2104848000",                                                 // no signature
        "t=2104848000,v1=not-hex",
        "t=2104848000,v1=" + new string('a', 63),                       // one character short
        "t=-5,v1=" + new string('a', 64),
        "t=99999999999999,v1=" + new string('a', 64),                  // past any DateTimeOffset
        "t=2104848000,t=2104848001,v1=" + new string('a', 64),          // which timestamp was signed?
        new string('t', 5000),
    };

    [Theory]
    [MemberData(nameof(MalformedHeaders))]
    public void A_missing_or_malformed_header_is_refused(string? header)
    {
        Assert.Equal(
            BillingWebhookVerification.SignatureInvalid,
            Verify(StripeSigning.Fixture("event-subscription-updated"), header).Outcome);
    }

    [Fact]
    public void A_signature_made_with_another_secret_is_refused()
    {
        var body = StripeSigning.Fixture("event-subscription-updated");

        Assert.Equal(
            BillingWebhookVerification.SignatureInvalid,
            Verify(body, StripeSigning.Header(body, Now, "whsec_somebody_elses_endpoint")).Outcome);
    }

    /// <summary>One byte changed — here a status rewritten — and the signature no longer covers what arrived.</summary>
    [Fact]
    public void A_body_changed_after_it_was_signed_is_refused()
    {
        var body = StripeSigning.Fixture("event-subscription-updated");
        var header = StripeSigning.Header(body, Now);

        Assert.Equal(
            BillingWebhookVerification.SignatureInvalid,
            Verify(body.Replace("\"active\"", "\"canceled\"", StringComparison.Ordinal), header).Outcome);
    }

    /// <summary>The timestamp is signed, so it cannot be refreshed; a captured delivery stops verifying after five minutes.</summary>
    [Fact]
    public void A_signature_older_or_newer_than_the_tolerance_is_refused()
    {
        var body = StripeSigning.Fixture("event-subscription-updated");

        Assert.Equal(BillingWebhookVerification.SignatureInvalid, Verify(body, StripeSigning.Header(body, Now.AddMinutes(-5).AddSeconds(-1))).Outcome);
        Assert.Equal(BillingWebhookVerification.SignatureInvalid, Verify(body, StripeSigning.Header(body, Now.AddMinutes(5).AddSeconds(1))).Outcome);
    }

    /// <summary>Against a downgrade: only <c>v1</c> counts, so a header with a matching <c>v0</c> and nothing else fails.</summary>
    [Fact]
    public void A_header_with_only_another_scheme_is_refused()
    {
        var body = StripeSigning.Fixture("event-subscription-updated");
        var header = $"t={StripeSigning.Seconds(Now)},v0={StripeSigning.V1(body, Now)}";

        Assert.Equal(BillingWebhookVerification.SignatureInvalid, Verify(body, header).Outcome);
    }

    /// <summary>Signed by Stripe and still not an event: told apart from a bad signature, and nothing is invented.</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{\"id\":\"evt_1\",\"type\":\"customer.subscription.updated\",\"livemode\":false}")]
    [InlineData("{\"id\":\"ch_1\",\"type\":\"charge.refunded\",\"livemode\":false,\"created\":1,\"data\":{\"object\":{}}}")]
    public void A_verified_body_that_is_not_an_event_is_unreadable(string body)
    {
        var result = Verify(body, StripeSigning.Header(body, Now));

        Assert.Equal(BillingWebhookVerification.Unreadable, result.Outcome);
        Assert.Null(result.Notice);
    }

    // ---- what an event is about -------------------------------------------------------------------------

    /// <summary>At the pinned version an invoice names its subscription under its parent, not at the top level.</summary>
    [Fact]
    public void An_invoice_names_the_subscription_its_parent_does()
    {
        var notice = Notice("event-invoice-paid");

        Assert.Equal(BillingEventKind.InvoicePaid, notice.Kind);
        Assert.Equal("in_1MtHbELkdIwHu7ixl4OzzPMv", notice.ObjectReference);
        Assert.Equal("sub_1MowQVLkdIwHu7ixeRlqHVzs", notice.SubscriptionReference);
    }

    [Fact]
    public void A_subscription_checkout_names_its_subscription_and_a_packs_names_none()
    {
        var subscription = Notice("event-checkout-subscription");
        var pack = Notice("event-checkout-pack");

        Assert.Equal(BillingEventKind.CheckoutCompleted, subscription.Kind);
        Assert.Equal("sub_1MowQVLkdIwHu7ixeRlqHVzs", subscription.SubscriptionReference);

        Assert.Equal(BillingEventKind.CheckoutCompleted, pack.Kind);
        Assert.Null(pack.SubscriptionReference);
    }

    [Theory]
    [InlineData("invoice.payment_failed", BillingEventKind.InvoicePaymentFailed)]
    [InlineData("invoice.payment_action_required", BillingEventKind.InvoicePaymentFailed)]
    [InlineData("customer.subscription.deleted", BillingEventKind.SubscriptionChanged)]
    [InlineData("customer.subscription.created", BillingEventKind.SubscriptionChanged)]
    [InlineData("charge.refunded", BillingEventKind.ChargeReversed)]
    [InlineData("charge.dispute.created", BillingEventKind.ChargeReversed)]
    [InlineData("customer.updated", BillingEventKind.Other)]
    [InlineData("customer.subscription.trial_will_end", BillingEventKind.Other)]
    public void Each_type_is_read_into_what_it_is_about(string type, BillingEventKind kind)
    {
        var body = StripeSigning.Fixture(type.StartsWith("invoice", StringComparison.Ordinal) ? "event-invoice-paid" : "event-subscription-updated")
            .Replace(type.StartsWith("invoice", StringComparison.Ordinal) ? "\"invoice.paid\"" : "\"customer.subscription.updated\"", $"\"{type}\"", StringComparison.Ordinal);

        var result = Verify(body, StripeSigning.Header(body, Now));

        Assert.Equal(kind, result.Notice!.Kind);
        Assert.Equal(type, result.Notice.EventType);
    }

    /// <summary>
    /// A reference is stored, so only something shaped like Stripe's ids is kept: a subscription field holding text
    /// anybody chose is read as none rather than stored.
    /// </summary>
    [Fact]
    public void A_reference_that_is_not_an_id_is_not_kept()
    {
        var body = StripeSigning.Fixture("event-subscription-updated")
            .Replace("\"id\": \"sub_1MowQVLkdIwHu7ixeRlqHVzs\"", "\"id\": \"sub_<script>alert(1)</script>\"", StringComparison.Ordinal);

        var notice = Verify(body, StripeSigning.Header(body, Now)).Notice!;

        Assert.Null(notice.SubscriptionReference);
        Assert.Null(notice.ObjectReference);
    }

    private static BillingProviderEventNotice Notice(string fixture)
    {
        var body = StripeSigning.Fixture(fixture);
        var result = Verify(body, StripeSigning.Header(body, Now));
        Assert.Equal(BillingWebhookVerification.Verified, result.Outcome);
        return result.Notice!;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
