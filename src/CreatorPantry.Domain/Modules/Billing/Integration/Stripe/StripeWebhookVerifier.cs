using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Integration.Stripe;

/// <summary>
/// Stripe's webhook signature, checked by hand over the raw body (PRICE-010, AP.6.2), and the few fields of a
/// verified event the application keeps.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The scheme</strong> (docs.stripe.com/webhooks, "Verify webhook signatures manually", read 2026-10-28):
/// <c>Stripe-Signature: t=…,v1=…[,v1=…][,v0=…]</c>; the signed payload is the timestamp, a full stop and the body
/// exactly as sent; each <c>v1</c> is the hex HMAC-SHA256 of that under the endpoint's signing secret. Only
/// <c>v1</c> counts — anything else is ignored, against a downgrade — and any one <c>v1</c> may match, because while
/// a secret is being rolled Stripe signs with each one still active. Compared in constant time.
/// </para>
/// <para>
/// <strong>Recency</strong>: the timestamp is signed, so it cannot be moved, and one more than
/// <see cref="BillingWebhookPolicy.SignatureTolerance"/> from this host's clock either way is refused — Stripe's
/// libraries' five minutes. A captured delivery replayed inside that window verifies, and is then a replay of a
/// stored event id, which changes nothing.
/// </para>
/// <para>
/// <strong>Nothing in the body is read until the signature holds.</strong> Then only the event's id, type, mode and
/// time, and which object and subscription it is about: never its amounts, its customer's details or anything the
/// creator entered, since none of it is acted on — processing reads the subscription again.
/// </para>
/// <para>
/// Shapes are the pinned version's (<c>2026-09-30.endive</c>, as the webhook endpoint is created at): an invoice
/// names its subscription under <c>parent.subscription_details.subscription</c>, not at the top level as it did
/// before <c>2025-03-31.basil</c>.
/// </para>
/// </remarks>
internal sealed class StripeWebhookVerifier(string signingSecret, IClock clock) : IBillingWebhookVerifier
{
    /// <summary>Far more than a real header, which is a timestamp and one or two signatures.</summary>
    private const int MaxHeaderLength = 4096;

    /// <summary>A SHA-256 HMAC, as hex.</summary>
    private const int SignatureHexLength = 64;

    /// <summary>The last second <see cref="DateTimeOffset"/> can hold: a later one is not a time, and would throw.</summary>
    private const long MaxUnixSeconds = 253402300799;

    private readonly byte[] _key = Encoding.UTF8.GetBytes(signingSecret);

    public BillingWebhookVerificationResult Verify(string? signatureHeader, ReadOnlySpan<byte> body)
    {
        if (!TryReadHeader(signatureHeader, out var timestamp, out var signatures))
        {
            return BillingWebhookVerificationResult.Of(BillingWebhookVerification.SignatureInvalid);
        }

        var age = clock.UtcNow - DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if (age.Duration() > BillingWebhookPolicy.SignatureTolerance)
        {
            return BillingWebhookVerificationResult.Of(BillingWebhookVerification.SignatureInvalid);
        }

        var expected = Expected(timestamp, body);
        if (!signatures.Any(signature => CryptographicOperations.FixedTimeEquals(signature, expected)))
        {
            return BillingWebhookVerificationResult.Of(BillingWebhookVerification.SignatureInvalid);
        }

        return ReadNotice(body) is { } notice
            ? new BillingWebhookVerificationResult(BillingWebhookVerification.Verified, notice)
            : BillingWebhookVerificationResult.Of(BillingWebhookVerification.Unreadable);
    }

    /// <summary>HMAC-SHA256 of <c>{t}.{body}</c> under the signing secret.</summary>
    private byte[] Expected(long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefix.Length));

        return HMACSHA256.HashData(_key, payload);
    }

    /// <summary>
    /// The timestamp and every <c>v1</c> signature. False for no header, an overlong one, no timestamp or a malformed
    /// one, or no <c>v1</c> that is 64 hex characters.
    /// </summary>
    private static bool TryReadHeader(string? header, out long timestamp, out List<byte[]> signatures)
    {
        timestamp = 0;
        signatures = [];

        if (string.IsNullOrWhiteSpace(header) || header.Length > MaxHeaderLength)
        {
            return false;
        }

        var sawTimestamp = false;

        foreach (var element in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var at = element.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0)
            {
                continue;
            }

            var name = element[..at];
            var value = element[(at + 1)..];

            if (name == "t")
            {
                // One timestamp: two would leave which was signed to whoever reads the header.
                if (sawTimestamp
                    || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp)
                    || timestamp > MaxUnixSeconds)
                {
                    return false;
                }

                sawTimestamp = true;
            }
            else if (name == "v1" && value.Length == SignatureHexLength && value.All(char.IsAsciiHexDigit))
            {
                signatures.Add(Convert.FromHexString(value));
            }
        }

        return sawTimestamp && signatures.Count > 0;
    }

    /// <summary>The event's identity and what it is about; null when the body is not an event this can read.</summary>
    private static BillingProviderEventNotice? ReadNotice(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            var reader = new Utf8JsonReader(body);
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || Id(root, "evt_") is not { } eventId
                || Text(root, "type") is not { Length: > 0 and <= BillingWebhookPolicy.EventTypeMaxLength } type
                || !root.TryGetProperty("livemode", out var live) || live.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("created", out var created) || !created.TryGetInt64(out var createdSeconds)
                || createdSeconds is < 0 or > MaxUnixSeconds
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("object", out var item) || item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var objectReference = Id(item, prefix: null);
            var (kind, subscription) = type switch
            {
                "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted"
                    => (BillingEventKind.SubscriptionChanged, Id(item, "sub_")),

                "invoice.paid" => (BillingEventKind.InvoicePaid, InvoiceSubscription(item)),

                "invoice.payment_failed" or "invoice.payment_action_required"
                    => (BillingEventKind.InvoicePaymentFailed, InvoiceSubscription(item)),

                // A subscription checkout names the subscription it made; a pack's names none and is AP.6.3's.
                "checkout.session.completed" or "checkout.session.async_payment_succeeded" or "checkout.session.async_payment_failed"
                    => (BillingEventKind.CheckoutCompleted, Text(item, "mode") == "subscription" ? Reference(item, "subscription", "sub_") : null),

                "charge.refunded" or "charge.dispute.created" => (BillingEventKind.ChargeReversed, (string?)null),

                _ => (BillingEventKind.Other, (string?)null),
            };

            return new BillingProviderEventNotice(
                eventId,
                type,
                kind,
                live.GetBoolean(),
                DateTimeOffset.FromUnixTimeSeconds(createdSeconds),
                objectReference,
                subscription);
        }
    }

    /// <summary>An invoice's subscription, at the pinned version: under <c>parent.subscription_details</c>.</summary>
    private static string? InvoiceSubscription(JsonElement invoice) =>
        invoice.TryGetProperty("parent", out var parent)
        && parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty("subscription_details", out var details)
        && details.ValueKind == JsonValueKind.Object
            ? Reference(details, "subscription", "sub_")
            : null;

    /// <summary>A field naming another object: its id as a string, or the object expanded with its id inside.</summary>
    private static string? Reference(JsonElement element, string name, string prefix) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => Checked(value.GetString(), prefix),
                JsonValueKind.Object => Id(value, prefix),
                _ => null,
            }
            : null;

    private static string? Id(JsonElement element, string? prefix) => Checked(Text(element, "id"), prefix);

    /// <summary>
    /// An id worth storing: the expected prefix, within the column, and made of the characters Stripe's ids are — so a
    /// stored reference is always an opaque token and never text somebody chose.
    /// </summary>
    private static string? Checked(string? value, string? prefix) =>
        value is { Length: > 0 and <= BillingPolicy.ExternalReferenceMaxLength }
        && (prefix is null || value.StartsWith(prefix, StringComparison.Ordinal))
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            ? value
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
