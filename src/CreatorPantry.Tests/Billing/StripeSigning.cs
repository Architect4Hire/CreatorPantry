using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// Signs a webhook body the way Stripe documents it (docs.stripe.com/webhooks, "Verify webhook signatures manually"),
/// and loads the embedded Stripe fixtures, for the tests that need a delivery Stripe would have made (AP.6.2).
/// </summary>
/// <remarks>
/// Written from the documentation rather than from the verifier, so a verifier that drifted from Stripe's scheme
/// would fail against these signatures instead of agreeing with itself.
/// </remarks>
internal static class StripeSigning
{
    /// <summary>A made-up signing secret, recognisable so a leak assertion can look for it.</summary>
    public const string Secret = "whsec_TEST_do_not_log_0a1b2c3d4e5f";

    /// <summary><c>t={timestamp},v1={hex HMAC-SHA256 of "{timestamp}.{body}"}</c>.</summary>
    public static string Header(string body, DateTimeOffset at, string secret = Secret) =>
        $"t={Seconds(at)},v1={V1(body, at, secret)}";

    /// <summary>The hex <c>v1</c> signature alone.</summary>
    public static string V1(string body, DateTimeOffset at, string secret = Secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{Seconds(at)}.{body}")));

    public static string Seconds(DateTimeOffset at) => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>An embedded <c>*.stripe.json</c> fixture by name (see <c>Fixtures/Stripe/README.md</c>).</summary>
    public static string Fixture(string name)
    {
        var assembly = typeof(StripeSigning).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(candidate => candidate.EndsWith($".{name}.stripe.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
