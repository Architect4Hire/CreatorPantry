using System.Globalization;
using System.Security.Claims;

namespace CreatorPantry.Gateway.Sessions;

/// <summary>BFF session bounds (baseline B-02/B-13). Configured under <c>Session</c>.</summary>
public sealed class GatewaySessionOptions
{
    public const string SectionName = "Session";

    /// <summary><c>__Host-</c> prefix: Secure, Path=/, and no Domain are enforced by browsers.</summary>
    public const string CookieName = "__Host-creatorpantry-session";

    public const string AntiforgeryCookieName = "__Host-creatorpantry-xsrf";
    public const string AntiforgeryHeaderName = "X-XSRF-TOKEN";

    /// <summary>Sliding inactivity timeout.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Forced re-authentication regardless of activity.</summary>
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How often the session's security stamp is re-checked with the API.</summary>
    public TimeSpan RevalidationInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a session may go unvalidated while the API is unreachable before it is ended, so an outage
    /// or misconfiguration cannot silently disable stamp-based revocation.
    /// </summary>
    public TimeSpan MaxValidationStaleness { get; set; } = TimeSpan.FromMinutes(15);

    public const string ValidationMessage =
        "Session options must satisfy 0 <= RevalidationInterval <= MaxValidationStaleness < IdleTimeout <= AbsoluteLifetime <= 30 days.";

    /// <summary>
    /// Bounds that keep revocation effective: revalidation happens well within the idle window, and a session
    /// cannot outlive its absolute lifetime or stay unvalidated indefinitely.
    /// </summary>
    public static bool IsValid(GatewaySessionOptions options) =>
        options.RevalidationInterval >= TimeSpan.Zero
        && options.RevalidationInterval <= options.MaxValidationStaleness
        && options.MaxValidationStaleness < options.IdleTimeout
        && options.IdleTimeout > TimeSpan.Zero
        && options.IdleTimeout <= options.AbsoluteLifetime
        && options.AbsoluteLifetime <= TimeSpan.FromDays(30);
}

/// <summary>Claims held in the server-side session ticket. None of these reach the browser.</summary>
internal static class SessionClaims
{
    public const string AuthenticationType = "bff-session";
    public const string SessionId = "sid";
    public const string SecurityStamp = "stamp";
    public const string AuthenticatedAt = "auth_time";
    public const string ValidatedAt = "validated_at";

    public static ClaimsPrincipal Create(
        SessionUser user, string sessionId, DateTimeOffset authenticatedAt, DateTimeOffset validatedAt)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId),
            new(ClaimTypes.Name, user.DisplayName),
            new(SessionId, sessionId),
            new(SecurityStamp, user.SecurityStamp),
            new(AuthenticatedAt, authenticatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new(ValidatedAt, validatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        };
        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static DateTimeOffset? ReadTime(ClaimsPrincipal principal, string claimType) =>
        long.TryParse(principal.FindFirst(claimType)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
