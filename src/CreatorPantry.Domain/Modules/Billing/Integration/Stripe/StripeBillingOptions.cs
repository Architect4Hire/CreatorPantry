using CreatorPantry.Domain.Modules.Billing.Gateways;

namespace CreatorPantry.Domain.Modules.Billing.Integration.Stripe;

/// <summary>
/// What a host needs to call Stripe, from one connection string (AP.6.1).
/// </summary>
/// <param name="Endpoint">The API's base address, always ending in a slash so relative paths resolve beneath it.</param>
/// <param name="SecretKey">
/// A secret or restricted key. A credential: never logged, never returned, never in a URL. Its prefix says which of
/// Stripe's modes it acts in, so <see cref="Mode"/> is read from it rather than configured beside it.
/// </param>
/// <param name="WebhookSecret">
/// The webhook endpoint's signing secret (<c>whsec_…</c>), when one was supplied. AP.6.2 verifies with it; nothing
/// here sends it anywhere. A credential like the key.
/// </param>
internal sealed record StripeBillingOptions(Uri Endpoint, string SecretKey, string? WebhookSecret)
{
    /// <summary>The connection string's name, and the named <c>HttpClient</c>'s.</summary>
    public const string ConnectionName = "billing-stripe";

    /// <summary>
    /// The API version every request is pinned to, sent as <c>Stripe-Version</c>. It fixes the shape of what is
    /// sent and read here; the webhook endpoint is created at the same version so its events match (AP.6.1
    /// decision). Changing it is a reviewed change to this adapter and its fixtures, never configuration.
    /// </summary>
    public const string ApiVersion = "2026-09-30.endive";

    public BillingPaymentMode Mode { get; } = ModeOf(SecretKey);

    /// <summary>How long one attempt may take before it is abandoned. Stripe answers well inside this.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long one call may take, every attempt and wait included: what a creator's click costs while Stripe is not
    /// answering, rather than three attempt timeouts end to end.
    /// </summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>The first wait between attempts; each later one doubles it.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Printed nowhere on purpose, but if it ever is, it is not with either secret in it.</summary>
    public override string ToString() => $"StripeBillingOptions {{ Endpoint = {Endpoint}, Mode = {Mode} }}";

    /// <summary>
    /// Reads <c>Endpoint=…;Key=…</c>, with an optional <c>WebhookSecret=…</c>. Null when the value is absent or the
    /// endpoint or key is missing or unusable — including a key whose prefix names neither of Stripe's modes: a
    /// half-configured provider is an unconfigured one, never a host that will not start.
    /// </summary>
    public static StripeBillingOptions? Parse(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
            {
                parts[part[..at].Trim()] = part[(at + 1)..].Trim();
            }
        }

        if (!parts.TryGetValue("Endpoint", out var endpoint)
            || !parts.TryGetValue("Key", out var key)
            || ModeOf(key) == BillingPaymentMode.NotConfigured
            || !Uri.TryCreate(endpoint.EndsWith('/') ? endpoint : endpoint + "/", UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.Query))
        {
            return null;
        }

        // Optional, so a host can make checkouts before AP.6.2 can hear about them. A value that is not a signing
        // secret is dropped rather than kept to fail every verification later.
        var webhookSecret = parts.TryGetValue("WebhookSecret", out var secret)
            && secret.StartsWith("whsec_", StringComparison.Ordinal)
            && secret.Length > "whsec_".Length
                ? secret
                : null;

        return new StripeBillingOptions(uri, key, webhookSecret);
    }

    /// <summary>
    /// A secret key (<c>sk_</c>) or a restricted one (<c>rk_</c>, recommended: it can be limited to the calls this
    /// adapter makes), in test or live mode. A publishable key (<c>pk_</c>) cannot make these calls and is not one.
    /// </summary>
    private static BillingPaymentMode ModeOf(string key) =>
        key.Length <= "sk_test_".Length ? BillingPaymentMode.NotConfigured
        : key.StartsWith("sk_test_", StringComparison.Ordinal) || key.StartsWith("rk_test_", StringComparison.Ordinal)
            ? BillingPaymentMode.Test
        : key.StartsWith("sk_live_", StringComparison.Ordinal) || key.StartsWith("rk_live_", StringComparison.Ordinal)
            ? BillingPaymentMode.Live
        : BillingPaymentMode.NotConfigured;
}
