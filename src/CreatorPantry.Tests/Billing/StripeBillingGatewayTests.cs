using System.Net;
using System.Text;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Integration.Stripe;
using CreatorPantry.Tests.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The Stripe adapter against Stripe's own object shapes (AP.6.1): what each call sends, what is retried and with
/// which key, how every way Stripe can decline maps, and what never reaches a log.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No test here calls Stripe.</strong> The 200 bodies are the example objects in Stripe's API reference, and
/// the error bodies are authored to Stripe's documented error object (<c>Fixtures/Stripe/README.md</c>), all
/// replayed through a stub handler.
/// </para>
/// <para>
/// The key and the account are made up and recognisable, so a leak assertion can look for them by name.
/// </para>
/// </remarks>
public sealed partial class StripeBillingGatewayTests
{
    private const string SecretKey = "rk_test_SECRET_do_not_log_5c19ab";

    private const string Account = "user-sam-8f2e";

    /// <summary>The customer in the customer fixture, and the one the checkout fixture is for.</summary>
    private const string Customer = "cus_NffrFeUfNV2Hib";

    /// <summary>The customer Stripe's own portal-session example is for, kept as Stripe wrote it.</summary>
    private const string PortalCustomer = "cus_NciAYcXfLnqBoz";

    private const string RequestId = "req_8xYz2AbCdEfGh";

    private static readonly DateTimeOffset Now = new(2037, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private static readonly Uri Success = new("https://app.creatorpantry.example/billing/return");

    private static readonly Uri Cancel = new("https://app.creatorpantry.example/billing");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the fixtures ------------------------------------------------------------------------------------

    private static string Fixture(string name)
    {
        var assembly = typeof(StripeBillingGatewayTests).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(candidate => candidate.EndsWith($".{name}.stripe.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Scripted Ok(string fixture) => new(HttpStatusCode.OK, Fixture(fixture));

    private static Scripted Error(HttpStatusCode status, string fixture, string? shouldRetry = null) =>
        new(status, Fixture(fixture), shouldRetry);

    private static SubscriptionCheckoutRequest Subscription(
        string plan = "creator", BillingCycle cycle = BillingCycle.Monthly, Uri? success = null, string key = "purchase-1") =>
        new(Account, Customer, plan, cycle, success ?? Success, Cancel, key);

    private static PackCheckoutRequest Pack(string pack = "small", string key = "purchase-1") =>
        new(Account, Customer, pack, Success, Cancel, key);

    // ---- what is sent ------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_request_is_pinned_to_the_api_version_and_authenticated_by_the_clients_header_only()
    {
        var stripe = new StubStripe(Ok("customer"));

        await stripe.Gateway().CreateCustomerAsync(Account, Ct);

        var sent = Assert.Single(stripe.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.stripe.example/v1/customers", sent.Uri);
        Assert.Equal("2026-09-30.endive", sent.Version);
        Assert.Equal($"Bearer {SecretKey}", sent.Authorization);

        // The key is a header and nowhere else: not in the address, not in the form.
        Assert.DoesNotContain(SecretKey, sent.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain(sent.Form, pair => pair.Value.Contains(SecretKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_customer_carries_the_account_as_metadata_and_nothing_else_about_the_creator()
    {
        var stripe = new StubStripe(Ok("customer"));

        var created = await stripe.Gateway().CreateCustomerAsync(Account, Ct);

        Assert.Equal(new BillingCustomerCreation(BillingPaymentOutcome.Succeeded, Customer, IsLiveMode: false), created);
        Assert.Equal(
            [new KeyValuePair<string, string>("metadata[creatorpantry_account_id]", Account)],
            Assert.Single(stripe.Requests).Form);
    }

    /// <summary>
    /// Derived from the account, so two requests racing to give one account its first customer get one customer
    /// from Stripe (BillingCustomerDataLayer) — and hashed, so the account id is not the key.
    /// </summary>
    [Fact]
    public async Task One_accounts_customer_requests_share_an_idempotency_key_that_does_not_carry_the_account()
    {
        var stripe = new StubStripe(Ok("customer"), Ok("customer"), Ok("customer"));
        var gateway = stripe.Gateway();

        await gateway.CreateCustomerAsync(Account, Ct);
        await gateway.CreateCustomerAsync(Account, Ct);
        await gateway.CreateCustomerAsync("user-alex-41c0", Ct);

        var keys = stripe.Requests.Select(request => request.IdempotencyKey!).ToList();
        Assert.Equal(keys[0], keys[1]);
        Assert.NotEqual(keys[0], keys[2]);
        Assert.StartsWith("cp-customer-v1-", keys[0], StringComparison.Ordinal);
        Assert.DoesNotContain(Account, keys[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Stripe saves a 5xx against its key for at least a day. A key fixed per account would replay that one failure to
    /// every attempt the creator made for as long; the window ends it, and a later attempt is a fresh request.
    /// </summary>
    [Fact]
    public async Task A_failure_saved_against_the_customer_key_is_not_replayed_past_its_window()
    {
        var clock = new ManualClock(Now);
        var stripe = new StubStripe(Error(HttpStatusCode.InternalServerError, "error-api"), Ok("customer"));
        var gateway = stripe.Gateway(clock: clock);

        Assert.Equal(BillingPaymentOutcome.Unavailable, (await gateway.CreateCustomerAsync(Account, Ct)).Outcome);

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(BillingPaymentOutcome.Succeeded, (await gateway.CreateCustomerAsync(Account, Ct)).Outcome);

        Assert.NotEqual(stripe.Requests[0].IdempotencyKey, stripe.Requests[1].IdempotencyKey);
    }

    /// <summary>
    /// The whole call is held to its deadline, attempts and waits together: while Stripe is not answering, a click
    /// costs the deadline, not three attempt timeouts end to end.
    /// </summary>
    [Fact]
    public async Task A_call_ends_at_its_deadline_whatever_attempts_it_had_left()
    {
        var stripe = new StubStripe(Scripted.Hanging, Scripted.Hanging, Scripted.Hanging);

        var redirect = await stripe.Gateway(totalTimeout: TimeSpan.FromMilliseconds(250)).StartPackCheckoutAsync(Pack(), Ct);

        Assert.Equal(BillingPaymentOutcome.Unavailable, redirect.Outcome);
        Assert.Equal(2, stripe.Requests.Count);
    }

    [Fact]
    public async Task A_portal_session_for_another_customer_is_not_handed_on()
    {
        var stripe = new StubStripe(Ok("portal-session"));

        var redirect = await stripe.Gateway().OpenPortalAsync(new BillingPortalRequest(Customer, Cancel), Ct);

        Assert.Equal(BillingPaymentOutcome.Unreadable, redirect.Outcome);
        Assert.Null(redirect.Url);
    }

    [Theory]
    [InlineData(BillingCycle.Monthly, "price_creator_monthly", "monthly")]
    [InlineData(BillingCycle.Annual, "price_creator_annual", "annual")]
    public async Task A_subscription_checkout_sells_the_plans_price_for_its_cycle_under_managed_payments(
        BillingCycle cycle, string price, string cycleName)
    {
        var stripe = new StubStripe(Ok("checkout-session"));

        await stripe.Gateway().StartSubscriptionCheckoutAsync(Subscription(cycle: cycle), Ct);

        var form = Assert.Single(stripe.Requests).Form;
        Assert.Equal("subscription", Value(form, "mode"));
        Assert.Equal(Customer, Value(form, "customer"));
        Assert.Equal(price, Value(form, "line_items[0][price]"));
        Assert.Equal("1", Value(form, "line_items[0][quantity]"));
        Assert.Equal("true", Value(form, "managed_payments[enabled]"));
        Assert.Equal(Account, Value(form, "client_reference_id"));

        // On the session and on the subscription it makes, so every later event names the account (AP.6.2).
        foreach (var prefix in new[] { "metadata", "subscription_data[metadata]" })
        {
            Assert.Equal(Account, Value(form, $"{prefix}[creatorpantry_account_id]"));
            Assert.Equal("subscription", Value(form, $"{prefix}[creatorpantry_purchase]"));
            Assert.Equal("creator", Value(form, $"{prefix}[creatorpantry_plan_key]"));
            Assert.Equal(cycleName, Value(form, $"{prefix}[creatorpantry_billing_cycle]"));
        }

        // Managed Payments is the merchant of record and owns the tax (B-58), so Stripe Tax is never asked for, and
        // no detail of the creator's is sent for Checkout to prefill.
        Assert.DoesNotContain(form, pair => pair.Key.StartsWith("automatic_tax", StringComparison.Ordinal));
        Assert.DoesNotContain(form, pair => pair.Key.Contains("email", StringComparison.Ordinal));
        Assert.DoesNotContain(form, pair => pair.Key.StartsWith("payment_intent_data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_checkout_returns_where_it_was_asked_to_with_the_session_placeholder_stripe_fills_in()
    {
        var stripe = new StubStripe(Ok("checkout-session"), Ok("checkout-session"));
        var gateway = stripe.Gateway();

        await gateway.StartSubscriptionCheckoutAsync(Subscription(), Ct);
        await gateway.StartSubscriptionCheckoutAsync(Subscription(success: new Uri("https://app.creatorpantry.example/billing/return?plan=creator"), key: "purchase-2"), Ct);

        // The braces arrive as braces: Stripe looks for the literal placeholder.
        Assert.Equal("https://app.creatorpantry.example/billing/return?session_id={CHECKOUT_SESSION_ID}", Value(stripe.Requests[0].Form, "success_url"));
        Assert.Equal("https://app.creatorpantry.example/billing/return?plan=creator&session_id={CHECKOUT_SESSION_ID}", Value(stripe.Requests[1].Form, "success_url"));
        Assert.Equal(Cancel.AbsoluteUri, Value(stripe.Requests[0].Form, "cancel_url"));
    }

    [Fact]
    public async Task A_started_checkout_is_the_session_stripe_made()
    {
        var stripe = new StubStripe(Ok("checkout-session"));

        var redirect = await stripe.Gateway().StartSubscriptionCheckoutAsync(Subscription(), Ct);

        Assert.Equal(BillingPaymentOutcome.Succeeded, redirect.Outcome);
        Assert.Equal("cs_test_a11YYufWQzNY63zpQ6QSNRQhkUpVph4WRmzW0zWJO2znZKdVujZ0N0S22u", redirect.SessionReference);
        Assert.Equal("checkout.stripe.com", redirect.Url!.Host);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1679686615), redirect.ExpiresAt);
    }

    [Fact]
    public async Task A_pack_checkout_is_a_one_time_payment_whose_charge_names_the_pack()
    {
        var stripe = new StubStripe(Ok("checkout-session"));

        var redirect = await stripe.Gateway().StartPackCheckoutAsync(Pack("medium"), Ct);

        Assert.Equal(BillingPaymentOutcome.Succeeded, redirect.Outcome);
        var form = Assert.Single(stripe.Requests).Form;
        Assert.Equal("payment", Value(form, "mode"));
        Assert.Equal("price_pack_medium", Value(form, "line_items[0][price]"));
        Assert.Equal("true", Value(form, "managed_payments[enabled]"));

        // On the payment as well, because a refund or a dispute arrives about the charge (AP.6.3).
        foreach (var prefix in new[] { "metadata", "payment_intent_data[metadata]" })
        {
            Assert.Equal(Account, Value(form, $"{prefix}[creatorpantry_account_id]"));
            Assert.Equal("pack", Value(form, $"{prefix}[creatorpantry_purchase]"));
            Assert.Equal("medium", Value(form, $"{prefix}[creatorpantry_pack_key]"));
        }

        Assert.DoesNotContain(form, pair => pair.Key.StartsWith("subscription_data", StringComparison.Ordinal));
    }

    /// <summary>A trial is not bought (B-52); nor is a cycle that is neither, or a key with no price.</summary>
    [Fact]
    public async Task What_is_not_for_sale_is_refused_before_anything_is_sent()
    {
        var stripe = new StubStripe();
        var gateway = stripe.Gateway();

        Assert.Equal(BillingPaymentOutcome.NotOffered, (await gateway.StartSubscriptionCheckoutAsync(Subscription(plan: "trial"), Ct)).Outcome);
        Assert.Equal(BillingPaymentOutcome.NotOffered, (await gateway.StartSubscriptionCheckoutAsync(Subscription(plan: "enterprise"), Ct)).Outcome);
        Assert.Equal(BillingPaymentOutcome.NotOffered, (await gateway.StartSubscriptionCheckoutAsync(Subscription(cycle: BillingCycle.Unspecified), Ct)).Outcome);
        Assert.Equal(BillingPaymentOutcome.NotOffered, (await gateway.StartPackCheckoutAsync(Pack("huge"), Ct)).Outcome);

        Assert.Empty(stripe.Requests);
    }

    /// <summary>
    /// One purchase is one checkout however often it is asked for — and the caller's key is never sent as it is, so
    /// it cannot collide with a key another kind of call derived from the same text.
    /// </summary>
    [Fact]
    public async Task A_checkouts_idempotency_key_follows_the_callers_and_differs_by_operation()
    {
        var stripe = new StubStripe(Ok("checkout-session"), Ok("checkout-session"), Ok("checkout-session"), Ok("checkout-session"));
        var gateway = stripe.Gateway();

        await gateway.StartSubscriptionCheckoutAsync(Subscription(key: "purchase-1"), Ct);
        await gateway.StartSubscriptionCheckoutAsync(Subscription(key: "purchase-1"), Ct);
        await gateway.StartSubscriptionCheckoutAsync(Subscription(key: "purchase-2"), Ct);
        await gateway.StartPackCheckoutAsync(Pack(key: "purchase-1"), Ct);

        var keys = stripe.Requests.Select(request => request.IdempotencyKey!).ToList();
        Assert.Equal(keys[0], keys[1]);
        Assert.NotEqual(keys[0], keys[2]);
        Assert.NotEqual(keys[0], keys[3]);
        Assert.All(keys, key => Assert.NotEqual("purchase-1", key));
        Assert.All(keys, key => Assert.InRange(key.Length, 1, 255));
    }

    [Fact]
    public async Task The_portal_opens_for_the_customer_with_the_configured_portal_and_returns_its_link()
    {
        var stripe = new StubStripe(Ok("portal-session"));

        var redirect = await stripe.Gateway(portalConfiguration: "bpc_1MAhNDLkdIwHu7ixckACO1Jq")
            .OpenPortalAsync(new BillingPortalRequest(PortalCustomer, Cancel), Ct);

        Assert.Equal(BillingPaymentOutcome.Succeeded, redirect.Outcome);
        Assert.Equal("billing.stripe.com", redirect.Url!.Host);
        Assert.Null(redirect.SessionReference);

        var sent = Assert.Single(stripe.Requests);
        Assert.Equal("https://api.stripe.example/v1/billing_portal/sessions", sent.Uri);
        Assert.Equal(
            [
                new KeyValuePair<string, string>("customer", PortalCustomer),
                new KeyValuePair<string, string>("return_url", Cancel.AbsoluteUri),
                new KeyValuePair<string, string>("configuration", "bpc_1MAhNDLkdIwHu7ixckACO1Jq"),
            ],
            sent.Form);
    }

    [Fact]
    public async Task With_no_portal_configuration_the_accounts_default_portal_opens()
    {
        var stripe = new StubStripe(Ok("portal-session"));

        await stripe.Gateway().OpenPortalAsync(new BillingPortalRequest(PortalCustomer, Cancel), Ct);

        Assert.DoesNotContain(Assert.Single(stripe.Requests).Form, pair => pair.Key == "configuration");
    }

    // ---- what is retried, and with which key -------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BillingPaymentOutcome.CredentialRejected)]
    [InlineData(HttpStatusCode.Forbidden, BillingPaymentOutcome.CredentialRejected)]
    [InlineData(HttpStatusCode.BadRequest, BillingPaymentOutcome.Refused)]
    [InlineData(HttpStatusCode.PaymentRequired, BillingPaymentOutcome.Refused)]
    [InlineData(HttpStatusCode.NotFound, BillingPaymentOutcome.Refused)]
    public async Task A_refusal_is_answered_once_and_not_repeated(HttpStatusCode status, BillingPaymentOutcome expected)
    {
        var fixture = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "error-authentication" : "error-resource-missing";
        var stripe = new StubStripe(Error(status, fixture));

        var redirect = await stripe.Gateway().StartSubscriptionCheckoutAsync(Subscription(), Ct);

        Assert.Equal(expected, redirect.Outcome);
        Assert.Null(redirect.Url);
        Assert.Single(stripe.Requests);
    }

    /// <summary>
    /// Stripe saves a 5xx against the idempotency key, so repeating it only replays it — and the outcome is unknown,
    /// which is Unavailable, reconciled from webhooks rather than guessed.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.FailedDependency)]
    public async Task A_failure_on_stripes_side_is_not_retried_and_is_unknown(HttpStatusCode status)
    {
        var stripe = new StubStripe(Error(status, "error-api"));

        var redirect = await stripe.Gateway().StartPackCheckoutAsync(Pack(), Ct);

        Assert.Equal(BillingPaymentOutcome.Unavailable, redirect.Outcome);
        Assert.Single(stripe.Requests);
    }

    [Fact]
    public async Task Stripe_saying_a_failure_should_be_retried_overrides_the_status()
    {
        var stripe = new StubStripe(Error(HttpStatusCode.InternalServerError, "error-api", shouldRetry: "true"), Ok("checkout-session"));

        var redirect = await stripe.Gateway().StartPackCheckoutAsync(Pack(), Ct);

        Assert.Equal(BillingPaymentOutcome.Succeeded, redirect.Outcome);
        Assert.Equal(2, stripe.Requests.Count);
    }

    [Fact]
    public async Task Stripe_saying_not_to_retry_is_obeyed_even_for_a_conflict()
    {
        var stripe = new StubStripe(Error(HttpStatusCode.Conflict, "error-idempotency-in-use", shouldRetry: "false"));

        var redirect = await stripe.Gateway().StartPackCheckoutAsync(Pack(), Ct);

        Assert.Equal(BillingPaymentOutcome.Unavailable, redirect.Outcome);
        Assert.Single(stripe.Requests);
    }

    /// <summary>
    /// What cannot have been saved against the key is repeated with the same key and the same fields: a request
    /// that got no answer, the key still executing, a rate limit. That is what makes the retry safe.
    /// </summary>
    [Theory]
    [MemberData(nameof(Recoverable))]
    public async Task What_stripe_cannot_have_saved_is_retried_with_the_same_key_and_fields(string reason, Scripted first)
    {
        Assert.NotEmpty(reason);
        var stripe = new StubStripe(first, Ok("checkout-session"));

        var redirect = await stripe.Gateway().StartSubscriptionCheckoutAsync(Subscription(), Ct);

        Assert.Equal(BillingPaymentOutcome.Succeeded, redirect.Outcome);
        Assert.Equal(2, stripe.Requests.Count);
        Assert.Equal(stripe.Requests[0].IdempotencyKey, stripe.Requests[1].IdempotencyKey);
        Assert.Equal(stripe.Requests[0].Form, stripe.Requests[1].Form);
    }

    public static TheoryData<string, Scripted> Recoverable() => new()
    {
        { "a dropped connection", Scripted.Failing },
        { "an attempt that ran out of time", Scripted.Hanging },
        { "the same key still executing", Error(HttpStatusCode.Conflict, "error-idempotency-in-use") },
        { "a rate limit", Error(HttpStatusCode.TooManyRequests, "error-rate-limit") },
    };

    [Fact]
    public async Task A_rate_limit_that_outlasts_the_retries_is_throttled_after_three_attempts()
    {
        var limited = Error(HttpStatusCode.TooManyRequests, "error-rate-limit");
        var stripe = new StubStripe(limited, limited, limited);

        var redirect = await stripe.Gateway().StartPackCheckoutAsync(Pack(), Ct);

        Assert.Equal(BillingPaymentOutcome.Throttled, redirect.Outcome);
        Assert.Equal(3, stripe.Requests.Count);
    }

    [Fact]
    public async Task No_answer_after_the_retries_is_unknown_rather_than_refused()
    {
        var stripe = new StubStripe(Scripted.Hanging, Scripted.Failing, Scripted.Hanging);

        var created = await stripe.Gateway().CreateCustomerAsync(Account, Ct);

        Assert.Equal(BillingPaymentOutcome.Unavailable, created.Outcome);
        Assert.Null(created.ExternalCustomerId);
        Assert.Equal(3, stripe.Requests.Count);
    }

    [Fact]
    public async Task The_callers_cancellation_stops_the_call_rather_than_reading_as_a_timeout()
    {
        var stripe = new StubStripe(Scripted.Hanging);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stripe.Gateway().CreateCustomerAsync(Account, cancelled.Token));
    }

    // ---- what is not Stripe's answer ---------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Unreadable))]
    public async Task An_answer_this_contract_cannot_read_is_unreadable_and_not_repeated(string reason, Scripted answer)
    {
        Assert.NotEmpty(reason);
        var stripe = new StubStripe(answer);

        var redirect = await stripe.Gateway().StartSubscriptionCheckoutAsync(Subscription(), Ct);

        Assert.Equal(BillingPaymentOutcome.Unreadable, redirect.Outcome);
        Assert.Null(redirect.Url);
        Assert.Single(stripe.Requests);
    }

    public static TheoryData<string, Scripted> Unreadable() => new()
    {
        { "not json", new Scripted(HttpStatusCode.OK, "<html>maintenance</html>") },
        { "an empty body", new Scripted(HttpStatusCode.OK, "") },
        { "a json array", new Scripted(HttpStatusCode.OK, "[]") },
        { "no id", new Scripted(HttpStatusCode.OK, Fixture("checkout-session").Replace("\"id\": \"cs_test_", "\"session\": \"cs_test_", StringComparison.Ordinal)) },
        { "another object's id", new Scripted(HttpStatusCode.OK, Fixture("portal-session")) },
        { "a live object for a test key", new Scripted(HttpStatusCode.OK, Fixture("checkout-session").Replace("\"livemode\": false", "\"livemode\": true", StringComparison.Ordinal)) },
        { "a link that is not https", new Scripted(HttpStatusCode.OK, Fixture("checkout-session").Replace("\"url\": \"https://", "\"url\": \"http://", StringComparison.Ordinal)) },
        { "a link off stripe.com", new Scripted(HttpStatusCode.OK, Fixture("checkout-session").Replace("https://checkout.stripe.com/", "https://checkout.stripe.com.example/", StringComparison.Ordinal)) },
        { "another customer's session", new Scripted(HttpStatusCode.OK, Fixture("checkout-session").Replace("\"customer\": \"cus_NffrFeUfNV2Hib\"", "\"customer\": \"cus_SomeoneElse\"", StringComparison.Ordinal)) },
        { "a session for no customer", new Scripted(HttpStatusCode.OK, Fixture("checkout-session").Replace("\"customer\": \"cus_NffrFeUfNV2Hib\"", "\"customer\": null", StringComparison.Ordinal)) },
        { "a redirect", new Scripted(HttpStatusCode.Found, "") },
    };

    // ---- arguments ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_malformed_request_is_the_callers_mistake_and_never_reaches_stripe()
    {
        var stripe = new StubStripe();
        var gateway = stripe.Gateway();

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.CreateCustomerAsync("  ", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.StartSubscriptionCheckoutAsync(Subscription() with { ExternalCustomerId = "acct_1" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.StartSubscriptionCheckoutAsync(Subscription() with { IdempotencyKey = " " }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.StartSubscriptionCheckoutAsync(Subscription() with { IdempotencyKey = new string('k', 256) }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.StartSubscriptionCheckoutAsync(Subscription(success: new Uri("http://app.creatorpantry.example/return")), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.StartSubscriptionCheckoutAsync(Subscription(success: new Uri("https://app.creatorpantry.example/return#done")), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.StartSubscriptionCheckoutAsync(Subscription(success: new Uri("https://app.creatorpantry.example/return?session_id=x")), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.StartPackCheckoutAsync(Pack() with { CancelUrl = new Uri("https://someone@app.creatorpantry.example/") }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.OpenPortalAsync(new BillingPortalRequest(Customer, new Uri("/billing", UriKind.Relative)), Ct));

        Assert.Empty(stripe.Requests);
    }

    [Fact]
    public async Task A_local_run_may_return_to_plain_http_on_this_machine()
    {
        var stripe = new StubStripe(Ok("checkout-session"));

        var redirect = await stripe.Gateway().StartSubscriptionCheckoutAsync(Subscription(success: new Uri("http://localhost:4200/billing/return")), Ct);

        Assert.Equal(BillingPaymentOutcome.Succeeded, redirect.Outcome);
    }

    // ---- what reaches a log ------------------------------------------------------------------------------

    /// <summary>
    /// Every way a call can end, and none of it logs the key, Stripe's message (which quotes the key, a parameter or
    /// an idempotency key back), the account, an address or a link Stripe returned — while the shape that support
    /// needs, the error code and Stripe's request id, is there.
    /// </summary>
    [Fact]
    public async Task Logs_carry_the_shape_of_a_call_and_nothing_of_the_creator_or_the_credential()
    {
        var logs = new CapturingLoggerProvider();
        var stripe = new StubStripe(
            Ok("customer"),
            Error(HttpStatusCode.BadRequest, "error-resource-missing"),
            Error(HttpStatusCode.Unauthorized, "error-authentication"),
            Error(HttpStatusCode.Conflict, "error-idempotency-in-use", shouldRetry: "false"),
            Scripted.Failing,
            Ok("checkout-session"),
            Ok("portal-session"));
        var gateway = stripe.Gateway(logs: logs);

        await gateway.CreateCustomerAsync(Account, Ct);
        await gateway.StartSubscriptionCheckoutAsync(Subscription(), Ct);
        await gateway.StartPackCheckoutAsync(Pack(), Ct);
        await gateway.StartPackCheckoutAsync(Pack(key: "purchase-2"), Ct);
        await gateway.StartSubscriptionCheckoutAsync(Subscription(key: "purchase-3"), Ct);
        await gateway.OpenPortalAsync(new BillingPortalRequest(PortalCustomer, Cancel), Ct);

        var all = string.Join("\n", logs.Entries);
        Assert.Contains("resource_missing", all, StringComparison.Ordinal);
        Assert.Contains(RequestId, all, StringComparison.Ordinal);

        foreach (var secret in new[]
        {
            SecretKey, "rk_test_", "Invalid API Key", "No such price", "In-progress", "in-progress", "cp-pack-checkout",
            Account, Customer, PortalCustomer, "creatorpantry.example", "checkout.stripe.com", "billing.stripe.com", "price_creator", "purchase-",
        })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }
    }

    // ---- the stub ------------------------------------------------------------------------------------------

    private static string Value(IReadOnlyList<KeyValuePair<string, string>> form, string key) =>
        Assert.Single(form, pair => pair.Key == key).Value;

    private sealed class ManualClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    public sealed record Scripted(HttpStatusCode Status, string Body, string? ShouldRetry = null)
    {
        public bool Fails { get; init; }

        public bool Never { get; init; }

        public static Scripted Failing { get; } = new(HttpStatusCode.OK, "") { Fails = true };

        public static Scripted Hanging { get; } = new(HttpStatusCode.OK, "") { Never = true };
    }

    private sealed record Sent(
        HttpMethod Method,
        string Uri,
        string? Authorization,
        string? Version,
        string? IdempotencyKey,
        IReadOnlyList<KeyValuePair<string, string>> Form);

    private sealed class StubStripe(params Scripted[] script) : HttpMessageHandler
    {
        private readonly Queue<Scripted> _script = new(script);

        public List<Sent> Requests { get; } = [];

        public IBillingPaymentGateway Gateway(
            CapturingLoggerProvider? logs = null,
            string? portalConfiguration = null,
            IClock? clock = null,
            TimeSpan? totalTimeout = null,
            string? upgradePortalConfiguration = null)
        {
            // The headers exactly as the host's registration sets them.
            var http = new HttpClient(this, disposeHandler: false);
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SecretKey);
            http.DefaultRequestHeaders.Add("Stripe-Version", StripeBillingOptions.ApiVersion);

            var settings = new StripeBillingSettings
            {
                PortalConfiguration = portalConfiguration,
                UpgradePortalConfiguration = upgradePortalConfiguration,
            };
            settings.Prices.Plans["creator"] = new StripePlanPrices { Monthly = "price_creator_monthly", Annual = "price_creator_annual" };
            settings.Prices.Plans["pro"] = new StripePlanPrices { Monthly = "price_pro_monthly", Annual = "price_pro_annual" };
            settings.Prices.Packs["small"] = "price_pack_small";
            settings.Prices.Packs["medium"] = "price_pack_medium";

            return new StripeBillingGateway(
                http,
                new StripeBillingOptions(new Uri("https://api.stripe.example/"), SecretKey, WebhookSecret: null)
                {
                    AttemptTimeout = TimeSpan.FromMilliseconds(150),
                    TotalTimeout = totalTimeout ?? TimeSpan.FromSeconds(30),
                    RetryBaseDelay = TimeSpan.FromMilliseconds(1),
                },
                Options.Create(settings),
                clock ?? new ManualClock(Now),
                (logs is null ? LoggerFactory.Create(_ => { }) : LoggerFactory.Create(builder => builder.AddProvider(logs)))
                    .CreateLogger<StripeBillingGateway>());
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Sent(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Stripe-Version", out var versions) ? versions.Single() : null,
                request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
                ParseForm(body)));

            var next = _script.Count > 0 ? _script.Dequeue() : throw new InvalidOperationException("The gateway asked for more than was scripted.");

            if (next.Fails)
            {
                throw new HttpRequestException("No such host is known. (api.stripe.example:443)");
            }

            if (next.Never)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var response = new HttpResponseMessage(next.Status) { Content = new StringContent(next.Body, Encoding.UTF8, "application/json") };
            response.Headers.Add("Request-Id", RequestId);
            if (next.ShouldRetry is { } shouldRetry)
            {
                response.Headers.Add("Stripe-Should-Retry", shouldRetry);
            }

            return response;
        }

        /// <summary>The form as Stripe reads it: decoded, in order, repeated keys kept.</summary>
        private static List<KeyValuePair<string, string>> ParseForm(string body) =>
            body.Length == 0
                ? []
                : body.Split('&').Select(pair =>
                {
                    var at = pair.IndexOf('=', StringComparison.Ordinal);
                    return new KeyValuePair<string, string>(Decode(pair[..at]), Decode(pair[(at + 1)..]));
                }).ToList();

        private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }
}
