using System.Security.Cryptography;
using CreatorPantry.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Gateway.InternalTokens;

/// <summary>Gateway settings for minting internal tokens (section <c>InternalToken</c>).</summary>
public sealed class InternalTokenOptions
{
    /// <summary>PKCS#8 PEM ECDSA P-256 private key. A secret: supplied by parameter or secret store only.</summary>
    public string SigningKeyPem { get; set; } = string.Empty;

    public TimeSpan Lifetime { get; set; } = InternalTokenDefaults.DefaultLifetime;
}

internal sealed class InternalTokenOptionsValidator : IValidateOptions<InternalTokenOptions>
{
    public ValidateOptionsResult Validate(string? name, InternalTokenOptions options)
    {
        var failures = new List<string>();

        if (options.Lifetime <= TimeSpan.Zero || options.Lifetime > InternalTokenDefaults.MaxLifetime)
        {
            failures.Add($"InternalToken:Lifetime must be positive and at most {InternalTokenDefaults.MaxLifetime}.");
        }

        if (string.IsNullOrWhiteSpace(options.SigningKeyPem))
        {
            failures.Add("InternalToken:SigningKeyPem is required.");
        }
        else
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportFromPem(options.SigningKeyPem);
                if (key.KeySize != 256)
                {
                    failures.Add("InternalToken:SigningKeyPem must be an ECDSA P-256 key.");
                }
                else
                {
                    _ = key.ExportParameters(includePrivateParameters: true); // throws for a public-only key
                }
            }
            catch (Exception exception) when (exception is ArgumentException or CryptographicException)
            {
                // Never echo key material into the failure message.
                failures.Add("InternalToken:SigningKeyPem is not a valid ECDSA P-256 private key.");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
