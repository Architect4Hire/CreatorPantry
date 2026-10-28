using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace CreatorPantry.Domain.Modules.Billing.Integration.Stripe;

/// <summary>
/// Stripe, as the payment provider for plans and packs (PRICE-010, AP.6.1): Checkout for a first subscription and
/// for every pack, the customer portal for everything else, under Managed Payments.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The contract this is written against</strong> is Stripe's API v1 at <c>2026-09-30.endive</c>
/// (docs.stripe.com, read 2026-10-28): <c>POST customers</c>, <c>POST checkout/sessions</c> and
/// <c>POST billing_portal/sessions</c>, form-encoded, authenticated by a bearer key — and, for a running subscription
/// (AP.6.2), <c>GET subscriptions/{id}</c> and the <c>subscription_schedules</c> calls, and for a pack (AP.6.3),
/// <c>GET checkout/sessions/{id}</c>, <c>GET charges/{id}</c> and <c>GET disputes/{id}</c>. Plain HTTP rather than
/// Stripe.net, as the repository's other providers are: these calls do not need an SDK, the pin is one header here
/// rather than whatever a package release carries, and no Stripe type exists to leak past this file.
/// </para>
/// <para>
/// <strong>Every checkout is Managed Payments</strong> (<c>managed_payments[enabled]=true</c>; AP.6.1's tax answer):
/// Stripe is the merchant of record and calculates, collects and remits the tax, so nothing here sets
/// <c>automatic_tax</c> or collects an address for it.
/// </para>
/// <para>
/// <strong>Every call is a POST with an idempotency key, and retried only where Stripe says that is safe.</strong> A
/// dropped connection or a timed-out attempt is repeated with the same key, which Stripe answers with the first
/// attempt's result if it got that far; so is a 409 (the same key still executing), a 429, and anything Stripe marks
/// <c>Stripe-Should-Retry: true</c>. A 5xx is not: Stripe saves it against the key, so repeating it only replays it,
/// and its outcome is unknown — which is <see cref="BillingPaymentOutcome.Unavailable"/>, reconciled from webhooks
/// (AP.6.2) rather than guessed at. A key is derived from the caller's, per operation, so one purchase is one
/// checkout however often it is asked for within Stripe's window, and the caller's key is never sent as it is.
/// Waits back off exponentially with jitter; the whole call, attempts and waits, is held to
/// <see cref="StripeBillingOptions.TotalTimeout"/>; and the named client's circuit breaker stops sending at all
/// while Stripe keeps failing (<c>StripeBillingRegistration</c>), which reads as Unavailable and is not retried.
/// </para>
/// <para>
/// <strong>What travels to Stripe about a creator</strong> is the account id, as metadata and a client reference to
/// reconcile webhooks by — no email, name or address. Checkout collects what it needs from the creator directly.
/// </para>
/// <para>
/// <strong>Nothing of Stripe's is logged here but a shape</strong>: the operation, outcome, HTTP status, Stripe's
/// error type and code (both from a fixed vocabulary, and filtered to it), its request id for support, and the
/// elapsed time. Never a key, a body, a message, a URL Stripe returned, or anything the creator entered.
/// </para>
/// </remarks>
internal sealed class StripeBillingGateway(
    HttpClient http,
    StripeBillingOptions options,
    IOptions<StripeBillingSettings> settings,
    IClock clock,
    ILogger<StripeBillingGateway> logger) : IBillingPaymentGateway
{
    private const int MaxAttempts = 3;

    /// <summary>A stranger's bytes, bounded. A session object is a few kilobytes.</summary>
    private const int MaxResponseBytes = 1024 * 1024;

    /// <summary>Stripe's limit on <c>client_reference_id</c>. A longer account id travels as metadata only.</summary>
    private const int ClientReferenceMaxLength = 200;

    /// <summary>Stripe's limit on an idempotency key.</summary>
    private const int IdempotencyKeyMaxLength = 255;

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long one account's customer requests share an idempotency key. Long enough that a burst of racing
    /// requests is one customer at Stripe; short, because Stripe saves a 5xx against a key for at least a day, and a
    /// key that never changed would replay one failure to every later attempt for that long.
    /// </summary>
    private static readonly TimeSpan CustomerKeyWindow = TimeSpan.FromMinutes(10);

    public BillingPaymentMode Mode => options.Mode;

    public async Task<BillingCustomerCreation> CreateCustomerAsync(string accountId, CancellationToken cancellationToken)
    {
        RequireAccount(accountId);

        var form = new List<KeyValuePair<string, string>> { new($"metadata[{StripeMetadata.AccountId}]", accountId) };

        // Derived from the account, the mode and a ten-minute window, so two requests racing to give one account its
        // first customer send the same key and get the same customer (BillingCustomerDataLayer.EnsureAsync), while a
        // failure Stripe saved against the key is not replayed past its window. A race straddling a window's edge
        // makes a second customer at Stripe, which the record's unique index absorbs. Hashed, because Stripe asks
        // that a key carry no personal identifier.
        var window = clock.UtcNow.ToUnixTimeSeconds() / (long)CustomerKeyWindow.TotalSeconds;
        var key = DerivedKey("customer", $"{options.Mode}:{accountId}:{window}");

        var reply = await SendAsync("customer", "v1/customers", form, key, cancellationToken);
        if (reply.Outcome is not BillingPaymentOutcome.Succeeded)
        {
            return BillingCustomerCreation.Of(reply.Outcome);
        }

        return ReadId(reply.Data, "cus_") is { } id && ReadModeMatches(reply.Data)
            ? new BillingCustomerCreation(BillingPaymentOutcome.Succeeded, id, options.Mode == BillingPaymentMode.Live)
            : Unreadable("customer", BillingCustomerCreation.Of(BillingPaymentOutcome.Unreadable));
    }

    public async Task<BillingRedirect> StartSubscriptionCheckoutAsync(
        SubscriptionCheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireCheckout(request.AccountId, request.ExternalCustomerId, request.SuccessUrl, request.CancelUrl, request.IdempotencyKey);

        // A trial has no price, nor does a cycle that is neither; neither is for sale, and nothing is sent.
        if (settings.Value.PriceFor(request.PlanKey, request.Cycle) is not { } price)
        {
            return BillingRedirect.Of(BillingPaymentOutcome.NotOffered);
        }

        var cycle = request.Cycle == BillingCycle.Annual ? "annual" : "monthly";
        var metadata = new (string Key, string Value)[]
        {
            (StripeMetadata.AccountId, request.AccountId),
            (StripeMetadata.Purchase, StripeMetadata.SubscriptionPurchase),
            (StripeMetadata.PlanKey, request.PlanKey),
            (StripeMetadata.BillingCycle, cycle),
        };

        var form = CheckoutForm("subscription", request.AccountId, request.ExternalCustomerId, price, request.SuccessUrl, request.CancelUrl, metadata);

        // On the subscription as well as the session, so every later event about it — renewals, a failed payment, a
        // cancellation — names the account without a lookup through the session (AP.6.2).
        form.AddRange(metadata.Select(pair => Pair($"subscription_data[metadata][{pair.Key}]", pair.Value)));

        var key = DerivedKey("subscription-checkout", request.IdempotencyKey);
        return await StartCheckoutAsync("subscription-checkout", form, key, request.ExternalCustomerId, cancellationToken);
    }

    public async Task<BillingRedirect> StartPackCheckoutAsync(PackCheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireCheckout(request.AccountId, request.ExternalCustomerId, request.SuccessUrl, request.CancelUrl, request.IdempotencyKey);

        if (settings.Value.PriceFor(request.PackKey) is not { } price)
        {
            return BillingRedirect.Of(BillingPaymentOutcome.NotOffered);
        }

        var metadata = new (string Key, string Value)[]
        {
            (StripeMetadata.AccountId, request.AccountId),
            (StripeMetadata.Purchase, StripeMetadata.PackPurchase),
            (StripeMetadata.PackKey, request.PackKey),
        };

        var form = CheckoutForm("payment", request.AccountId, request.ExternalCustomerId, price, request.SuccessUrl, request.CancelUrl, metadata);

        // On the payment too: a refund or a dispute arrives about the charge, not the session, and AP.6.3 claws back
        // the grant it names.
        form.AddRange(metadata.Select(pair => Pair($"payment_intent_data[metadata][{pair.Key}]", pair.Value)));

        var key = DerivedKey("pack-checkout", request.IdempotencyKey);
        return await StartCheckoutAsync("pack-checkout", form, key, request.ExternalCustomerId, cancellationToken);
    }

    public async Task<BillingRedirect> OpenPortalAsync(BillingPortalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireCustomer(request.ExternalCustomerId);
        RequireReturnable(request.ReturnUrl, nameof(request.ReturnUrl));

        var form = new List<KeyValuePair<string, string>>
        {
            Pair("customer", request.ExternalCustomerId),
            Pair("return_url", request.ReturnUrl.AbsoluteUri),
        };

        if (settings.Value.PortalConfiguration is { Length: > 0 } configuration)
        {
            form.Add(Pair("configuration", configuration));
        }

        // A portal session is a short-lived link, and opening it twice is harmless; the key only keeps this call's
        // own retries from making two.
        var key = DerivedKey("portal", Guid.NewGuid().ToString("N"));

        var reply = await SendAsync("portal", "v1/billing_portal/sessions", form, key, cancellationToken);
        if (reply.Outcome is not BillingPaymentOutcome.Succeeded)
        {
            return BillingRedirect.Of(reply.Outcome);
        }

        return ReadId(reply.Data, "bps_") is not null
            && ReadModeMatches(reply.Data)
            && ReadCustomerMatches(reply.Data, request.ExternalCustomerId)
            && ReadUrl(reply.Data) is { } url
            ? new BillingRedirect(BillingPaymentOutcome.Succeeded, url)
            : Unreadable("portal", BillingRedirect.Of(BillingPaymentOutcome.Unreadable));
    }

    /// <remarks>
    /// <c>GET v1/subscriptions/{id}</c> with the schedule and the latest invoice expanded, so a downgrade waiting for
    /// the period's end and whether an upgrade's proration has been paid are read in the same call (AP.6.2). At the pinned version a subscription's period lives on its item
    /// (<c>items.data[].current_period_end</c>), not on the subscription, since <c>2025-03-31.basil</c>.
    /// </remarks>
    public async Task<BillingSubscriptionRead> GetSubscriptionAsync(
        string externalSubscriptionId, CancellationToken cancellationToken)
    {
        RequireReference(externalSubscriptionId, "sub_", nameof(externalSubscriptionId));

        var reply = await ReadAsync(
            "subscription", $"v1/subscriptions/{externalSubscriptionId}?expand%5B%5D=schedule&expand%5B%5D=latest_invoice", cancellationToken);

        if (reply.Outcome is not BillingPaymentOutcome.Succeeded)
        {
            return BillingSubscriptionRead.Of(reply.Outcome);
        }

        return ReadSubscription(reply.Data, externalSubscriptionId) is { } subscription
            ? new BillingSubscriptionRead(BillingPaymentOutcome.Succeeded, subscription)
            : Unreadable("subscription", BillingSubscriptionRead.Of(BillingPaymentOutcome.Unreadable));
    }

    /// <remarks>
    /// <c>GET v1/checkout/sessions/{id}</c> with the line items expanded, which a session does not carry by default
    /// (docs.stripe.com, "The Checkout Session object", read 2026-10-28): the one line's price is what says which pack
    /// was bought, and the metadata only which one the checkout was started for. <c>payment_status</c> is
    /// <c>paid</c>, <c>unpaid</c> or <c>no_payment_required</c> — a delayed payment method sits at <c>unpaid</c> until
    /// it settles, and its own event arrives when it does.
    /// </remarks>
    public async Task<BillingPackCheckoutRead> GetPackCheckoutAsync(
        string externalSessionId, CancellationToken cancellationToken)
    {
        RequireReference(externalSessionId, "cs_", nameof(externalSessionId));

        var reply = await ReadAsync(
            "pack-checkout", $"v1/checkout/sessions/{externalSessionId}?expand%5B%5D=line_items", cancellationToken);

        if (reply.Outcome is not BillingPaymentOutcome.Succeeded)
        {
            return BillingPackCheckoutRead.Of(reply.Outcome);
        }

        return ReadPackCheckout(reply.Data, externalSessionId) is { } checkout
            ? new BillingPackCheckoutRead(BillingPaymentOutcome.Succeeded, checkout)
            : Unreadable("pack-checkout", BillingPackCheckoutRead.Of(BillingPaymentOutcome.Unreadable));
    }

    /// <remarks>
    /// <para>
    /// A refund is read from its charge (<c>GET v1/charges/{id}</c>) and a dispute from the dispute
    /// (<c>GET v1/disputes/{id}</c>), because Stripe holds them as different objects and a dispute event's object is
    /// the dispute, not the charge (docs.stripe.com, "The Charge object" and "The Dispute object", read 2026-10-28).
    /// Each is read with the payment expanded: the pack marker and the account travel on the payment
    /// (<c>payment_intent_data[metadata]</c> at the checkout), and a charge does not inherit a payment's metadata.
    /// </para>
    /// <para>
    /// <strong>What counts as reversed:</strong> a charge with anything refunded — a partial refund included, which
    /// claws back the whole unspent balance (AP.6.3) — and a dispute at all, since one exists only once the money is
    /// held back. Nothing about the amount crosses: how many credits come back is this host's arithmetic over its own
    /// grant.
    /// </para>
    /// </remarks>
    public async Task<BillingPaymentReversalRead> GetPaymentReversalAsync(
        string externalReference, BillingPackReversal kind, CancellationToken cancellationToken)
    {
        var (operation, path) = kind switch
        {
            BillingPackReversal.Refund => ("charge", "v1/charges"),

            // Stripe's dispute ids have carried both prefixes over the years; either is an id, neither is interpreted.
            BillingPackReversal.Dispute => ("dispute", "v1/disputes"),

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A reversal is a refund or a dispute."),
        };

        RequireReference(
            externalReference,
            kind == BillingPackReversal.Refund ? ["ch_"] : ["du_", "dp_"],
            nameof(externalReference));

        var reply = await ReadAsync(
            operation, $"{path}/{externalReference}?expand%5B%5D=payment_intent", cancellationToken);

        if (reply.Outcome is not BillingPaymentOutcome.Succeeded)
        {
            return BillingPaymentReversalRead.Of(reply.Outcome);
        }

        return ReadReversal(reply.Data, externalReference, kind) is { } reversal
            ? new BillingPaymentReversalRead(BillingPaymentOutcome.Succeeded, reversal)
            : Unreadable(operation, BillingPaymentReversalRead.Of(BillingPaymentOutcome.Unreadable));
    }

    /// <remarks>
    /// <c>POST v1/billing_portal/sessions</c> with a <c>subscription_update_confirm</c> flow (docs.stripe.com,
    /// "Deep links in the customer portal", read 2026-10-28): the one item, its new price, quantity one. The upgrade
    /// configuration is what invoices the proration at once and takes the payment; without one there is nothing to
    /// open the page under, and nothing is sent.
    /// </remarks>
    public async Task<BillingRedirect> StartPlanUpgradeAsync(PlanUpgradeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireCustomer(request.ExternalCustomerId);
        RequireReference(request.ExternalSubscriptionId, "sub_", nameof(request.ExternalSubscriptionId));
        RequireReference(request.SubscriptionItemId, "si_", nameof(request.SubscriptionItemId));
        RequireReturnable(request.ReturnUrl, nameof(request.ReturnUrl));
        RequireReturnable(request.CompletedUrl, nameof(request.CompletedUrl));

        if (settings.Value.UpgradePortalConfiguration is not { Length: > 0 } configuration)
        {
            return BillingRedirect.Of(BillingPaymentOutcome.NotConfigured);
        }

        if (settings.Value.PriceFor(request.PlanKey, request.Cycle) is not { } price)
        {
            return BillingRedirect.Of(BillingPaymentOutcome.NotOffered);
        }

        var form = new List<KeyValuePair<string, string>>
        {
            Pair("customer", request.ExternalCustomerId),
            Pair("return_url", request.ReturnUrl.AbsoluteUri),
            Pair("configuration", configuration),
            Pair("flow_data[type]", "subscription_update_confirm"),
            Pair("flow_data[subscription_update_confirm][subscription]", request.ExternalSubscriptionId),
            Pair("flow_data[subscription_update_confirm][items][0][id]", request.SubscriptionItemId),
            Pair("flow_data[subscription_update_confirm][items][0][price]", price),
            Pair("flow_data[subscription_update_confirm][items][0][quantity]", "1"),
            Pair("flow_data[after_completion][type]", "redirect"),
            Pair("flow_data[after_completion][redirect][return_url]", request.CompletedUrl.AbsoluteUri),
        };

        // A short-lived link, as the portal's own is: the key only keeps this call's retries from making two.
        var key = DerivedKey("upgrade", Guid.NewGuid().ToString("N"));

        var reply = await SendAsync("upgrade", "v1/billing_portal/sessions", form, key, cancellationToken);
        if (reply.Outcome is not BillingPaymentOutcome.Succeeded)
        {
            return BillingRedirect.Of(reply.Outcome);
        }

        return ReadId(reply.Data, "bps_") is not null
            && ReadModeMatches(reply.Data)
            && ReadCustomerMatches(reply.Data, request.ExternalCustomerId)
            && ReadUrl(reply.Data) is { } url
            ? new BillingRedirect(BillingPaymentOutcome.Succeeded, url)
            : Unreadable("upgrade", BillingRedirect.Of(BillingPaymentOutcome.Unreadable));
    }

    /// <remarks>
    /// <para>
    /// Two calls (docs.stripe.com, "Subscription schedules", read 2026-10-28): a schedule made from the subscription —
    /// or the one already managing it, read — then that schedule updated to two phases. The first is the current
    /// phase exactly as it stands, its own start, end and price, so nothing about the period being paid for moves;
    /// the second starts at that end on the new price. <c>proration_behavior=none</c>: a change at the boundary owes
    /// nothing either way. <c>end_behavior=release</c>, so once the second phase has run the subscription carries on
    /// alone, on its new price.
    /// </para>
    /// <para>
    /// Each call carries its own key derived from the caller's, so a retry of the whole change is the same schedule
    /// and the same update, never a second schedule.
    /// </para>
    /// </remarks>
    public async Task<BillingPaymentOutcome> SchedulePlanChangeAsync(
        ScheduledPlanChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireReference(request.ExternalSubscriptionId, "sub_", nameof(request.ExternalSubscriptionId));
        if (request.ExistingScheduleId is not null)
        {
            RequireReference(request.ExistingScheduleId, "sub_sched_", nameof(request.ExistingScheduleId));
        }

        RequireKey(request.IdempotencyKey);

        if (settings.Value.PriceFor(request.PlanKey, request.Cycle) is not { } price)
        {
            return BillingPaymentOutcome.NotOffered;
        }

        var schedule = request.ExistingScheduleId is { } existing
            ? await ReadAsync("schedule-read", $"v1/subscription_schedules/{existing}", cancellationToken)
            : await SendAsync(
                "schedule-create",
                "v1/subscription_schedules",
                [Pair("from_subscription", request.ExternalSubscriptionId)],
                DerivedKey("schedule-create", request.IdempotencyKey),
                cancellationToken);

        if (schedule.Outcome is not BillingPaymentOutcome.Succeeded)
        {
            return schedule.Outcome;
        }

        if (ReadId(schedule.Data, "sub_sched_") is not { } scheduleId
            || !ReadModeMatches(schedule.Data)
            || CurrentPhase(schedule.Data) is not { } current)
        {
            return Unreadable("schedule", BillingPaymentOutcome.Unreadable);
        }

        var form = new List<KeyValuePair<string, string>>
        {
            Pair("end_behavior", "release"),
            Pair("proration_behavior", "none"),
            Pair("phases[0][items][0][price]", current.Price),
            Pair("phases[0][items][0][quantity]", "1"),
            Pair("phases[0][start_date]", current.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Pair("phases[0][end_date]", current.End.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Pair("phases[1][items][0][price]", price),
            Pair("phases[1][items][0][quantity]", "1"),
            Pair("phases[1][duration][interval]", request.Cycle == BillingCycle.Annual ? "year" : "month"),
            Pair("phases[1][duration][interval_count]", "1"),
        };

        var updated = await SendAsync(
            "schedule-update",
            $"v1/subscription_schedules/{scheduleId}",
            form,
            DerivedKey("schedule-update", request.IdempotencyKey),
            cancellationToken);

        if (updated.Outcome is not BillingPaymentOutcome.Succeeded)
        {
            return updated.Outcome;
        }

        return ReadId(updated.Data, "sub_sched_") == scheduleId && ReadModeMatches(updated.Data)
            ? BillingPaymentOutcome.Succeeded
            : Unreadable("schedule-update", BillingPaymentOutcome.Unreadable);
    }

    /// <remarks><c>POST v1/subscription_schedules/{id}/release</c>: the subscription stays exactly as it is.</remarks>
    public async Task<BillingPaymentOutcome> ReleaseScheduleAsync(
        string externalScheduleId, string idempotencyKey, CancellationToken cancellationToken)
    {
        RequireReference(externalScheduleId, "sub_sched_", nameof(externalScheduleId));
        RequireKey(idempotencyKey);

        var reply = await SendAsync(
            "schedule-release",
            $"v1/subscription_schedules/{externalScheduleId}/release",
            [],
            DerivedKey("schedule-release", idempotencyKey),
            cancellationToken);

        if (reply.Outcome is not BillingPaymentOutcome.Succeeded)
        {
            return reply.Outcome;
        }

        return ReadId(reply.Data, "sub_sched_") == externalScheduleId
            && ReadModeMatches(reply.Data)
            && StringOf(reply.Data, "status") == "released"
                ? BillingPaymentOutcome.Succeeded
                : Unreadable("schedule-release", BillingPaymentOutcome.Unreadable);
    }

    private async Task<BillingRedirect> StartCheckoutAsync(
        string operation, List<KeyValuePair<string, string>> form, string key, string customerId, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(operation, "v1/checkout/sessions", form, key, cancellationToken);
        if (reply.Outcome is not BillingPaymentOutcome.Succeeded)
        {
            return BillingRedirect.Of(reply.Outcome);
        }

        if (ReadId(reply.Data, "cs_") is not { } id
            || !ReadModeMatches(reply.Data)
            || !ReadCustomerMatches(reply.Data, customerId)
            || ReadUrl(reply.Data) is not { } url)
        {
            return Unreadable(operation, BillingRedirect.Of(BillingPaymentOutcome.Unreadable));
        }

        DateTimeOffset? expiresAt = reply.Data.TryGetProperty("expires_at", out var expires)
            && expires.ValueKind == JsonValueKind.Number
            && expires.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;

        return new BillingRedirect(BillingPaymentOutcome.Succeeded, url, id, expiresAt);
    }

    /// <summary>The fields every checkout shares: one line, the customer, where it returns to, and the reconciliation.</summary>
    private static List<KeyValuePair<string, string>> CheckoutForm(
        string mode,
        string accountId,
        string customerId,
        string price,
        Uri successUrl,
        Uri cancelUrl,
        IEnumerable<(string Key, string Value)> metadata)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            Pair("mode", mode),
            Pair("customer", customerId),
            Pair("line_items[0][price]", price),
            Pair("line_items[0][quantity]", "1"),

            // Stripe replaces the literal placeholder with the session's id, so the return page can show "pending"
            // for this checkout until its webhook lands (AP.6.5). Appended to the text, not through Uri, which
            // would escape the braces Stripe looks for.
            Pair("success_url", WithSessionPlaceholder(successUrl)),
            Pair("cancel_url", cancelUrl.AbsoluteUri),
            Pair("managed_payments[enabled]", "true"),
        };

        if (accountId.Length <= ClientReferenceMaxLength)
        {
            form.Add(Pair("client_reference_id", accountId));
        }

        form.AddRange(metadata.Select(pair => Pair($"metadata[{pair.Key}]", pair.Value)));
        return form;
    }

    private static string WithSessionPlaceholder(Uri successUrl)
    {
        var text = successUrl.AbsoluteUri;
        return text + (string.IsNullOrEmpty(successUrl.Query) ? "?" : "&") + "session_id={CHECKOUT_SESSION_ID}";
    }

    private static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);

    /// <summary>
    /// <c>cp-{operation}-v1-</c> and a hash of what identifies the operation: deterministic, within Stripe's 255
    /// characters whatever the caller sent, free of anything personal, and different per operation so one caller
    /// key cannot collide with itself across two kinds of call.
    /// </summary>
    private static string DerivedKey(string operation, string identity)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $"cp-{operation}-v1-{hash}";
    }

    // ---- arguments: a malformed request is the caller's mistake, never Stripe's to discover -----------------

    private static void RequireAccount(string accountId)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > BillingPolicy.AccountIdMaxLength)
        {
            throw new ArgumentException("An account id is required.", nameof(accountId));
        }
    }

    private static void RequireCustomer(string customerId)
    {
        ArgumentNullException.ThrowIfNull(customerId);
        if (!customerId.StartsWith("cus_", StringComparison.Ordinal) || customerId.Length > BillingPolicy.ExternalReferenceMaxLength)
        {
            throw new ArgumentException("A Stripe customer id is required.", nameof(customerId));
        }
    }

    private static void RequireCheckout(string accountId, string customerId, Uri successUrl, Uri cancelUrl, string idempotencyKey)
    {
        RequireAccount(accountId);
        RequireCustomer(customerId);
        RequireReturnable(successUrl, nameof(successUrl));
        RequireReturnable(cancelUrl, nameof(cancelUrl));

        // The gateway adds the session id; one already there would be a second, and the return page would read either.
        if (successUrl.Query.Contains("session_id=", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A success address must not carry its own session_id.", nameof(successUrl));
        }

        ArgumentNullException.ThrowIfNull(idempotencyKey);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > IdempotencyKeyMaxLength)
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }
    }

    /// <summary>
    /// An address Stripe may send a creator back to: absolute, HTTPS — or HTTP on this machine, for a local run —
    /// with no credentials in it and no fragment for Stripe to drop.
    /// </summary>
    private static void RequireReturnable(Uri url, string name)
    {
        ArgumentNullException.ThrowIfNull(url, name);
        var returnable = url.IsAbsoluteUri
            && (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
            && string.IsNullOrEmpty(url.UserInfo)
            && string.IsNullOrEmpty(url.Fragment);

        if (!returnable)
        {
            throw new ArgumentException("A return address must be an absolute HTTPS address with no fragment.", name);
        }
    }

    // ---- reading what came back ---------------------------------------------------------------------------

    private static string? ReadId(JsonElement data, string prefix) =>
        data.TryGetProperty("id", out var id)
        && id.ValueKind == JsonValueKind.String
        && id.GetString() is { } value
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Length <= BillingPolicy.ExternalReferenceMaxLength
            ? value
            : null;

    /// <summary>
    /// The object is from the mode this host's key acts in. It cannot be otherwise from a real Stripe, which is
    /// exactly why a mismatch is read as "not Stripe's answer to this request" rather than trusted.
    /// </summary>
    private bool ReadModeMatches(JsonElement data) =>
        data.TryGetProperty("livemode", out var live)
        && live.ValueKind is JsonValueKind.True or JsonValueKind.False
        && live.GetBoolean() == (options.Mode == BillingPaymentMode.Live);

    /// <summary>
    /// The session is for the customer it was asked for. A session for anybody else — however it arrived — would be
    /// someone else's checkout or portal handed to this creator.
    /// </summary>
    private static bool ReadCustomerMatches(JsonElement data, string customerId) =>
        data.TryGetProperty("customer", out var customer)
        && customer.ValueKind == JsonValueKind.String
        && string.Equals(customer.GetString(), customerId, StringComparison.Ordinal);

    /// <summary>
    /// An HTTPS page on Stripe's own domain: <c>checkout.stripe.com</c> and <c>billing.stripe.com</c> today. Managed
    /// Payments allows no custom Checkout domain; a custom portal domain, if the owner ever sets one, is added here.
    /// </summary>
    private static Uri? ReadUrl(JsonElement data) =>
        data.TryGetProperty("url", out var url)
        && url.ValueKind == JsonValueKind.String
        && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "stripe.com" || uri.Host.EndsWith(".stripe.com", StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;

    private T Unreadable<T>(string operation, T result)
    {
        logger.LogWarning("Stripe {Operation} answered 200 with an object this contract cannot read.", operation);
        return result;
    }

    // ---- reading a subscription (AP.6.2) -------------------------------------------------------------------

    /// <summary>
    /// The subscription in the application's terms; null when any part the plan follows from is missing or not what
    /// this contract expects — better unread than half read, since a half-read schedule would withdraw a downgrade.
    /// </summary>
    private BillingSubscriptionSnapshot? ReadSubscription(JsonElement data, string askedFor)
    {
        if (ReadId(data, "sub_") != askedFor
            || !ReadModeMatches(data)
            || ReferenceOf(data, "customer", "cus_") is not { } customer
            || !data.TryGetProperty("items", out var items)
            || !items.TryGetProperty("data", out var lines)
            || lines.ValueKind != JsonValueKind.Array
            || lines.GetArrayLength() != 1)
        {
            // One item is every subscription a CreatorPantry checkout makes; more is one made elsewhere, and an
            // upgrade's confirmation page cannot change it either.
            return null;
        }

        var item = lines[0];
        if (ReadId(item, "si_") is not { } itemId
            || PriceOf(item) is not { } price
            || Seconds(item, "current_period_end") is not { } periodEnd)
        {
            return null;
        }

        var sold = settings.Value.PlanFor(price);

        // A cancellation at period end may be said either way; at this version Stripe also sets cancel_at for it.
        var cancelAt = Seconds(data, "cancel_at")
            ?? (data.TryGetProperty("cancel_at_period_end", out var atPeriodEnd) && atPeriodEnd.ValueKind == JsonValueKind.True
                ? periodEnd
                : null);

        string? scheduleId = null;
        BillingScheduledPhase? next = null;

        if (data.TryGetProperty("schedule", out var schedule) && schedule.ValueKind != JsonValueKind.Null)
        {
            // Asked for expanded. An id alone would leave what it has waiting unknown, which is not the same as none.
            if (schedule.ValueKind != JsonValueKind.Object || ReadId(schedule, "sub_sched_") is not { } id)
            {
                return null;
            }

            scheduleId = id;

            if (StringOf(schedule, "status") is "active" or "not_started")
            {
                if (!TryNextPhase(schedule, price, out next))
                {
                    return null;
                }
            }
        }

        return new BillingSubscriptionSnapshot(
            askedFor,
            customer,
            Metadata(data, StripeMetadata.AccountId),
            options.Mode == BillingPaymentMode.Live,
            StatusOf(StringOf(data, "status")),
            itemId,
            sold?.PlanKey,
            sold?.Cycle ?? BillingCycle.Unspecified,
            periodEnd,
            cancelAt,
            Seconds(data, "ended_at"),
            scheduleId,
            next,
            LatestInvoicePaid(data));
    }

    /// <summary>
    /// Whether the latest invoice, expanded, says it is paid. Not expanded, absent or anything but <c>paid</c> reads as
    /// not paid, which holds an upgrade back rather than granting it.
    /// </summary>
    private static bool LatestInvoicePaid(JsonElement data) =>
        data.TryGetProperty("latest_invoice", out var invoice)
        && invoice.ValueKind == JsonValueKind.Object
        && StringOf(invoice, "status") == "paid";

    /// <summary>
    /// The first phase after the current one, if its price differs from the subscription's: what the schedule changes
    /// next. False when the phases cannot be read.
    /// </summary>
    private bool TryNextPhase(JsonElement schedule, string currentPrice, out BillingScheduledPhase? next)
    {
        next = null;

        if (!schedule.TryGetProperty("phases", out var phases) || phases.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var currentStart = schedule.TryGetProperty("current_phase", out var current) && current.ValueKind == JsonValueKind.Object
            ? Seconds(current, "start_date")
            : null;

        var upcoming = new List<(DateTimeOffset Start, string Price)>();
        foreach (var phase in phases.EnumerateArray())
        {
            if (Seconds(phase, "start_date") is not { } start
                || !phase.TryGetProperty("items", out var phaseItems)
                || phaseItems.ValueKind != JsonValueKind.Array
                || phaseItems.GetArrayLength() != 1
                || PriceOf(phaseItems[0]) is not { } phasePrice)
            {
                return false;
            }

            if (currentStart is null || start > currentStart)
            {
                upcoming.Add((start, phasePrice));
            }
        }

        // A schedule not started yet has no current phase, and its first phase is what comes next.
        var first = upcoming.OrderBy(phase => phase.Start).Cast<(DateTimeOffset Start, string Price)?>().FirstOrDefault();
        if (first is { } phaseNext && !string.Equals(phaseNext.Price, currentPrice, StringComparison.Ordinal))
        {
            var sold = settings.Value.PlanFor(phaseNext.Price);
            next = new BillingScheduledPhase(sold?.PlanKey, sold?.Cycle ?? BillingCycle.Unspecified, phaseNext.Start);
        }

        return true;
    }

    /// <summary>The schedule's current phase, as an update has to restate it: its price, start and end, in seconds.</summary>
    private static (string Price, long Start, long End)? CurrentPhase(JsonElement schedule)
    {
        if (!schedule.TryGetProperty("phases", out var phases) || phases.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var currentStart = schedule.TryGetProperty("current_phase", out var current) && current.ValueKind == JsonValueKind.Object
            && current.TryGetProperty("start_date", out var start) && start.TryGetInt64(out var startSeconds)
                ? startSeconds
                : (long?)null;

        foreach (var phase in phases.EnumerateArray())
        {
            // A schedule just made from a subscription has one phase and may not report it as current yet.
            if (phase.TryGetProperty("start_date", out var phaseStart) && phaseStart.TryGetInt64(out var phaseSeconds)
                && (currentStart is null || phaseSeconds == currentStart)
                && phase.TryGetProperty("end_date", out var phaseEnd) && phaseEnd.TryGetInt64(out var endSeconds)
                && phase.TryGetProperty("items", out var phaseItems) && phaseItems.ValueKind == JsonValueKind.Array
                && phaseItems.GetArrayLength() == 1
                && PriceOf(phaseItems[0]) is { } price)
            {
                return (price, phaseSeconds, endSeconds);
            }
        }

        return null;
    }

    // ---- reading a pack's purchase and its reversal (AP.6.3) -----------------------------------------------

    /// <summary>
    /// The pack's checkout in the application's terms; null when it is not the session that was asked for, or is from
    /// the other mode, or does not say whether it was paid.
    /// </summary>
    private BillingPackCheckoutSnapshot? ReadPackCheckout(JsonElement data, string askedFor)
    {
        if (ReadId(data, "cs_") != askedFor
            || !ReadModeMatches(data)
            || StringOf(data, "payment_status") is not { Length: > 0 } paid)
        {
            return null;
        }

        // A one-off payment carrying the marker this host's pack checkout writes. A subscription's session, or one
        // made in Stripe's dashboard, is not a pack and grants nothing.
        var isPack = StringOf(data, "mode") == "payment"
            && Metadata(data, StripeMetadata.Purchase) == StripeMetadata.PackPurchase;

        return new BillingPackCheckoutSnapshot(
            askedFor,
            ReferenceOf(data, "customer", "cus_"),
            Metadata(data, StripeMetadata.AccountId),
            options.Mode == BillingPaymentMode.Live,
            isPack,
            paid is "paid" or "no_payment_required",
            PackKeyOf(data),
            ReferenceOf(data, "payment_intent", "pi_"));
    }

    /// <summary>
    /// The catalogue key the session's one line was charged on; null for a price this host does not sell, for line
    /// items that were not expanded, and for a session with more than one line, which no checkout here makes.
    /// </summary>
    private string? PackKeyOf(JsonElement data) =>
        data.TryGetProperty("line_items", out var items)
        && items.ValueKind == JsonValueKind.Object
        && items.TryGetProperty("data", out var lines)
        && lines.ValueKind == JsonValueKind.Array
        && lines.GetArrayLength() == 1
        && PriceOf(lines[0]) is { } price
            ? settings.Value.PackFor(price)
            : null;

    /// <summary>
    /// The reversal in the application's terms; null when it is not the object that was asked for or is from the
    /// other mode.
    /// </summary>
    private BillingPaymentReversalSnapshot? ReadReversal(JsonElement data, string askedFor, BillingPackReversal kind)
    {
        // Compared with what was asked for rather than by prefix, which the argument guard has already held.
        if (StringOf(data, "id") != askedFor || !ReadModeMatches(data))
        {
            return null;
        }

        var reversed = kind == BillingPackReversal.Refund
            ? AmountRefunded(data) > 0 || Flag(data, "refunded")

            // A dispute exists from the moment the money is held back; its status says where it has got to since.
            : StringOf(data, "status") is { Length: > 0 };

        var (accountId, isPack) = PaymentMarkers(data);

        return new BillingPaymentReversalSnapshot(
            askedFor,
            options.Mode == BillingPaymentMode.Live,
            kind,
            ReferenceOf(data, "payment_intent", "pi_"),
            accountId,
            isPack,
            reversed);
    }

    /// <summary>
    /// What the expanded payment says about the purchase it paid for. Nothing when it was not expanded, which reads as
    /// "no pack marker" — and so as a payment this host did not take, never as one it did.
    /// </summary>
    private static (string? AccountId, bool IsPack) PaymentMarkers(JsonElement data) =>
        data.TryGetProperty("payment_intent", out var payment) && payment.ValueKind == JsonValueKind.Object
            ? (Metadata(payment, StripeMetadata.AccountId),
                Metadata(payment, StripeMetadata.Purchase) == StripeMetadata.PackPurchase)
            : (null, false);

    /// <summary>What has been refunded on a charge, in the smallest currency unit; nought when it does not say.</summary>
    private static long AmountRefunded(JsonElement data) =>
        data.TryGetProperty("amount_refunded", out var amount)
        && amount.ValueKind == JsonValueKind.Number
        && amount.TryGetInt64(out var refunded)
        && refunded > 0
            ? refunded
            : 0;

    private static bool Flag(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A line's price id, whether the price is expanded (a subscription item) or not (a schedule phase).</summary>
    private static string? PriceOf(JsonElement line) => ReferenceOf(line, "price", "price_");

    /// <summary>A field naming another object: its id, or the object expanded with its id inside.</summary>
    private static string? ReferenceOf(JsonElement element, string name, string prefix)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        var id = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => StringOf(value, "id"),
            _ => null,
        };

        return id is { Length: <= BillingPolicy.ExternalReferenceMaxLength } && id.StartsWith(prefix, StringComparison.Ordinal) ? id : null;
    }

    private static string? Metadata(JsonElement data, string key) =>
        data.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            ? StringOf(metadata, key)
            : null;

    /// <summary>A Unix time in seconds, or null when the field is absent, null or not one.</summary>
    private static DateTimeOffset? Seconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var seconds)
        && seconds is >= 0 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static BillingSubscriptionStatus StatusOf(string? status) => status switch
    {
        "incomplete" => BillingSubscriptionStatus.Incomplete,
        "incomplete_expired" => BillingSubscriptionStatus.IncompleteExpired,
        "trialing" => BillingSubscriptionStatus.Trialing,
        "active" => BillingSubscriptionStatus.Active,
        "past_due" => BillingSubscriptionStatus.PastDue,
        "unpaid" => BillingSubscriptionStatus.Unpaid,
        "canceled" => BillingSubscriptionStatus.Canceled,
        "paused" => BillingSubscriptionStatus.Paused,
        _ => BillingSubscriptionStatus.Unspecified,
    };

    private static void RequireReference(string value, string prefix, string name) =>
        RequireReference(value, [prefix], name);

    /// <summary>
    /// An id starting with one of <paramref name="prefixes"/>: several where Stripe has used more than one over the
    /// years, as a dispute's <c>du_</c> and <c>dp_</c>.
    /// </summary>
    private static void RequireReference(string value, string[] prefixes, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (!prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal) && value.Length > prefix.Length)
            || value.Length > BillingPolicy.ExternalReferenceMaxLength
            || !value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
        {
            // Also what keeps a value from reaching a request path as anything but an id.
            throw new ArgumentException($"A Stripe id starting {string.Join(" or ", prefixes)} is required.", name);
        }
    }

    private static void RequireKey(string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > IdempotencyKeyMaxLength)
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }
    }

    // ---- the transport --------------------------------------------------------------------------------------

    /// <summary>One POST, retried while Stripe says that is safe, always with the same key and the same fields.</summary>
    private Task<Result> SendAsync(
        string operation,
        string relativePath,
        IReadOnlyList<KeyValuePair<string, string>> form,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        SendAsync(operation, HttpMethod.Post, relativePath, form, idempotencyKey, cancellationToken);

    /// <summary>One GET (AP.6.2): no body and no key, and safe to repeat on anything that may pass, a 5xx included.</summary>
    private Task<Result> ReadAsync(string operation, string relativePath, CancellationToken cancellationToken) =>
        SendAsync(operation, HttpMethod.Get, relativePath, form: null, idempotencyKey: null, cancellationToken);

    private async Task<Result> SendAsync(
        string operation,
        HttpMethod method,
        string relativePath,
        IReadOnlyList<KeyValuePair<string, string>>? form,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var uri = new Uri(options.Endpoint, relativePath);

        // One bound on the whole call, attempts and waits together, so an outage costs a creator's click the
        // deadline rather than three attempt timeouts end to end.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.TotalTimeout);

        for (var attempt = 1; ; attempt++)
        {
            var reply = await SendOnceAsync(method, uri, form, idempotencyKey, deadline.Token, cancellationToken);

            if (attempt < MaxAttempts && ShouldRetry(reply, read: method == HttpMethod.Get)
                && await WaitToRetryAsync(attempt, deadline.Token, cancellationToken))
            {
                continue;
            }

            var outcome = OutcomeOf(reply.Kind);
            logger.LogInformation(
                "Stripe {Operation} ended {Outcome} (HTTP {Status}, {ErrorType}/{ErrorCode}, request {RequestId}) after {Attempts} attempt(s) in {ElapsedMs} ms.",
                operation,
                outcome,
                reply.Status,
                reply.ErrorType ?? "-",
                reply.ErrorCode ?? "-",
                reply.RequestId ?? "-",
                attempt,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            return new Result(outcome, reply.Data);
        }
    }

    /// <summary>
    /// Stripe's own word first: <c>Stripe-Should-Retry</c> overrides the status either way. Without it, what is
    /// repeated is what cannot have been saved against the key — no answer at all, a 409 because the same key is
    /// still running, a 429 — and never a 5xx, which Stripe has saved and would only replay. A read saves nothing
    /// against any key, so a 5xx is repeated too.
    /// </summary>
    private static bool ShouldRetry(Reply reply, bool read) =>
        reply.Kind is not ReplyKind.CircuitOpen
        && (reply.ShouldRetry
            ?? (reply.Kind is ReplyKind.NoAnswer or ReplyKind.Conflict or ReplyKind.Throttled
                || (read && reply.Kind is ReplyKind.Failed)));

    /// <summary>
    /// Exponential backoff with jitter, as Stripe's rate-limit guidance asks, so callers that failed together do
    /// not retry together. False when the deadline arrives first: the call ends on what it last heard.
    /// </summary>
    private async Task<bool> WaitToRetryAsync(int attempt, CancellationToken deadline, CancellationToken caller)
    {
        var backoff = options.RetryBaseDelay * Math.Pow(2, attempt - 1) * (0.5 + Random.Shared.NextDouble());
        try
        {
            await Task.Delay(backoff < MaxRetryDelay ? backoff : MaxRetryDelay, deadline);
            return true;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return false;
        }
    }

    private static BillingPaymentOutcome OutcomeOf(ReplyKind kind) => kind switch
    {
        ReplyKind.Data => BillingPaymentOutcome.Succeeded,
        ReplyKind.Refused => BillingPaymentOutcome.Refused,
        ReplyKind.CredentialRejected => BillingPaymentOutcome.CredentialRejected,
        ReplyKind.Throttled => BillingPaymentOutcome.Throttled,

        // A 409 that outlived its retries is a key still executing: what it did is not known yet. An open circuit
        // sent nothing, and is unavailable for the same reason a failed attempt is: Stripe is not answering.
        ReplyKind.Conflict or ReplyKind.Failed or ReplyKind.NoAnswer or ReplyKind.CircuitOpen => BillingPaymentOutcome.Unavailable,
        _ => BillingPaymentOutcome.Unreadable,
    };

    /// <param name="deadline">The whole call's bound, already linked to the caller's token.</param>
    /// <param name="cancellationToken">The caller's own, so its cancellation is told apart from a timeout.</param>
    private async Task<Reply> SendOnceAsync(
        HttpMethod method,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>>? form,
        string? idempotencyKey,
        CancellationToken deadline,
        CancellationToken cancellationToken)
    {
        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        attemptTimeout.CancelAfter(options.AttemptTimeout);

        try
        {
            // The key and the version are on the named client's headers, set by the host; nothing here handles them.
            using var request = new HttpRequestMessage(method, uri);
            if (form is not null)
            {
                request.Content = new FormUrlEncodedContent(form);
            }

            if (idempotencyKey is not null)
            {
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptTimeout.Token);
            var status = (int)response.StatusCode;
            var requestId = Token(Header(response, "Request-Id"));
            var shouldRetry = Header(response, "Stripe-Should-Retry") switch
            {
                "true" => true,
                "false" => (bool?)false,
                _ => null,
            };

            using var document = await ReadJsonAsync(response, attemptTimeout.Token);

            if (status is >= 200 and < 300)
            {
                return document is { RootElement.ValueKind: JsonValueKind.Object }
                    ? new Reply(ReplyKind.Data, document.RootElement.Clone(), status, RequestId: requestId)
                    : Note(new Reply(ReplyKind.Unusable, default, status, RequestId: requestId));
            }

            var (errorType, errorCode) = ErrorOf(document);
            var kind = status switch
            {
                (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden => ReplyKind.CredentialRejected,
                (int)HttpStatusCode.Conflict => ReplyKind.Conflict,
                (int)HttpStatusCode.TooManyRequests => ReplyKind.Throttled,
                >= 500 or (int)HttpStatusCode.FailedDependency => ReplyKind.Failed,

                // A redirect is not an answer this contract understands, and the client does not follow one.
                >= 400 => ReplyKind.Refused,
                _ => ReplyKind.Unusable,
            };

            return Note(new Reply(kind, default, status, errorType, errorCode, requestId, shouldRetry));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The attempt's own clock or the call's deadline, not the caller's: Stripe did not answer in time, and
            // may still have acted.
            return Note(new Reply(ReplyKind.NoAnswer, default, Status: 0));
        }
        catch (BrokenCircuitException)
        {
            // The client's breaker is open after repeated failures (StripeBillingRegistration): nothing was sent.
            return Note(new Reply(ReplyKind.CircuitOpen, default, Status: 0));
        }
        catch (HttpRequestException)
        {
            // By type alone. The message can carry the address, and nothing here needs it.
            return Note(new Reply(ReplyKind.NoAnswer, default, Status: 0));
        }
        catch (IOException)
        {
            return Note(new Reply(ReplyKind.NoAnswer, default, Status: 0));
        }
    }

    private Reply Note(Reply reply)
    {
        logger.LogWarning(
            "Stripe request attempt ended {ReplyKind} (HTTP {Status}, {ErrorType}/{ErrorCode}, request {RequestId}).",
            reply.Kind,
            reply.Status,
            reply.ErrorType ?? "-",
            reply.ErrorCode ?? "-",
            reply.RequestId ?? "-");

        return reply;
    }

    /// <summary>
    /// The error's <c>type</c> and <c>code</c>, never its <c>message</c> — which can quote a parameter back — and
    /// only as tokens Stripe's vocabulary is written in.
    /// </summary>
    private static (string? Type, string? Code) ErrorOf(JsonDocument? document)
    {
        if (document is null
            || document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (Token(StringOf(error, "type")), Token(StringOf(error, "code")));
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    /// <summary>A short identifier of letters, digits and underscores, or null — so a log line cannot carry anything else.</summary>
    private static string? Token(string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? value
            : null;

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private enum ReplyKind
    {
        Data,

        /// <summary>No response at all: a dropped connection or an attempt that ran out of time.</summary>
        NoAnswer,
        Refused,
        CredentialRejected,
        Conflict,
        Throttled,

        /// <summary>A 5xx or a 424: Stripe failed, and saved that against the key.</summary>
        Failed,

        /// <summary>The client's circuit breaker is open, so nothing was sent. Not retried within the call.</summary>
        CircuitOpen,
        Unusable,
    }

    private readonly record struct Reply(
        ReplyKind Kind,
        JsonElement Data,
        int Status,
        string? ErrorType = null,
        string? ErrorCode = null,
        string? RequestId = null,
        bool? ShouldRetry = null);

    private readonly record struct Result(BillingPaymentOutcome Outcome, JsonElement Data);
}

/// <summary>
/// The metadata every Stripe object this adapter creates carries, so a webhook about it names the account and the
/// purchase without a lookup (AP.6.2, AP.6.3). Prefixed, because a Stripe account can be shared with other tools.
/// </summary>
internal static class StripeMetadata
{
    public const string AccountId = "creatorpantry_account_id";

    /// <summary><see cref="SubscriptionPurchase"/> or <see cref="PackPurchase"/>.</summary>
    public const string Purchase = "creatorpantry_purchase";

    public const string PlanKey = "creatorpantry_plan_key";

    /// <summary><c>monthly</c> or <c>annual</c>.</summary>
    public const string BillingCycle = "creatorpantry_billing_cycle";

    public const string PackKey = "creatorpantry_pack_key";

    public const string SubscriptionPurchase = "subscription";

    public const string PackPurchase = "pack";
}
