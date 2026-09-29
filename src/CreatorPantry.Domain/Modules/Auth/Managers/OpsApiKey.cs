using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace CreatorPantry.Domain.Modules.Auth.Managers;

/// <summary>Field limits and key sizes for <c>OpsApiClient</c>, shared by EF configuration and callers.</summary>
public static class OpsApiKeyPolicy
{
    public const int NameMaxLength = 200;
    public const int PrefixMaxLength = 32;
    public const int ScopesMaxLength = 500;

    /// <summary>SHA-256.</summary>
    public const int HashLength = 32;

    public const int SaltLength = 16;

    /// <summary>Bytes of entropy in the secret segment.</summary>
    public const int SecretLength = 32;

    /// <summary>
    /// Marks a credential as one of ours, so a key pasted into the wrong field is recognisable on sight and
    /// secret scanners have something to match.
    /// </summary>
    public const string Prefix = "cpops_";

    /// <summary>The authentication scheme name and the <c>Authorization</c> scheme token.</summary>
    public const string Scheme = "OpsApiKey";
}

/// <summary>
/// A presented ops API key, split into the part that identifies a client and the part that proves it.
/// </summary>
/// <param name="Prefix">Public. Indexed, so verification is one seek.</param>
/// <param name="Secret">The 32 random bytes the hash is computed over. Never stored, never logged.</param>
public sealed record OpsApiKeyParts(string Prefix, byte[] Secret);

/// <summary>
/// Issues and verifies ops API keys (baseline B-14): <c>cpops_&lt;prefix&gt;.&lt;secret&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why not a password hash.</strong> The secret is <see cref="OpsApiKeyPolicy.SecretLength"/> bytes
/// from a cryptographic RNG, so there is no guessable password to slow an attacker down by — and this runs on
/// every ops request, where PBKDF2's whole purpose would be to make the API slow. A per-client salt is still
/// stored, so identical keys could not produce identical hashes.
/// </para>
/// <para>
/// <strong>Comparison is constant-time.</strong> A byte-by-byte compare that returns early would leak the
/// hash one byte at a time to a caller willing to measure.
/// </para>
/// </remarks>
public static class OpsApiKeyHasher
{
    /// <summary>Generates a new key: the string to hand to the operator, plus the salt and hash to store.</summary>
    public static (string Key, string Prefix, byte[] Salt, byte[] Hash) Issue()
    {
        var prefix = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(8));
        var secret = RandomNumberGenerator.GetBytes(OpsApiKeyPolicy.SecretLength);
        var salt = RandomNumberGenerator.GetBytes(OpsApiKeyPolicy.SaltLength);

        return (
            $"{OpsApiKeyPolicy.Prefix}{prefix}.{Base64Url.EncodeToString(secret)}",
            prefix,
            salt,
            Hash(salt, secret));
    }

    /// <summary>
    /// Splits a presented credential, or null when it is not one of ours. Malformed input is a miss, never
    /// an exception: the value came off the wire.
    /// </summary>
    public static OpsApiKeyParts? Parse(string? credential)
    {
        if (string.IsNullOrWhiteSpace(credential) || !credential.StartsWith(OpsApiKeyPolicy.Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var body = credential[OpsApiKeyPolicy.Prefix.Length..];
        var separator = body.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator == body.Length - 1)
        {
            return null;
        }

        var prefix = body[..separator];
        if (prefix.Length > OpsApiKeyPolicy.PrefixMaxLength)
        {
            return null;
        }

        try
        {
            return new OpsApiKeyParts(prefix, Base64Url.DecodeFromChars(body.AsSpan(separator + 1)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>SHA-256 over the salt followed by the secret.</summary>
    public static byte[] Hash(byte[] salt, byte[] secret) => SHA256.HashData([.. salt, .. secret]);

    /// <summary>Constant-time verification of a presented secret against a stored salt and hash.</summary>
    public static bool Verify(byte[] salt, byte[] expectedHash, byte[] presentedSecret) =>
        CryptographicOperations.FixedTimeEquals(Hash(salt, presentedSecret), expectedHash);

    /// <summary>
    /// Derives the salt and hash for a key supplied through the secret store, so a deployment can name its own
    /// key rather than being handed a generated one.
    /// </summary>
    /// <remarks>
    /// The salt is derived from the key rather than random, so re-running the seeder with an unchanged key
    /// produces an unchanged hash — which is how the seeder tells "same key, nothing to do" from "rotated".
    /// </remarks>
    public static (string Prefix, byte[] Salt, byte[] Hash)? Derive(string credential)
    {
        var parts = Parse(credential);
        if (parts is null)
        {
            return null;
        }

        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(parts.Prefix))[..OpsApiKeyPolicy.SaltLength];

        return (parts.Prefix, salt, Hash(salt, parts.Secret));
    }
}
