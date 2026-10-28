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
/// <c>POST billing_portal/sessions</c>, form-encoded, authenticated by a bearer key. Plain HTTP rather than
/// Stripe.net, as the repository's other providers are: four calls do not need an SDK, the pin is one header here
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

    // ---- the transport --------------------------------------------------------------------------------------

    /// <summary>One POST, retried while Stripe says that is safe, always with the same key and the same fields.</summary>
    private async Task<Result> SendAsync(
        string operation,
        string relativePath,
        IReadOnlyList<KeyValuePair<string, string>> form,
        string idempotencyKey,
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
            var reply = await SendOnceAsync(uri, form, idempotencyKey, deadline.Token, cancellationToken);

            if (attempt < MaxAttempts && ShouldRetry(reply) && await WaitToRetryAsync(attempt, deadline.Token, cancellationToken))
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
    /// still running, a 429 — and never a 5xx, which Stripe has saved and would only replay.
    /// </summary>
    private static bool ShouldRetry(Reply reply) =>
        reply.Kind is not ReplyKind.CircuitOpen
        && (reply.ShouldRetry ?? reply.Kind is ReplyKind.NoAnswer or ReplyKind.Conflict or ReplyKind.Throttled);

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
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> form,
        string idempotencyKey,
        CancellationToken deadline,
        CancellationToken cancellationToken)
    {
        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        attemptTimeout.CancelAfter(options.AttemptTimeout);

        try
        {
            // The key and the version are on the named client's headers, set by the host; nothing here handles them.
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
            request.Headers.Add("Idempotency-Key", idempotencyKey);

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
