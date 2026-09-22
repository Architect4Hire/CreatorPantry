namespace CreatorPantry.ServiceDefaults;

/// <summary>
/// The gateway-to-API internal token contract (baseline B-13), shared by the gateway (issuer) and the API
/// (validator). Keys are configuration, never constants.
/// </summary>
public static class InternalTokenDefaults
{
    public const string ConfigurationSection = "InternalToken";

    public const string Issuer = "creatorpantry-gateway";
    public const string Audience = "creatorpantry-api";

    /// <summary>ECDSA P-256 with SHA-256; the only accepted algorithm.</summary>
    public const string Algorithm = "ES256";

    public const string SessionIdClaim = "sid";
    public const string RoleClaim = "role";

    /// <summary>Distinguishes a browser session's token from the gateway's own service token.</summary>
    public const string TokenUseClaim = "token_use";
    public const string UserTokenUse = "user";
    public const string ServiceTokenUse = "service";

    /// <summary>The subject of gateway service tokens (credential checks, session revalidation).</summary>
    public const string GatewayServiceSubject = "creatorpantry-gateway";

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Allowed clock difference between gateway and API instances.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(15);
}
