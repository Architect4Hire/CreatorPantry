using System.Security.Cryptography;
using CreatorPantry.Domain.Time;
using CreatorPantry.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CreatorPantry.ApiService.Authorization;

/// <summary>API settings for validating gateway-minted internal tokens (section <c>InternalToken</c>).</summary>
public sealed class InternalTokenValidationOptions
{
    /// <summary>SubjectPublicKeyInfo PEM of the gateway's ECDSA P-256 signing key. Public; not a secret.</summary>
    public string PublicKeyPem { get; set; } = string.Empty;
}

/// <summary>
/// Accepts only the gateway's internal token (baseline B-13): ES256, fixed issuer and audience, a lifetime
/// of at most <see cref="InternalTokenDefaults.MaxLifetime"/>, signed by the gateway's key.
/// </summary>
public static class InternalTokenAuthentication
{
    public static IServiceCollection AddInternalTokenAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<InternalTokenValidationOptions>()
            .Bind(configuration.GetSection(InternalTokenDefaults.ConfigurationSection))
            .Validate(options => TryImportPublicKey(options.PublicKeyPem, out _),
                "InternalToken:PublicKeyPem must be the gateway's ECDSA P-256 public key.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<InternalTokenValidationOptions>, IClock>((bearer, keyOptions, clock) =>
            {
                TryImportPublicKey(keyOptions.Value.PublicKeyPem, out var key);

                bearer.MapInboundClaims = false;
                bearer.RequireHttpsMetadata = false; // no metadata endpoint: the key is configured locally
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = InternalTokenDefaults.Issuer,
                    ValidAudience = InternalTokenDefaults.Audience,
                    IssuerSigningKey = key,
                    ValidAlgorithms = [InternalTokenDefaults.Algorithm],
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    LifetimeValidator = (notBefore, expires, _, parameters) =>
                        ValidateLifetime(notBefore, expires, parameters, clock.UtcNow.UtcDateTime),
                    ClockSkew = InternalTokenDefaults.ClockSkew,
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = InternalTokenDefaults.RoleClaim,
                };
            });

        return services;
    }

    /// <summary>Standard lifetime checks plus a cap, so a leaked signing key cannot mint long-lived tokens.</summary>
    private static bool ValidateLifetime(
        DateTime? notBefore, DateTime? expires, TokenValidationParameters parameters, DateTime now)
    {
        if (notBefore is null || expires is null || expires - notBefore > InternalTokenDefaults.MaxLifetime)
        {
            return false;
        }

        return notBefore.Value - parameters.ClockSkew <= now && now <= expires.Value + parameters.ClockSkew;
    }

    private static bool TryImportPublicKey(string pem, out ECDsaSecurityKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(pem))
        {
            return false;
        }

        try
        {
            // Only a public key is acceptable: signing material must never be configured on the API.
            if (!pem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
            {
                return false;
            }

            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            if (ecdsa.KeySize != 256)
            {
                return false;
            }

            key = new ECDsaSecurityKey(ecdsa);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            return false;
        }
    }
}
